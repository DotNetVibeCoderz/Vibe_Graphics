//! Bind group layouts and the render pipeline cache for the scene pass.

use std::collections::HashMap;

use crate::geometry::{Topology, Vertex};
use crate::material::{CullMode, Material};
use crate::shader::{CustomShader, ShaderPass, compose};
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

/// Which pass a scene pipeline renders.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum PipelinePass {
    /// Lit colour into the HDR target.
    Forward,
    /// Surface attributes into the deferred G-buffer.
    GBuffer,
}

/// Formats of the five deferred G-buffer targets.
pub const GBUFFER_FORMATS: [wgpu::TextureFormat; 5] = [wgpu::TextureFormat::Rgba16Float; 5];

/// Everything that forces a distinct pipeline object.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PipelineKey {
    pub pass: PipelinePass,
    pub topology: Topology,
    pub cull: CullMode,
    pub blend: bool,
    pub depth_write: bool,
    pub depth_test: bool,
    pub wireframe: bool,
    pub samples: u32,
    pub format: wgpu::TextureFormat,
    /// Custom shader id (0 = built-in) and its version.
    pub shader: u32,
    pub shader_version: u32,
}

impl PipelineKey {
    pub fn for_material(
        material: &Material,
        topology: Topology,
        samples: u32,
        format: wgpu::TextureFormat,
        shader: Option<&CustomShader>,
    ) -> Self {
        let blend = material.is_transparent();
        let shader_id = if shader.is_some() { material.shader.unwrap_or(0) } else { 0 };
        Self {
            pass: PipelinePass::Forward,
            topology,
            cull: material.cull_mode,
            blend,
            // Transparent surfaces must not occlude each other.
            depth_write: material.depth_write && !blend,
            depth_test: material.depth_test,
            wireframe: material.wireframe,
            samples,
            format,
            shader: shader_id,
            shader_version: shader.map_or(0, CustomShader::version),
        }
    }

    /// The deferred G-buffer variant of this key (single sample, no blending).
    pub fn gbuffer(mut self) -> Self {
        self.pass = PipelinePass::GBuffer;
        self.blend = false;
        self.samples = 1;
        self.format = GBUFFER_FORMATS[0];
        self
    }
}

#[derive(Debug)]
pub struct PipelineCache {
    pipeline_layout: wgpu::PipelineLayout,
    modules: HashMap<(u32, u32, PipelinePass), wgpu::ShaderModule>,
    pipelines: HashMap<PipelineKey, wgpu::RenderPipeline>,
    polygon_mode_line: bool,
}

impl PipelineCache {
    pub fn new(device: &wgpu::Device, layouts: &Layouts, polygon_mode_line: bool) -> Self {
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
            pipeline_layout,
            modules: HashMap::new(),
            pipelines: HashMap::new(),
            polygon_mode_line,
        }
    }

    pub fn clear(&mut self) {
        self.pipelines.clear();
    }

    /// Returns (and builds on first use) the pipeline for `key`. `shader` must be
    /// the custom shader named by `key.shader`, if any.
    pub fn get(
        &mut self,
        device: &wgpu::Device,
        key: PipelineKey,
        shader: Option<&CustomShader>,
    ) -> &wgpu::RenderPipeline {
        let module_key = (key.shader, key.shader_version, key.pass);
        if !self.modules.contains_key(&module_key) {
            let pass = match key.pass {
                PipelinePass::Forward => ShaderPass::Forward,
                PipelinePass::GBuffer => ShaderPass::DeferredGBuffer,
            };
            let source = compose(pass, shader.map(|s| s.hooks.as_str()));
            let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some(if key.shader == 0 {
                    "threenet.shader.scene"
                } else {
                    "threenet.shader.custom"
                }),
                source: wgpu::ShaderSource::Wgsl(source.into()),
            });
            // Old versions of an edited custom shader are dropped.
            self.modules
                .retain(|(id, version, _), _| *id != key.shader || *version == key.shader_version);
            self.pipelines
                .retain(|k, _| k.shader != key.shader || k.shader_version == key.shader_version);
            self.modules.insert(module_key, module);
        }

        let polygon_mode_line = self.polygon_mode_line;
        let module = &self.modules[&module_key];
        let pipeline_layout = &self.pipeline_layout;
        self.pipelines.entry(key).or_insert_with(|| {
            let wireframe = key.wireframe && polygon_mode_line;
            let blend = key.blend.then_some(wgpu::BlendState::ALPHA_BLENDING);
            let forward_target = [Some(wgpu::ColorTargetState {
                format: key.format,
                blend,
                write_mask: wgpu::ColorWrites::ALL,
            })];
            let gbuffer_targets = GBUFFER_FORMATS.map(|format| {
                Some(wgpu::ColorTargetState {
                    format,
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })
            });
            let (entry_point, targets): (&str, &[Option<wgpu::ColorTargetState>]) = match key.pass {
                PipelinePass::Forward => ("fs_main", &forward_target),
                PipelinePass::GBuffer => ("fs_gbuffer", &gbuffer_targets),
            };
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("threenet.pipeline.scene"),
                layout: Some(pipeline_layout),
                vertex: wgpu::VertexState {
                    module,
                    entry_point: Some("vs_main"),
                    buffers: &[Some(Vertex::LAYOUT)],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module,
                    entry_point: Some(entry_point),
                    targets,
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
