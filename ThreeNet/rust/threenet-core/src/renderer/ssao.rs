//! Screen space ambient occlusion.
//!
//! 1. A full resolution prepass writes view space normals and linear depth.
//! 2. The occlusion pass samples a hemisphere kernel around every pixel at half
//!    resolution, rotated by a tiled 4x4 noise texture.
//! 3. Two bilateral blur passes (horizontal then vertical) remove the noise
//!    pattern without bleeding occlusion across depth edges.
//!
//! The scene shader samples the result and applies it to ambient and image
//! based lighting (and, scaled down, to direct lighting).

use wgpu::util::DeviceExt;

use crate::geometry::{Topology, Vertex};
use crate::math::Mat4;
use crate::renderer::pipeline::DEPTH_FORMAT;
use crate::renderer::resources::ResourceCache;
use crate::renderer::uniforms::{BlurUniform, FrameUniform, MAX_SSAO_SAMPLES, SsaoUniform};
use crate::scene::GeometryId;

pub const GBUFFER_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;
pub const AO_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::R8Unorm;
const NOISE_SIZE: u32 = 4;

/// User facing SSAO settings, taken from the renderer configuration.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SsaoSettings {
    pub enabled: bool,
    /// Sampling radius in world units.
    pub radius: f32,
    /// Depth bias that prevents self occlusion on flat surfaces.
    pub bias: f32,
    /// Contrast exponent applied to the occlusion term.
    pub intensity: f32,
    /// Kernel samples per pixel (4-32).
    pub samples: u32,
    /// How much AO darkens direct lighting (0 = ambient only, 1 = everything).
    pub direct_strength: f32,
}

impl Default for SsaoSettings {
    fn default() -> Self {
        Self {
            enabled: false,
            radius: 0.5,
            bias: 0.025,
            intensity: 1.5,
            samples: 16,
            direct_strength: 0.25,
        }
    }
}

/// An opaque mesh drawn into the normal/depth prepass.
#[derive(Debug, Clone, Copy)]
pub struct GBufferItem {
    pub geometry: GeometryId,
    pub object_offset: u32,
}

struct SizedTargets {
    width: u32,
    height: u32,
    gbuffer: wgpu::TextureView,
    depth: wgpu::TextureView,
    ao: wgpu::TextureView,
    ao_scratch: wgpu::TextureView,
    occlusion_bind_group: wgpu::BindGroup,
    blur_horizontal_bind_group: wgpu::BindGroup,
    blur_vertical_bind_group: wgpu::BindGroup,
}

pub struct Ssao {
    gbuffer_pipeline: wgpu::RenderPipeline,
    gbuffer_frame_layout: wgpu::BindGroupLayout,
    gbuffer_frame_bind_group: Option<wgpu::BindGroup>,
    occlusion_pipeline: wgpu::RenderPipeline,
    occlusion_layout: wgpu::BindGroupLayout,
    blur_pipeline: wgpu::RenderPipeline,
    blur_layout: wgpu::BindGroupLayout,
    ssao_buffer: wgpu::Buffer,
    blur_horizontal_buffer: wgpu::Buffer,
    blur_vertical_buffer: wgpu::Buffer,
    noise_view: wgpu::TextureView,
    point_sampler: wgpu::Sampler,
    repeat_sampler: wgpu::Sampler,
    linear_sampler: wgpu::Sampler,
    kernel: [[f32; 4]; MAX_SSAO_SAMPLES],
    targets: Option<SizedTargets>,
    /// False when only the normal/depth prepass is wanted (depth of field,
    /// motion blur) and the occlusion passes are skipped.
    occlusion: bool,
    generation: u64,
}

impl std::fmt::Debug for Ssao {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Ssao")
            .field("size", &self.targets.as_ref().map(|t| (t.width, t.height)))
            .finish()
    }
}

