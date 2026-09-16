//! Deferred shading.
//!
//! Opaque geometry writes its evaluated surface (albedo, normal, material
//! parameters, emission) into five G-buffer targets plus a sampleable depth
//! buffer. A single fullscreen lighting pass then shades every covered pixel
//! once with the same lighting library the forward renderer uses, so the cost
//! of lights no longer multiplies with overdraw. Transparent objects are drawn
//! afterwards with the forward pipelines against the G-buffer depth.

use crate::renderer::pipeline::{DEPTH_FORMAT, GBUFFER_FORMATS, HDR_FORMAT, Layouts};
use crate::shader::COMMON;

const LIGHTING: &str = include_str!("../shaders/deferred_lighting.wgsl");

pub struct DeferredTargets {
    pub width: u32,
    pub height: u32,
    pub colors: Vec<wgpu::TextureView>,
    pub depth: wgpu::TextureView,
    pub bind_group: wgpu::BindGroup,
}

pub struct Deferred {
    layout: wgpu::BindGroupLayout,
    pipeline: wgpu::RenderPipeline,
    targets: Option<DeferredTargets>,
}

impl std::fmt::Debug for Deferred {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Deferred")
            .field("size", &self.targets.as_ref().map(|t| (t.width, t.height)))
            .finish()
    }
}

impl Deferred {
    pub fn new(device: &wgpu::Device, layouts: &Layouts) -> Self {
        let mut entries: Vec<wgpu::BindGroupLayoutEntry> = (0..GBUFFER_FORMATS.len() as u32)
            .map(|binding| wgpu::BindGroupLayoutEntry {
                binding,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Texture {
                    sample_type: wgpu::TextureSampleType::Float { filterable: false },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled: false,
                },
                count: None,
            })
            .collect();
        entries.push(wgpu::BindGroupLayoutEntry {
            binding: GBUFFER_FORMATS.len() as u32,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Depth,
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        });
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.gbuffer"),
            entries: &entries,
        });

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.deferred_lighting"),
            source: wgpu::ShaderSource::Wgsl(format!("{COMMON}\n{LIGHTING}").into()),
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("threenet.pipeline_layout.deferred_lighting"),
            bind_group_layouts: &[Some(&layouts.frame), Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("threenet.pipeline.deferred_lighting"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_fullscreen"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_lighting"),
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

        Self {
            layout,
            pipeline,
            targets: None,
        }
    }

    pub fn targets(&self) -> Option<&DeferredTargets> {
        self.targets.as_ref()
    }

    /// Allocates the G-buffer for the current size, or frees it.
    pub fn ensure_targets(&mut self, device: &wgpu::Device, enabled: bool, width: u32, height: u32) {
        if !enabled {
            self.targets = None;
            return;
        }
        if let Some(targets) = &self.targets
            && targets.width == width
            && targets.height == height
        {
            return;
        }

        let size = wgpu::Extent3d {
            width: width.max(1),
            height: height.max(1),
            depth_or_array_layers: 1,
        };
        let make = |label: &str, format: wgpu::TextureFormat| {
            device
                .create_texture(&wgpu::TextureDescriptor {
                    label: Some(label),
                    size,
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format,
                    usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
                    view_formats: &[],
                })
                .create_view(&wgpu::TextureViewDescriptor::default())
        };
        let colors: Vec<wgpu::TextureView> = GBUFFER_FORMATS
            .iter()
            .enumerate()
            .map(|(index, format)| make(&format!("threenet.gbuffer.{index}"), *format))
            .collect();
        let depth = make("threenet.gbuffer.depth", DEPTH_FORMAT);

        let mut entries: Vec<wgpu::BindGroupEntry> = colors
            .iter()
            .enumerate()
            .map(|(binding, view)| wgpu::BindGroupEntry {
                binding: binding as u32,
                resource: wgpu::BindingResource::TextureView(view),
            })
            .collect();
        entries.push(wgpu::BindGroupEntry {
            binding: colors.len() as u32,
            resource: wgpu::BindingResource::TextureView(&depth),
        });
        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("threenet.bind_group.gbuffer"),
            layout: &self.layout,
            entries: &entries,
        });

        self.targets = Some(DeferredTargets {
            width,
            height,
            colors,
            depth,
            bind_group,
        });
    }

    /// Shades every covered G-buffer pixel into `target` (already cleared).
    pub fn light(&self, encoder: &mut wgpu::CommandEncoder, target: &wgpu::TextureView, frame_bind_group: &wgpu::BindGroup) {
        let Some(targets) = &self.targets else {
            return;
        };
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("threenet.pass.deferred_lighting"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: target,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, frame_bind_group, &[]);
        pass.set_bind_group(1, &targets.bind_group, &[]);
        pass.draw(0..3, 0..1);
    }
}
