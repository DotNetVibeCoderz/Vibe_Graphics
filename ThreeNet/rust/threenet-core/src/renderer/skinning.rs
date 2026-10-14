//! Skinning on the GPU.
//!
//! The CPU path in [`crate::scene::Scene::update_animations`] transforms every
//! vertex and uploads the result, which is correct and keeps bounds and
//! raycasts honest but costs a full vertex buffer of traffic per skinned mesh
//! per frame. This path uploads the bind pose and the weights once, sends only
//! the joint matrices each frame - a few kilobytes at most - and lets a compute
//! kernel write the deformed vertices straight into the buffer the draw reads.
//!
//! What it gives up is the CPU's knowledge of the pose. Nothing in system
//! memory moves, so a raycast hits the bind pose and bounds would too. Bounds
//! are kept usable by expanding the bind pose box by how far the joints have
//! actually travelled, which is cheap because there are only ever a handful of
//! joints and the matrices are already in hand.

use std::collections::HashMap;

use crate::geometry::Vertex;
use crate::math::Mat4;
use crate::scene::GeometryId;

/// Per vertex skinning inputs, uploaded once and then left alone.
struct SkinInputs {
    bind_pose: wgpu::Buffer,
    joints: wgpu::Buffer,
    weights: wgpu::Buffer,
    vertex_count: u32,
    /// Geometry version the inputs were built from, so a reimport rebuilds.
    version: u32,
}

pub struct GpuSkinning {
    layout: wgpu::BindGroupLayout,
    pipeline: Option<wgpu::ComputePipeline>,
    inputs: HashMap<(u64, GeometryId), SkinInputs>,
    /// One joint matrix buffer per skinned node, grown as skeletons need.
    matrices: HashMap<(u64, crate::scene::NodeId), wgpu::Buffer>,
}

impl std::fmt::Debug for GpuSkinning {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GpuSkinning")
            .field("meshes", &self.inputs.len())
            .finish()
    }
}

const KERNEL: &str = include_str!("../shaders/skinning.wgsl");

impl GpuSkinning {
    pub fn new(device: &wgpu::Device) -> Self {
        let storage = |binding: u32, read_only: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Storage { read_only },
                has_dynamic_offset: false,
                min_binding_size: None,
            },
            count: None,
        };
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.skinning"),
            entries: &[
                storage(0, true),  // bind pose
                storage(1, true),  // joint indices
                storage(2, true),  // weights
                storage(3, true),  // joint matrices
                storage(4, false), // the vertices a draw will read
            ],
        });
        Self {
            layout,
            pipeline: None,
            inputs: HashMap::new(),
            matrices: HashMap::new(),
        }
    }

    /// Compiles the kernel the first time it is needed.
    fn pipeline(&mut self, device: &wgpu::Device) -> &wgpu::ComputePipeline {
        self.pipeline.get_or_insert_with(|| {
            let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("threenet.shader.skinning"),
                source: wgpu::ShaderSource::Wgsl(KERNEL.into()),
            });
            let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("threenet.pipeline_layout.skinning"),
                bind_group_layouts: &[Some(&self.layout)],
                immediate_size: 0,
            });
            device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: Some("threenet.pipeline.skinning"),
                layout: Some(&pipeline_layout),
                module: &module,
                entry_point: Some("skin"),
                compilation_options: Default::default(),
                cache: None,
            })
        })
    }

    /// Uploads the bind pose and weights for a geometry, once.
    #[allow(clippy::too_many_arguments)]
    fn ensure_inputs(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        scene: u64,
        geometry: GeometryId,
        bind_pose: &[Vertex],
        joints: &[[u16; 4]],
        weights: &[[f32; 4]],
        version: u32,
    ) -> bool {
        if self
            .inputs
            .get(&(scene, geometry))
            .is_some_and(|existing| existing.version == version)
        {
            return true;
        }
        if bind_pose.is_empty() {
            return false;
        }

        // Joint indices arrive as four u16 and go up as four u32: WGSL has no
        // 16 bit scalar, and unpacking by hand in the kernel would cost more
        // than the four bytes a vertex this spends.
        let widened: Vec<u32> = joints
            .iter()
            .flat_map(|set| set.iter().map(|index| *index as u32).collect::<Vec<_>>())
            .collect();

        let make = |label: &str, bytes: &[u8]| {
            use wgpu::util::DeviceExt;
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents: bytes,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            })
        };

        let _ = queue;
        self.inputs.insert(
            (scene, geometry),
            SkinInputs {
                bind_pose: make("threenet.skin.bind_pose", bytemuck::cast_slice(bind_pose)),
                joints: make("threenet.skin.joints", bytemuck::cast_slice(&widened)),
                weights: make("threenet.skin.weights", bytemuck::cast_slice(weights)),
                vertex_count: bind_pose.len() as u32,
                version,
            },
        );
        true
    }

    /// Uploads this frame's joint matrices for one node.
    fn ensure_matrices(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        scene: u64,
        node: crate::scene::NodeId,
        matrices: &[Mat4],
    ) -> bool {
        if matrices.is_empty() {
            return false;
        }
        let packed: Vec<[[f32; 4]; 4]> = matrices.iter().map(|m| m.to_cols_array_2d()).collect();
        let bytes: &[u8] = bytemuck::cast_slice(&packed);
        let fits = self
            .matrices
            .get(&(scene, node))
            .is_some_and(|buffer| buffer.size() as usize == bytes.len());
        if !fits {
            self.matrices.insert(
                (scene, node),
                device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some("threenet.skin.matrices"),
                    size: bytes.len() as u64,
                    usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                }),
            );
        }
        let Some(buffer) = self.matrices.get(&(scene, node)) else {
            return false;
        };
        queue.write_buffer(buffer, 0, bytes);
        true
    }

    /// Deforms one mesh into `target`, which is its own vertex buffer.
    #[allow(clippy::too_many_arguments)]
    pub fn dispatch(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        scene: u64,
        node: crate::scene::NodeId,
        geometry: GeometryId,
        bind_pose: &[Vertex],
        joints: &[[u16; 4]],
        weights: &[[f32; 4]],
        version: u32,
        matrices: &[Mat4],
        target: &wgpu::Buffer,
    ) -> bool {
        if !self.ensure_inputs(device, queue, scene, geometry, bind_pose, joints, weights, version) {
            return false;
        }
        if !self.ensure_matrices(device, queue, scene, node, matrices) {
            return false;
        }

        let (count, bind_group) = {
            let Some(inputs) = self.inputs.get(&(scene, geometry)) else {
                return false;
            };
            let Some(joint_buffer) = self.matrices.get(&(scene, node)) else {
                return false;
            };
            let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("threenet.bind_group.skinning"),
                layout: &self.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: inputs.bind_pose.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: inputs.joints.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: inputs.weights.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: joint_buffer.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 4,
                        resource: target.as_entire_binding(),
                    },
                ],
            });
            (inputs.vertex_count, bind_group)
        };

        let pipeline = self.pipeline(device).clone();
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: Some("threenet.pass.skinning"),
            timestamp_writes: None,
        });
        pass.set_pipeline(&pipeline);
        pass.set_bind_group(0, &bind_group, &[]);
        pass.dispatch_workgroups(count.div_ceil(64), 1, 1);
        true
    }
}
