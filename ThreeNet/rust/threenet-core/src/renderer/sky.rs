//! The sky pass.
//!
//! A fullscreen triangle is drawn at the far plane after the scene, with the
//! depth test on and depth writes off, so it fills exactly the pixels no
//! geometry claimed. That is cheaper than a dome mesh and avoids its two
//! problems: a dome is fogged like any other surface, and it lands in the depth
//! prepass that SSAO and depth of field read.

use crate::renderer::pipeline::{DEPTH_FORMAT, HDR_FORMAT, Layouts};
use crate::shader::COMMON;

const SKY: &str = include_str!("../shaders/sky.wgsl");

pub struct Sky {
    /// One pipeline per sample count, because the scene target may be multisampled.
    pipelines: Vec<(u32, wgpu::RenderPipeline)>,
    shader: wgpu::ShaderModule,
    layout: wgpu::PipelineLayout,
}

impl std::fmt::Debug for Sky {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Sky").field("pipelines", &self.pipelines.len()).finish()
    }
}

impl Sky {
    pub fn new(device: &wgpu::Device, layouts: &Layouts) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.sky"),
            source: wgpu::ShaderSource::Wgsl(format!("{COMMON}\n{SKY}").into()),
        });
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("threenet.pipeline_layout.sky"),
            bind_group_layouts: &[Some(&layouts.frame)],
            immediate_size: 0,
        });
        Self { pipelines: Vec::new(), shader, layout }
    }

    fn pipeline(&mut self, device: &wgpu::Device, samples: u32) -> &wgpu::RenderPipeline {
        if let Some(index) = self.pipelines.iter().position(|(count, _)| *count == samples) {
            return &self.pipelines[index].1;
        }

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("threenet.pipeline.sky"),
            layout: Some(&self.layout),
            vertex: wgpu::VertexState {
                module: &self.shader,
                entry_point: Some("vs_sky"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &self.shader,
                entry_point: Some("fs_sky"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: HDR_FORMAT,
                    blend: None,
                    write_mask: wgpu::ColorWrites::COLOR,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleList,
                cull_mode: None,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format: DEPTH_FORMAT,
                depth_write_enabled: Some(false),
                // The triangle sits at 1.0, so this passes only where the depth
                // buffer is still cleared - that is, where nothing was drawn.
                depth_compare: Some(wgpu::CompareFunction::LessEqual),
                stencil: Default::default(),
                bias: Default::default(),
            }),
            multisample: wgpu::MultisampleState {
                count: samples,
                ..Default::default()
            },
            multiview_mask: None,
            cache: None,
        });

        self.pipelines.push((samples, pipeline));
        &self.pipelines.last().expect("just pushed").1
    }

    /// Draws the sky into `color`, keeping whatever `depth` already covers.
    #[allow(clippy::too_many_arguments)]
    pub fn draw(
        &mut self,
        device: &wgpu::Device,
        encoder: &mut wgpu::CommandEncoder,
        color: &wgpu::TextureView,
        resolve_target: Option<&wgpu::TextureView>,
        depth: &wgpu::TextureView,
        frame_bind_group: &wgpu::BindGroup,
        samples: u32,
    ) {
        let pipeline = self.pipeline(device, samples);
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("threenet.pass.sky"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: color,
                depth_slice: None,
                resolve_target,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: depth,
                depth_ops: Some(wgpu::Operations {
                    load: wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: None,
            }),
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, frame_bind_group, &[]);
        pass.draw(0..3, 0..1);
    }
}
