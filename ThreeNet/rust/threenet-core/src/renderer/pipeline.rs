//! Bind group layouts and the render pipeline cache for the scene pass.

use std::collections::HashMap;

use crate::geometry::{Topology, Vertex};
use crate::material::{CullMode, Material};
use crate::renderer::uniforms::{
    FrameUniform, LightsUniform, MaterialUniform, ObjectUniform, ShadowUniform,
};

pub const DEPTH_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Depth32Float;
pub const HDR_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

/// The three bind group layouts of the scene shader:
/// frame (per view), material (per material) and object (per draw, dynamic).
#[derive(Debug)]
pub struct Layouts {
    pub frame: wgpu::BindGroupLayout,
    pub material: wgpu::BindGroupLayout,
    pub object: wgpu::BindGroupLayout,
}

impl Layouts {
    pub fn new(device: &wgpu::Device) -> Self {
        let uniform = |binding: u32, visibility: wgpu::ShaderStages, size: u64, dynamic: bool| {
            wgpu::BindGroupLayoutEntry {
                binding,
                visibility,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: dynamic,
                    min_binding_size: wgpu::BufferSize::new(size),
                },
                count: None,
            }
        };
        let texture = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable: true },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let sampler = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
            count: None,
        };

        let frame = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.frame"),
            entries: &[
                uniform(
                    0,
                    wgpu::ShaderStages::VERTEX_FRAGMENT,
                    size_of::<FrameUniform>() as u64,
                    false,
                ),
                uniform(
                    1,
                    wgpu::ShaderStages::FRAGMENT,
                    size_of::<LightsUniform>() as u64,
                    false,
                ),
                texture(2),
                sampler(3),
                // Shadow map array + comparison sampler + cascade matrices.
                wgpu::BindGroupLayoutEntry {
                    binding: 4,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Depth,
                        view_dimension: wgpu::TextureViewDimension::D2Array,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 5,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Comparison),
                    count: None,
                },
                uniform(
                    6,
                    wgpu::ShaderStages::FRAGMENT,
                    size_of::<ShadowUniform>() as u64,
                    false,
                ),
                // Blurred SSAO term.
                texture(7),
                sampler(8),
            ],
        });

        let material = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.material"),
            entries: &[
                uniform(
                    0,
                    wgpu::ShaderStages::VERTEX_FRAGMENT,
                    size_of::<MaterialUniform>() as u64,
                    false,
                ),
                texture(1),
                texture(2),
                texture(3),
                texture(4),
                texture(5),
                sampler(6),
            ],
        });

        let object = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.object"),
            entries: &[uniform(
                0,
                wgpu::ShaderStages::VERTEX,
                size_of::<ObjectUniform>() as u64,
                true,
            )],
        });

        Self {
            frame,
            material,
            object,
        }
    }
}

/// Everything that forces a distinct pipeline object.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PipelineKey {
    pub topology: Topology,
    pub cull: CullMode,
    pub blend: bool,
    pub depth_write: bool,
    pub depth_test: bool,
    pub wireframe: bool,
    pub samples: u32,
    pub format: wgpu::TextureFormat,
}

impl PipelineKey {
    pub fn for_material(
        material: &Material,
        topology: Topology,
        samples: u32,
        format: wgpu::TextureFormat,
    ) -> Self {
        let blend = material.is_transparent();
        Self {
            topology,
            cull: material.cull_mode,
            blend,
            // Transparent surfaces must not occlude each other.
            depth_write: material.depth_write && !blend,
            depth_test: material.depth_test,
            wireframe: material.wireframe,
            samples,
            format,
        }
    }
}

#[derive(Debug)]
pub struct PipelineCache {
    shader: wgpu::ShaderModule,
    pipeline_layout: wgpu::PipelineLayout,
    pipelines: HashMap<PipelineKey, wgpu::RenderPipeline>,
    polygon_mode_line: bool,
}

impl PipelineCache {
    pub fn new(
        device: &wgpu::Device,
        layouts: &Layouts,
        shader: wgpu::ShaderModule,
        polygon_mode_line: bool,
    ) -> Self {
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("threenet.pipeline_layout.scene"),
            bind_group_layouts: &[
                Some(&layouts.frame),
                Some(&layouts.material),
                Some(&layouts.object),
            ],
            immediate_size: 0,
        });
        Self {
            shader,
            pipeline_layout,
            pipelines: HashMap::new(),
            polygon_mode_line,
        }
    }

    pub fn clear(&mut self) {
        self.pipelines.clear();
    }

    pub fn get(&mut self, device: &wgpu::Device, key: PipelineKey) -> &wgpu::RenderPipeline {
        let polygon_mode_line = self.polygon_mode_line;
        let shader = &self.shader;
        let pipeline_layout = &self.pipeline_layout;
        self.pipelines.entry(key).or_insert_with(|| {
            let wireframe = key.wireframe && polygon_mode_line;
            let blend = key.blend.then_some(wgpu::BlendState::ALPHA_BLENDING);
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("threenet.pipeline.scene"),
                layout: Some(pipeline_layout),
                vertex: wgpu::VertexState {
                    module: shader,
                    entry_point: Some("vs_main"),
                    buffers: &[Some(Vertex::LAYOUT)],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: shader,
                    entry_point: Some("fs_main"),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: key.format,
                        blend,
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                    compilation_options: Default::default(),
                }),
                primitive: wgpu::PrimitiveState {
                    topology: key.topology.to_wgpu(),
                    strip_index_format: None,
                    front_face: wgpu::FrontFace::Ccw,
                    // Culling only applies to triangles.
                    cull_mode: if key.topology == Topology::TriangleList {
                        key.cull.to_wgpu()
                    } else {
                        None
                    },
                    polygon_mode: if wireframe {
                        wgpu::PolygonMode::Line
                    } else {
                        wgpu::PolygonMode::Fill
                    },
                    unclipped_depth: false,
                    conservative: false,
                },
                depth_stencil: Some(wgpu::DepthStencilState {
                    format: DEPTH_FORMAT,
                    depth_write_enabled: Some(key.depth_write),
                    depth_compare: Some(if key.depth_test {
                        wgpu::CompareFunction::LessEqual
                    } else {
                        wgpu::CompareFunction::Always
                    }),
                    stencil: wgpu::StencilState::default(),
                    bias: wgpu::DepthBiasState::default(),
                }),
                multisample: wgpu::MultisampleState {
                    count: key.samples,
                    mask: !0,
                    alpha_to_coverage_enabled: false,
                },
                multiview_mask: None,
                cache: None,
            })
        })
    }
}