impl Ssao {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        object_layout: &wgpu::BindGroupLayout,
    ) -> Self {
        let texture_entry = |binding: u32, filterable: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let sampler_entry =
            |binding: u32, kind: wgpu::SamplerBindingType| wgpu::BindGroupLayoutEntry {
                binding,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Sampler(kind),
                count: None,
            };
        let uniform_entry = |binding: u32, visibility: wgpu::ShaderStages, size: usize| {
            wgpu::BindGroupLayoutEntry {
                binding,
                visibility,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: wgpu::BufferSize::new(size as u64),
                },
                count: None,
            }
        };

        // ------------------------------------------------------------ prepass
        let gbuffer_frame_layout =
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("threenet.layout.gbuffer_frame"),
                entries: &[uniform_entry(
                    0,
                    wgpu::ShaderStages::VERTEX,
                    size_of::<FrameUniform>(),
                )],
            });
        let gbuffer_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.gbuffer"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/gbuffer.wgsl").into()),
        });
        let gbuffer_pipeline_layout =
            device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("threenet.pipeline_layout.gbuffer"),
                bind_group_layouts: &[Some(&gbuffer_frame_layout), Some(object_layout)],
                immediate_size: 0,
            });
        let gbuffer_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("threenet.pipeline.gbuffer"),
            layout: Some(&gbuffer_pipeline_layout),
            vertex: wgpu::VertexState {
                module: &gbuffer_shader,
                entry_point: Some("vs_gbuffer"),
                buffers: &[Some(Vertex::LAYOUT)],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &gbuffer_shader,
                entry_point: Some("fs_gbuffer"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: GBUFFER_FORMAT,
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleList,
                // Double sided: the shader flips back facing normals.
                cull_mode: None,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format: DEPTH_FORMAT,
                depth_write_enabled: Some(true),
                depth_compare: Some(wgpu::CompareFunction::LessEqual),
                stencil: wgpu::StencilState::default(),
                bias: wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });

        // ---------------------------------------------------------- occlusion
        let occlusion_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.ssao"),
            entries: &[
                texture_entry(0, false),
                sampler_entry(1, wgpu::SamplerBindingType::NonFiltering),
                texture_entry(2, false),
                sampler_entry(3, wgpu::SamplerBindingType::NonFiltering),
                uniform_entry(4, wgpu::ShaderStages::FRAGMENT, size_of::<SsaoUniform>()),
            ],
        });
        let occlusion_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.ssao"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/ssao.wgsl").into()),
        });
        let occlusion_pipeline = fullscreen_pipeline(
            device,
            &occlusion_layout,
            &occlusion_shader,
            "fs_ssao",
            "threenet.pipeline.ssao",
        );

        // --------------------------------------------------------------- blur
        let blur_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.ssao_blur"),
            entries: &[
                texture_entry(0, true),
                sampler_entry(1, wgpu::SamplerBindingType::Filtering),
                texture_entry(2, true),
                uniform_entry(3, wgpu::ShaderStages::FRAGMENT, size_of::<BlurUniform>()),
            ],
        });
        let blur_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.ssao_blur"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/ssao_blur.wgsl").into()),
        });
        let blur_pipeline = fullscreen_pipeline(
            device,
            &blur_layout,
            &blur_shader,
            "fs_blur",
            "threenet.pipeline.ssao_blur",
        );

        let kernel = generate_kernel(0x9E37_79B9);
        let ssao_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("threenet.ssao.uniform"),
            contents: bytemuck::bytes_of(&SsaoUniform::zeroed_with_kernel(kernel)),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let blur_buffer = |label| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents: bytemuck::bytes_of(&BlurUniform::default()),
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            })
        };
        let blur_horizontal_buffer = blur_buffer("threenet.ssao.blur_h");
        let blur_vertical_buffer = blur_buffer("threenet.ssao.blur_v");

        let noise_view = device
            .create_texture_with_data(
                queue,
                &wgpu::TextureDescriptor {
                    label: Some("threenet.ssao.noise"),
                    size: wgpu::Extent3d {
                        width: NOISE_SIZE,
                        height: NOISE_SIZE,
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format: wgpu::TextureFormat::Rgba8Unorm,
                    usage: wgpu::TextureUsages::TEXTURE_BINDING,
                    view_formats: &[],
                },
                wgpu::util::TextureDataOrder::default(),
                &generate_noise(0xA0_5EED),
            )
            .create_view(&wgpu::TextureViewDescriptor::default());

        let sampler = |label, address_mode, filter| {
            device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some(label),
                address_mode_u: address_mode,
                address_mode_v: address_mode,
                address_mode_w: address_mode,
                mag_filter: filter,
                min_filter: filter,
                ..Default::default()
            })
        };
        let point_sampler = sampler(
            "threenet.ssao.point",
            wgpu::AddressMode::ClampToEdge,
            wgpu::FilterMode::Nearest,
        );
        let repeat_sampler = sampler(
            "threenet.ssao.repeat",
            wgpu::AddressMode::Repeat,
            wgpu::FilterMode::Nearest,
        );
        let linear_sampler = sampler(
            "threenet.ssao.linear",
            wgpu::AddressMode::ClampToEdge,
            wgpu::FilterMode::Linear,
        );

        Self {
            gbuffer_pipeline,
            gbuffer_frame_layout,
            gbuffer_frame_bind_group: None,
            occlusion_pipeline,
            occlusion_layout,
            blur_pipeline,
            blur_layout,
            ssao_buffer,
            blur_horizontal_buffer,
            blur_vertical_buffer,
            noise_view,
            point_sampler,
            repeat_sampler,
            linear_sampler,
            kernel,
            targets: None,
            occlusion: false,
            generation: 0,
        }
    }

    /// Changes whenever the AO view handed to the scene shader changes.
    #[inline]
    pub fn generation(&self) -> u64 {
        self.generation
    }

    /// The blurred occlusion texture, once targets exist.
    #[inline]
    pub fn ao_view(&self) -> Option<&wgpu::TextureView> {
        self.targets.as_ref().filter(|_| self.occlusion).map(|t| &t.ao)
    }

    /// Single sample depth of the prepass, for effects that need scene depth.
    #[inline]
    pub fn depth_view(&self) -> Option<&wgpu::TextureView> {
        self.targets.as_ref().map(|t| &t.depth)
    }

    #[inline]
    pub fn linear_sampler(&self) -> &wgpu::Sampler {
        &self.linear_sampler
    }

    /// Allocates (or frees) the size dependent targets.
    pub fn ensure_targets(
        &mut self,
        device: &wgpu::Device,
        enabled: bool,
        occlusion: bool,
        width: u32,
        height: u32,
    ) {
        if occlusion != self.occlusion {
            self.occlusion = occlusion;
            self.generation += 1;
        }
        if !enabled {
            if self.targets.take().is_some() {
                self.generation += 1;
            }
            return;
        }
        if let Some(targets) = &self.targets
            && targets.width == width
            && targets.height == height
        {
            return;
        }
        self.targets = Some(self.create_targets(device, width, height));
        self.generation += 1;
    }

    fn create_targets(&self, device: &wgpu::Device, width: u32, height: u32) -> SizedTargets {
        let make = |label, format, w: u32, h: u32| {
            device
                .create_texture(&wgpu::TextureDescriptor {
                    label: Some(label),
                    size: wgpu::Extent3d {
                        width: w.max(1),
                        height: h.max(1),
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format,
                    usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                        | wgpu::TextureUsages::TEXTURE_BINDING,
                    view_formats: &[],
                })
                .create_view(&wgpu::TextureViewDescriptor::default())
        };
        let (half_width, half_height) = (width.div_ceil(2), height.div_ceil(2));
        let gbuffer = make("threenet.ssao.gbuffer", GBUFFER_FORMAT, width, height);
        let depth = make("threenet.ssao.depth", DEPTH_FORMAT, width, height);
        let ao = make("threenet.ssao.ao", AO_FORMAT, half_width, half_height);
        let ao_scratch = make(
            "threenet.ssao.ao_scratch",
            AO_FORMAT,
            half_width,
            half_height,
        );

        let occlusion_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("threenet.bind_group.ssao"),
            layout: &self.occlusion_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&gbuffer),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&self.point_sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(&self.noise_view),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::Sampler(&self.repeat_sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: self.ssao_buffer.as_entire_binding(),
                },
            ],
        });
        let blur_bind_group = |source: &wgpu::TextureView, buffer: &wgpu::Buffer| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("threenet.bind_group.ssao_blur"),
                layout: &self.blur_layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(source),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::Sampler(&self.linear_sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(&gbuffer),
                    },
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: buffer.as_entire_binding(),
                    },
                ],
            })
        };
        // Occlusion -> ao, horizontal blur ao -> scratch, vertical blur scratch -> ao.
        let blur_horizontal_bind_group = blur_bind_group(&ao, &self.blur_horizontal_buffer);
        let blur_vertical_bind_group = blur_bind_group(&ao_scratch, &self.blur_vertical_buffer);

        SizedTargets {
            width,
            height,
            gbuffer,
            depth,
            ao,
            ao_scratch,
            occlusion_bind_group,
            blur_horizontal_bind_group,
            blur_vertical_bind_group,
        }
    }

    /// Runs the prepass, the occlusion pass and the blur. Returns the number of
    /// prepass draw calls.
    #[allow(clippy::too_many_arguments)]
    pub fn render(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        settings: &SsaoSettings,
        projection: &Mat4,
        frame_buffer: &wgpu::Buffer,
        object_bind_group: &wgpu::BindGroup,
        items: &[GBufferItem],
        resources: &ResourceCache,
    ) -> u32 {
        let Some(targets) = &self.targets else {
            return 0;
        };
        let gbuffer_frame_bind_group = self.gbuffer_frame_bind_group.get_or_insert_with(|| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("threenet.bind_group.gbuffer_frame"),
                layout: &self.gbuffer_frame_layout,
                entries: &[wgpu::BindGroupEntry {
                    binding: 0,
                    resource: frame_buffer.as_entire_binding(),
                }],
            })
        });

        let (half_width, half_height) = (targets.width.div_ceil(2), targets.height.div_ceil(2));
        let mut uniform = SsaoUniform::zeroed_with_kernel(self.kernel);
        uniform.projection = projection.to_cols_array_2d();
        uniform.params = [
            settings.radius.max(0.01),
            settings.bias.max(0.0),
            settings.intensity.max(0.1),
            settings.samples.clamp(4, MAX_SSAO_SAMPLES as u32) as f32,
        ];
        uniform.noise = [
            half_width as f32 / NOISE_SIZE as f32,
            half_height as f32 / NOISE_SIZE as f32,
            1.0 / half_width as f32,
            1.0 / half_height as f32,
        ];
        queue.write_buffer(&self.ssao_buffer, 0, bytemuck::bytes_of(&uniform));
        // Blur steps are expressed in gbuffer uv space; sharpness scales the depth weight.
        let sharpness = 40.0;
        queue.write_buffer(
            &self.blur_horizontal_buffer,
            0,
            bytemuck::bytes_of(&BlurUniform {
                params: [1.0 / half_width as f32, 0.0, sharpness, 0.0],
            }),
        );
        queue.write_buffer(
            &self.blur_vertical_buffer,
            0,
            bytemuck::bytes_of(&BlurUniform {
                params: [0.0, 1.0 / half_height as f32, sharpness, 0.0],
            }),
        );

        // ------------------------------------------------------------ prepass
        let mut draw_calls = 0;
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("threenet.pass.gbuffer"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &targets.gbuffer,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: &targets.depth,
                    depth_ops: Some(wgpu::Operations {
                        load: wgpu::LoadOp::Clear(1.0),
                        store: wgpu::StoreOp::Store,
                    }),
                    stencil_ops: None,
                }),
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            pass.set_pipeline(&self.gbuffer_pipeline);
            pass.set_bind_group(0, &*gbuffer_frame_bind_group, &[]);
            for item in items {
                let Some(mesh) = resources.mesh(item.geometry) else {
                    continue;
                };
                if mesh.topology != Topology::TriangleList {
                    continue;
                }
                pass.set_bind_group(1, object_bind_group, &[item.object_offset]);
                pass.set_vertex_buffer(0, mesh.vertex_buffer.slice(..));
                match &mesh.index_buffer {
                    Some(index_buffer) => {
                        pass.set_index_buffer(index_buffer.slice(..), wgpu::IndexFormat::Uint32);
                        pass.draw_indexed(0..mesh.index_count, 0, 0..1);
                    }
                    None => pass.draw(0..mesh.vertex_count, 0..1),
                }
                draw_calls += 1;
            }
        }

        if !self.occlusion {
            return draw_calls;
        }

        // ----------------------------------------------------- occlusion + blur
        let fullscreen = |encoder: &mut wgpu::CommandEncoder,
                          label: &str,
                          target: &wgpu::TextureView,
                          pipeline: &wgpu::RenderPipeline,
                          bind_group: &wgpu::BindGroup| {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some(label),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: target,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::WHITE),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            pass.set_pipeline(pipeline);
            pass.set_bind_group(0, bind_group, &[]);
            pass.draw(0..3, 0..1);
        };
        fullscreen(
            encoder,
            "threenet.pass.ssao",
            &targets.ao,
            &self.occlusion_pipeline,
            &targets.occlusion_bind_group,
        );
        fullscreen(
            encoder,
            "threenet.pass.ssao_blur_h",
            &targets.ao_scratch,
            &self.blur_pipeline,
            &targets.blur_horizontal_bind_group,
        );
        fullscreen(
            encoder,
            "threenet.pass.ssao_blur_v",
            &targets.ao,
            &self.blur_pipeline,
            &targets.blur_vertical_bind_group,
        );

        draw_calls
    }
}

