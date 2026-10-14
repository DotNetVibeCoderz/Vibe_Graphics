//! Screen space reflections.
//!
//! One fullscreen pass over the HDR image, between the scene and the camera
//! effects, so that what a surface reflects goes on to bloom and be tone mapped
//! with everything else. It reads the same normal and linear depth prepass SSAO
//! uses, which is why turning it on turns that prepass on as well.
//!
//! It reflects what is on screen and nothing else. There is no second copy of
//! the scene to fall back on, so a ray that leaves the frame returns nothing and
//! the reflection fades out instead of ending. The weighting is Fresnel and a
//! global intensity: the prepass carries geometry rather than material
//! parameters, so roughness does not blur the reflection and a rough surface
//! reflects as sharply as a polished one. Use it for floors and water, which is
//! what it is good at, and reach for a cube probe when a specific object needs a
//! correct reflection.

use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

use crate::math::Mat4;
use crate::renderer::pipeline::HDR_FORMAT;

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct SsrUniform {
    projection: [[f32; 4]; 4],
    inverse_projection: [[f32; 4]; 4],
    params: [f32; 4],
}

/// User facing screen space reflection settings.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SsrSettings {
    pub enabled: bool,
    /// How much of the reflection to keep, 0 to 1, before the Fresnel weighting.
    pub intensity: f32,
    /// How far a ray may travel, in world units. Longer reaches further and
    /// costs proportionally more, because the step count is fixed.
    pub max_distance: f32,
    /// How far behind a surface a ray may be and still count as having hit it.
    /// Too small and reflections break up; too large and a ray passing behind a
    /// thin object latches onto it.
    pub thickness: f32,
    /// Coarse steps along the ray before the binary refinement (8-128).
    pub steps: u32,
}

impl Default for SsrSettings {
    fn default() -> Self {
        Self {
            enabled: false,
            intensity: 1.0,
            max_distance: 12.0,
            thickness: 0.5,
            steps: 32,
        }
    }
}

pub struct Ssr {
    layout: wgpu::BindGroupLayout,
    pipeline: wgpu::RenderPipeline,
    sampler: wgpu::Sampler,
    uniform: wgpu::Buffer,
    target: Option<(u32, u32, wgpu::TextureView)>,
}

impl std::fmt::Debug for Ssr {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Ssr")
            .field("size", &self.target.as_ref().map(|(w, h, _)| (*w, *h)))
            .finish()
    }
}

impl Ssr {
    pub fn new(device: &wgpu::Device) -> Self {
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.ssr"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                // The prepass target is read with textureLoad, never sampled:
                // a normal interpolated across a silhouette belongs to neither
                // surface.
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: false },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 3,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: wgpu::BufferSize::new(size_of::<SsrUniform>() as u64),
                    },
                    count: None,
                },
            ],
        });
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.ssr"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/ssr.wgsl").into()),
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("threenet.pipeline_layout.ssr"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("threenet.pipeline.ssr"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_fullscreen"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_ssr"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: HDR_FORMAT,
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
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("threenet.ssr.sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        let uniform = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("threenet.ssr.uniform"),
            contents: bytemuck::bytes_of(&SsrUniform::zeroed()),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        Self {
            layout,
            pipeline,
            sampler,
            uniform,
            target: None,
        }
    }

    fn ensure_target(&mut self, device: &wgpu::Device, width: u32, height: u32) {
        if let Some((w, h, _)) = &self.target
            && *w == width
            && *h == height
        {
            return;
        }
        let view = device
            .create_texture(&wgpu::TextureDescriptor {
                label: Some("threenet.ssr.target"),
                size: wgpu::Extent3d {
                    width: width.max(1),
                    height: height.max(1),
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: HDR_FORMAT,
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
                view_formats: &[],
            })
            .create_view(&wgpu::TextureViewDescriptor::default());
        self.target = Some((width, height, view));
    }

    /// Adds reflections to `hdr` and returns the view holding the result
    /// (`hdr` itself when there is nothing to do).
    #[allow(clippy::too_many_arguments)]
    pub fn run<'a>(
        &'a mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        hdr: &'a wgpu::TextureView,
        gbuffer: Option<&wgpu::TextureView>,
        projection: &Mat4,
        width: u32,
        height: u32,
        settings: &SsrSettings,
    ) -> &'a wgpu::TextureView {
        if !settings.enabled || settings.intensity <= 0.0 {
            return hdr;
        }
        // Without the prepass there are no normals to reflect off. The renderer
        // asks for it whenever this is on, so this is a guard rather than a
        // path anything reaches.
        let Some(gbuffer) = gbuffer else {
            return hdr;
        };

        self.ensure_target(device, width, height);
        queue.write_buffer(
            &self.uniform,
            0,
            bytemuck::bytes_of(&SsrUniform {
                projection: projection.to_cols_array_2d(),
                inverse_projection: projection.inverse().to_cols_array_2d(),
                params: [
                    settings.intensity.clamp(0.0, 1.0),
                    settings.max_distance.max(0.01),
                    settings.thickness.max(0.001),
                    settings.steps.clamp(8, 128) as f32,
                ],
            }),
        );

        let (_, _, target) = self.target.as_ref().expect("target was just created");
        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("threenet.bind_group.ssr"),
            layout: &self.layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(hdr),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(gbuffer),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: self.uniform.as_entire_binding(),
                },
            ],
        });
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("threenet.pass.ssr"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: target,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &bind_group, &[]);
        pass.draw(0..3, 0..1);
        drop(pass);

        target
    }
}
