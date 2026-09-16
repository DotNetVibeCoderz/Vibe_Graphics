//! Camera effects on the HDR image, before bloom and tone mapping: bokeh depth
//! of field and camera motion blur. Both read a single sample depth buffer (the
//! deferred G-buffer depth, or the forward depth prepass).

use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

use crate::math::Mat4;
use crate::renderer::pipeline::HDR_FORMAT;

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct EffectsUniform {
    inverse_projection: [[f32; 4]; 4],
    inverse_view_projection: [[f32; 4]; 4],
    previous_view_projection: [[f32; 4]; 4],
    dof: [f32; 4],
    motion: [f32; 4],
    texel: [f32; 4],
}

/// Per frame inputs of the effects chain.
#[derive(Debug, Clone, Copy)]
pub struct EffectsSettings {
    pub depth_of_field: bool,
    pub focus_distance: f32,
    pub focus_range: f32,
    pub max_blur: f32,
    pub motion_blur: bool,
    pub motion_strength: f32,
    pub motion_samples: u32,
    pub projection: Mat4,
    pub view_projection: Mat4,
    pub previous_view_projection: Mat4,
}

pub struct Effects {
    layout: wgpu::BindGroupLayout,
    dof_pipeline: wgpu::RenderPipeline,
    motion_pipeline: wgpu::RenderPipeline,
    sampler: wgpu::Sampler,
    uniform: wgpu::Buffer,
    targets: Option<(u32, u32, wgpu::TextureView, wgpu::TextureView)>,
}

impl std::fmt::Debug for Effects {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Effects").finish_non_exhaustive()
    }
}

impl Effects {
    pub fn new(device: &wgpu::Device) -> Self {
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.effects"),
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
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Depth,
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
                        min_binding_size: wgpu::BufferSize::new(size_of::<EffectsUniform>() as u64),
                    },
                    count: None,
                },
            ],
        });
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.effects"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/effects.wgsl").into()),
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("threenet.pipeline_layout.effects"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = |entry: &str| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("threenet.pipeline.effects"),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: &shader,
                    entry_point: Some("vs_fullscreen"),
                    buffers: &[],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: &shader,
                    entry_point: Some(entry),
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
            })
        };
        let dof_pipeline = pipeline("fs_dof");
        let motion_pipeline = pipeline("fs_motion");
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("threenet.effects.sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        let uniform = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("threenet.effects.uniform"),
            contents: bytemuck::bytes_of(&EffectsUniform::zeroed()),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        Self {
            layout,
            dof_pipeline,
            motion_pipeline,
            sampler,
            uniform,
            targets: None,
        }
    }

    fn ensure_targets(&mut self, device: &wgpu::Device, width: u32, height: u32) {
        if let Some((w, h, _, _)) = &self.targets
            && *w == width
            && *h == height
        {
            return;
        }
        let make = |label: &str| {
            device
                .create_texture(&wgpu::TextureDescriptor {
                    label: Some(label),
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
                .create_view(&wgpu::TextureViewDescriptor::default())
        };
        self.targets = Some((width, height, make("threenet.effects.a"), make("threenet.effects.b")));
    }

    /// Runs the enabled effects on `hdr` and returns the view holding the
    /// result (`hdr` itself when nothing is enabled).
    #[allow(clippy::too_many_arguments)]
    pub fn run<'a>(
        &'a mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        hdr: &'a wgpu::TextureView,
        depth: Option<&wgpu::TextureView>,
        width: u32,
        height: u32,
        settings: &EffectsSettings,
    ) -> &'a wgpu::TextureView {
        let Some(depth) = depth else {
            return hdr;
        };
        if !settings.depth_of_field && !settings.motion_blur {
            return hdr;
        }

        self.ensure_targets(device, width, height);
        // Blur radii are authored for 1080p and scale with the target.
        let scale = height as f32 / 1080.0;
        let uniform = EffectsUniform {
            inverse_projection: settings.projection.inverse().to_cols_array_2d(),
            inverse_view_projection: settings.view_projection.inverse().to_cols_array_2d(),
            previous_view_projection: settings.previous_view_projection.to_cols_array_2d(),
            dof: [
                settings.focus_distance.max(0.01),
                settings.focus_range.max(0.01),
                (settings.max_blur * scale).clamp(1.0, 40.0),
                0.0,
            ],
            motion: [
                settings.motion_strength.clamp(0.0, 2.0),
                settings.motion_samples.clamp(4, 32) as f32,
                0.06,
                0.0,
            ],
            texel: [1.0 / width as f32, 1.0 / height as f32, width as f32, height as f32],
        };
        queue.write_buffer(&self.uniform, 0, bytemuck::bytes_of(&uniform));

        let (_, _, a, b) = self.targets.as_ref().expect("targets were just created");
        let mut source: &wgpu::TextureView = hdr;
        let mut passes: Vec<(&wgpu::RenderPipeline, &wgpu::TextureView)> = Vec::new();
        if settings.depth_of_field {
            passes.push((&self.dof_pipeline, a));
        }
        if settings.motion_blur {
            passes.push((&self.motion_pipeline, if settings.depth_of_field { b } else { a }));
        }

        for (pipeline, target) in passes {
            let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("threenet.bind_group.effects"),
                layout: &self.layout,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(source) },
                    wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(depth) },
                    wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::Sampler(&self.sampler) },
                    wgpu::BindGroupEntry { binding: 3, resource: self.uniform.as_entire_binding() },
                ],
            });
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("threenet.pass.effects"),
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
            pass.set_pipeline(pipeline);
            pass.set_bind_group(0, &bind_group, &[]);
            pass.draw(0..3, 0..1);
            drop(pass);
            source = target;
        }

        source
    }
}