fn fullscreen_pipeline(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    shader: &wgpu::ShaderModule,
    fragment: &str,
    label: &str,
) -> wgpu::RenderPipeline {
    let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some(label),
        bind_group_layouts: &[Some(layout)],
        immediate_size: 0,
    });
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some(label),
        layout: Some(&pipeline_layout),
        vertex: wgpu::VertexState {
            module: shader,
            entry_point: Some("vs_fullscreen"),
            buffers: &[],
            compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module: shader,
            entry_point: Some(fragment),
            targets: &[Some(wgpu::ColorTargetState {
                format: AO_FORMAT,
                blend: None,
                write_mask: wgpu::ColorWrites::ALL,
            })],
            compilation_options: Default::default(),
        }),
        primitive: wgpu::PrimitiveState::default(),
        depth_stencil: None,
        multisample: wgpu::MultisampleState::default(),
        multiview_mask: None,
        cache: None,
    })
}

/// Tiny deterministic xorshift generator: the kernel must be identical on
/// every run so screenshots stay reproducible.
struct XorShift(u32);

impl XorShift {
    fn next_f32(&mut self) -> f32 {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        self.0 = x;
        (x >> 8) as f32 / (1u32 << 24) as f32
    }
}

/// Hemisphere samples (+Z up) that get denser near the origin, so close
/// occluders contribute more than distant ones.
pub fn generate_kernel(seed: u32) -> [[f32; 4]; MAX_SSAO_SAMPLES] {
    let mut rng = XorShift(seed.max(1));
    let mut kernel = [[0.0; 4]; MAX_SSAO_SAMPLES];
    for (i, sample) in kernel.iter_mut().enumerate() {
        let mut v = glam::Vec3::new(
            rng.next_f32() * 2.0 - 1.0,
            rng.next_f32() * 2.0 - 1.0,
            rng.next_f32(),
        );
        // Keep samples away from the tangent plane to limit self occlusion.
        v.z = v.z.max(0.1);
        v = v.normalize() * rng.next_f32();
        let t = i as f32 / MAX_SSAO_SAMPLES as f32;
        v *= 0.1 + 0.9 * t * t;
        *sample = [v.x, v.y, v.z, 0.0];
    }
    kernel
}

