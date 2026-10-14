//! Post-processing: bright pass, separable blur (bloom) and the tone mapping
//! composite that writes the final image into the swap chain or an offscreen
//! target.

use std::collections::HashMap;

use wgpu::util::DeviceExt;

use crate::renderer::pipeline::HDR_FORMAT;
use crate::renderer::uniforms::PostUniform;

/// Tone mapping operator applied by the composite pass.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
#[repr(u32)]
pub enum ToneMapping {
    /// Clamp to `0..1`, no curve.
    None = 0,
    Reinhard = 1,
    #[default]
    Aces = 2,
    Filmic = 3,
}

impl ToneMapping {
    pub fn from_u32(value: u32) -> Self {
        match value {
            0 => ToneMapping::None,
            1 => ToneMapping::Reinhard,
            3 => ToneMapping::Filmic,
            _ => ToneMapping::Aces,
        }
    }
}

/// Half resolution ping-pong textures used by the bloom passes.
#[derive(Debug)]
struct BloomTargets {
    width: u32,
    height: u32,
    a: wgpu::TextureView,
    b: wgpu::TextureView,
}

impl BloomTargets {
    fn new(device: &wgpu::Device, width: u32, height: u32) -> Self {
        let make = |label: &str| {
            device
                .create_texture(&wgpu::TextureDescriptor {
                    label: Some(label),
                    size: wgpu::Extent3d {
                        width,
                        height,
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format: HDR_FORMAT,
                    usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                        | wgpu::TextureUsages::TEXTURE_BINDING,
                    view_formats: &[],
                })
                .create_view(&wgpu::TextureViewDescriptor::default())
        };
        Self {
            width,
            height,
            a: make("threenet.bloom.a"),
            b: make("threenet.bloom.b"),
        }
    }
}

#[derive(Debug)]
pub struct PostProcess {
    shader: wgpu::ShaderModule,
    simple_layout: wgpu::BindGroupLayout,
    composite_layout: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    threshold_pipeline: wgpu::RenderPipeline,
    blur_pipeline: wgpu::RenderPipeline,
    composite_pipelines: HashMap<wgpu::TextureFormat, wgpu::RenderPipeline>,
    threshold_uniform: wgpu::Buffer,
    blur_h_uniform: wgpu::Buffer,
    blur_v_uniform: wgpu::Buffer,
    composite_uniform: wgpu::Buffer,
    bloom: Option<BloomTargets>,
    fxaa_pipelines: HashMap<wgpu::TextureFormat, wgpu::RenderPipeline>,
    fxaa_uniform: wgpu::Buffer,
    /// The composite lands here first when antialiasing is on, so FXAA has
    /// something to read. Keyed by size and format; rebuilt when either moves.
    fxaa_target: Option<(wgpu::Texture, wgpu::TextureView, u32, u32, wgpu::TextureFormat)>,
    width: u32,
    height: u32,
}

impl PostProcess {
    pub fn new(device: &wgpu::Device, shader: wgpu::ShaderModule) -> Self {
        let texture_entry = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable: true },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let sampler_entry = wgpu::BindGroupLayoutEntry {
            binding: 1,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
            count: None,
        };
        let uniform_entry = wgpu::BindGroupLayoutEntry {
            binding: 2,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size: wgpu::BufferSize::new(size_of::<PostUniform>() as u64),
            },
            count: None,
        };