/// Random rotation vectors around the view normal, encoded in `0..255`.
fn generate_noise(seed: u32) -> Vec<u8> {
    let mut rng = XorShift(seed.max(1));
    let mut data = Vec::with_capacity((NOISE_SIZE * NOISE_SIZE * 4) as usize);
    for _ in 0..NOISE_SIZE * NOISE_SIZE {
        let v = glam::Vec2::new(rng.next_f32() * 2.0 - 1.0, rng.next_f32() * 2.0 - 1.0)
            .normalize_or(glam::Vec2::X);
        data.extend_from_slice(&[
            ((v.x * 0.5 + 0.5) * 255.0) as u8,
            ((v.y * 0.5 + 0.5) * 255.0) as u8,
            128,
            255,
        ]);
    }
    data
}

impl SsaoUniform {
    fn zeroed_with_kernel(kernel: [[f32; 4]; MAX_SSAO_SAMPLES]) -> Self {
        let mut uniform: SsaoUniform = bytemuck::Zeroable::zeroed();
        uniform.kernel = kernel;
        uniform
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kernel_is_a_unit_hemisphere() {
        let kernel = generate_kernel(42);
        for sample in kernel {
            let v = glam::Vec3::new(sample[0], sample[1], sample[2]);
            assert!(v.z >= 0.0, "sample below the surface: {v:?}");
            assert!(v.length() <= 1.0 + 1e-4);
        }
        assert_eq!(
            kernel,
            generate_kernel(42),
            "the kernel must be deterministic"
        );
    }
}