        let simple_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.post"),
            entries: &[texture_entry(0), sampler_entry, uniform_entry],
        });
        let composite_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.post.composite"),
            entries: &[
                texture_entry(0),
                sampler_entry,
                uniform_entry,
                texture_entry(3),
                texture_entry(4),
            ],
        });

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("threenet.post.sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::MipmapFilterMode::Nearest,
            ..Default::default()
        });

        let simple_pipeline_layout =
            device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("threenet.pipeline_layout.post"),
                bind_group_layouts: &[Some(&simple_layout)],
                immediate_size: 0,
            });

        let make_pipeline = |label: &str, entry: &str, format: wgpu::TextureFormat| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(label),
                layout: Some(&simple_pipeline_layout),
                vertex: wgpu::VertexState {
                    module: &shader,
                    entry_point: Some("vs_fullscreen"),
                    buffers: &[],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: &shader,
                    entry_point: Some(entry),
                    targets: &[Some(format.into())],
                    compilation_options: Default::default(),
                }),
                primitive: wgpu::PrimitiveState::default(),
                depth_stencil: None,
                multisample: wgpu::MultisampleState::default(),
                multiview_mask: None,
                cache: None,
            })
        };

        let threshold_pipeline =
            make_pipeline("threenet.post.threshold", "fs_threshold", HDR_FORMAT);
        let blur_pipeline = make_pipeline("threenet.post.blur", "fs_blur", HDR_FORMAT);

        let uniform = |label: &str| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents: bytemuck::bytes_of(&PostUniform::default()),
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            })
        };

        Self {
            simple_layout,
            composite_layout,
            sampler,
            threshold_pipeline,
            blur_pipeline,
            composite_pipelines: HashMap::new(),
            fxaa_pipelines: HashMap::new(),
            fxaa_uniform: device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("threenet.post.fxaa.uniform"),
                contents: bytemuck::bytes_of(&PostUniform::default()),
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            }),
            fxaa_target: None,
            width: 1,
            height: 1,
            threshold_uniform: uniform("threenet.post.threshold.uniform"),
            blur_h_uniform: uniform("threenet.post.blur_h.uniform"),
            blur_v_uniform: uniform("threenet.post.blur_v.uniform"),
            composite_uniform: uniform("threenet.post.composite.uniform"),
            bloom: None,
            shader,
        }
    }

    fn composite_pipeline(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
    ) -> &wgpu::RenderPipeline {
        let shader = &self.shader;
        let layout = &self.composite_layout;
        self.composite_pipelines.entry(format).or_insert_with(|| {
            let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("threenet.pipeline_layout.post.composite"),
                bind_group_layouts: &[Some(layout)],
                immediate_size: 0,
            });
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("threenet.post.composite"),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: shader,
                    entry_point: Some("vs_fullscreen"),
                    buffers: &[],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: shader,
                    entry_point: Some("fs_composite"),
                    targets: &[Some(format.into())],
                    compilation_options: Default::default(),
                }),
                primitive: wgpu::PrimitiveState::default(),
                depth_stencil: None,
                multisample: wgpu::MultisampleState::default(),
                multiview_mask: None,
                cache: None,
            })
        })
    }

    fn fxaa_pipeline(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
    ) -> &wgpu::RenderPipeline {
        let shader = &self.shader;
        let layout = &self.simple_layout;
        self.fxaa_pipelines.entry(format).or_insert_with(|| {
            let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("threenet.post.fxaa.layout"),
                bind_group_layouts: &[Some(layout)],
                immediate_size: 0,
            });
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("threenet.post.fxaa"),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: shader,
                    entry_point: Some("vs_fullscreen"),
                    buffers: &[],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: shader,
                    entry_point: Some("fs_fxaa"),
                    targets: &[Some(wgpu::ColorTargetState {
                        format,
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
        })
    }

    /// The intermediate the composite writes into while FXAA is on.
    fn ensure_fxaa_target(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
    ) -> wgpu::TextureView {
        let (width, height) = (self.width.max(1), self.height.max(1));
        let matches = self
            .fxaa_target
            .as_ref()
            .is_some_and(|(_, _, w, h, f)| *w == width && *h == height && *f == format);
        if !matches {
            let texture = device.create_texture(&wgpu::TextureDescriptor {
                label: Some("threenet.post.fxaa.target"),
                size: wgpu::Extent3d {
                    width,
                    height,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format,
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
                view_formats: &[],
            });
            let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
            self.fxaa_target = Some((texture, view, width, height, format));
        }
        self.fxaa_target
            .as_ref()
            .expect("just built")
            .1
            .clone()
    }

    /// (Re)allocates the half resolution bloom targets.
    pub fn resize(&mut self, device: &wgpu::Device, width: u32, height: u32, bloom_enabled: bool) {
        self.width = width.max(1);
        self.height = height.max(1);
        if !bloom_enabled {
            self.bloom = None;
            return;
        }
        let (w, h) = ((width / 2).max(1), (height / 2).max(1));
        let matches = self
            .bloom
            .as_ref()
            .is_some_and(|b| b.width == w && b.height == h);
        if !matches {
            self.bloom = Some(BloomTargets::new(device, w, h));
        }
    }

    fn bind_simple(
        &self,
        device: &wgpu::Device,
        source: &wgpu::TextureView,
        uniform: &wgpu::Buffer,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("threenet.post.bind_group"),
            layout: &self.simple_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(source),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: uniform.as_entire_binding(),
                },
            ],
        })
    }

    fn full_screen_pass(
        encoder: &mut wgpu::CommandEncoder,
        label: &str,
        target: &wgpu::TextureView,
        pipeline: &wgpu::RenderPipeline,
        bind_group: &wgpu::BindGroup,
    ) {
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some(label),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: target,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
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
    }

    /// Runs bloom (when enabled) and the tone mapping composite.
    #[allow(clippy::too_many_arguments)]
    pub fn execute(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        hdr: &wgpu::TextureView,
        output: &wgpu::TextureView,
        output_format: wgpu::TextureFormat,
        color_lut: Option<&wgpu::TextureView>,
        settings: PostSettings,
    ) {
        let bloom_enabled = settings.bloom_intensity > 0.0 && self.bloom.is_some();
        // Same trick the bloom binding uses: a layout entry has to point at
        // something, so without a table it points at the HDR buffer and the
        // shader never reads it because the strength is zero.
        let lut_strength = match color_lut {
            Some(_) => settings.color_lut_strength.clamp(0.0, 1.0),
            None => 0.0,
        };
        let lut_view = color_lut.unwrap_or(hdr);

        if bloom_enabled {
            let bloom = self.bloom.as_ref().expect("checked above");
            let texel = [1.0 / bloom.width as f32, 1.0 / bloom.height as f32];

            queue.write_buffer(
                &self.threshold_uniform,
                0,
                bytemuck::bytes_of(&PostUniform {
                    params: [
                        settings.exposure,
                        settings.tone_mapping as u32 as f32,
                        settings.bloom_intensity,
                        settings.bloom_threshold,
                    ],
                    texel: [texel[0], texel[1], 0.0, 0.0],
                    // The bloom stages do no grading; zero is neutral there.
                    grade: [0.0; 4],
                    grade2: [0.0; 4],
                }),
            );
            queue.write_buffer(
                &self.blur_h_uniform,
                0,
                bytemuck::bytes_of(&PostUniform {
                    params: [settings.exposure, 0.0, 0.0, 0.0],
                    texel: [texel[0], texel[1], 1.0, 0.0],
                    // The bloom stages do no grading; zero is neutral there.
                    grade: [0.0; 4],
                    grade2: [0.0; 4],
                }),
            );
            queue.write_buffer(
                &self.blur_v_uniform,
                0,
                bytemuck::bytes_of(&PostUniform {
                    params: [settings.exposure, 0.0, 0.0, 0.0],
                    texel: [texel[0], texel[1], 0.0, 1.0],
                    // The bloom stages do no grading; zero is neutral there.
                    grade: [0.0; 4],
                    grade2: [0.0; 4],
                }),
            );

            let threshold_bind = self.bind_simple(device, hdr, &self.threshold_uniform);
            Self::full_screen_pass(
                encoder,
                "threenet.post.threshold",
                &bloom.a,
                &self.threshold_pipeline,
                &threshold_bind,
            );

            // Two separable iterations widen the kernel without extra taps.
            for _ in 0..settings.bloom_iterations.max(1) {
                let horizontal = self.bind_simple(device, &bloom.a, &self.blur_h_uniform);
                Self::full_screen_pass(
                    encoder,
                    "threenet.post.blur_h",
                    &bloom.b,
                    &self.blur_pipeline,
                    &horizontal,
                );
                let vertical = self.bind_simple(device, &bloom.b, &self.blur_v_uniform);
                Self::full_screen_pass(
                    encoder,
                    "threenet.post.blur_v",
                    &bloom.a,
                    &self.blur_pipeline,
                    &vertical,
                );
            }
        }

        queue.write_buffer(
            &self.composite_uniform,
            0,
            bytemuck::bytes_of(&PostUniform {
                params: [
                    settings.exposure,
                    settings.tone_mapping as u32 as f32,
                    if bloom_enabled {
                        settings.bloom_intensity
                    } else {
                        0.0
                    },
                    settings.bloom_threshold,
                ],
                texel: [0.0; 4],
                grade: [
                    settings.vignette,
                    settings.vignette_softness,
                    settings.grain,
                    settings.time,
                ],
                grade2: [
                    settings.saturation,
                    settings.contrast,
                    settings.brightness,
                    lut_strength,
                ],
            }),
        );

        // The composite always binds a bloom texture; without bloom it reads the
        // HDR buffer and is ignored by the shader (intensity is zero).
        let bloom_view = match (&self.bloom, bloom_enabled) {
            (Some(bloom), true) => &bloom.a,
            _ => hdr,
        };
        let composite_bind = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("threenet.post.composite.bind_group"),
            layout: &self.composite_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(hdr),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: self.composite_uniform.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::TextureView(bloom_view),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: wgpu::BindingResource::TextureView(lut_view),
                },
            ],
        });
        // With antialiasing on, the composite lands in an intermediate and
        // FXAA copies it to the output; without it, the composite is the output.
        let composite_target = if settings.fxaa {
            self.ensure_fxaa_target(device, output_format)
        } else {
            output.clone()
        };

        let pipeline = self.composite_pipeline(device, output_format).clone();
        Self::full_screen_pass(
            encoder,
            "threenet.post.composite",
            &composite_target,
            &pipeline,
            &composite_bind,
        );

        if settings.fxaa {
            queue.write_buffer(
                &self.fxaa_uniform,
                0,
                bytemuck::bytes_of(&PostUniform {
                    params: [0.0; 4],
                    texel: [
                        1.0 / self.width.max(1) as f32,
                        1.0 / self.height.max(1) as f32,
                        0.0,
                        0.0,
                    ],
                    grade: [0.0; 4],
                    grade2: [0.0; 4],
                }),
            );
            let bind = self.bind_simple(device, &composite_target, &self.fxaa_uniform);
            let pipeline = self.fxaa_pipeline(device, output_format).clone();
            Self::full_screen_pass(encoder, "threenet.post.fxaa", output, &pipeline, &bind);
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct PostSettings {
    pub exposure: f32,
    pub tone_mapping: ToneMapping,
    pub bloom_intensity: f32,
    pub bloom_threshold: f32,
    pub bloom_iterations: u32,
    /// Darkens the corners; 0 is off.
    pub vignette: f32,
    /// How far in from the corner the darkening starts, 0 to 1.
    pub vignette_softness: f32,
    /// Film grain; 0 is off.
    pub grain: f32,
    /// Seconds, so the grain moves rather than sitting still on the lens.
    pub time: f32,
    /// 1 leaves colour alone, 0 is greyscale, above 1 pushes it.
    pub saturation: f32,
    /// 1 leaves contrast alone.
    pub contrast: f32,
    /// Added after everything else; 0 leaves brightness alone.
    pub brightness: f32,
    /// Fast approximate antialiasing, run on the finished image. The only
    /// antialiasing the deferred path can have, since MSAA needs a multisampled
    /// target and the G-buffer is resolved before the lighting sees it.
    pub fxaa: bool,
    /// How much of the colour lookup table to apply; 0 is off and skips it.
    pub color_lut_strength: f32,
}

impl Default for PostSettings {
    fn default() -> Self {
        Self {
            exposure: 1.0,
            tone_mapping: ToneMapping::Aces,
            bloom_intensity: 0.0,
            bloom_threshold: 1.0,
            bloom_iterations: 2,
            vignette: 0.0,
            vignette_softness: 0.5,
            grain: 0.0,
            time: 0.0,
            saturation: 1.0,
            contrast: 1.0,
            brightness: 0.0,
            fxaa: false,
            color_lut_strength: 0.0,
        }
    }
}
