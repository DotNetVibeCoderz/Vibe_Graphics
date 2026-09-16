//! Shadow mapping: a depth texture array shared by every shadow casting light.
//!
//! * Directional lights use cascaded shadow maps. The camera frustum up to
//!   `shadow_distance` is split into cascades (practical split scheme); each
//!   cascade is fitted with a bounding sphere so its size never changes when the
//!   camera rotates, and the projection is snapped to whole texels so shadows do
//!   not shimmer while the camera moves.
//! * Spot lights use one perspective layer covering the outer cone.
//! * Point lights do not cast shadows yet (they would need six layers each).

use std::collections::HashMap;

use wgpu::util::DeviceExt;

use crate::camera::Camera;
use crate::geometry::{Topology, Vertex};
use crate::light::{Light, LightKind};
use crate::math::{Aabb, Frustum, Mat4, Vec3, Vec4};
use crate::renderer::pipeline::DEPTH_FORMAT;
use crate::renderer::resources::ResourceCache;
use crate::renderer::uniforms::{
    LightUniform, MAX_CASCADES, MAX_SHADOW_LAYERS, ShadowPassUniform, ShadowUniform,
};
use crate::scene::GeometryId;

/// User facing shadow settings, taken from the renderer configuration.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowSettings {
    pub enabled: bool,
    /// Width and height of every layer in texels.
    pub map_size: u32,
    /// How far from the camera directional shadows reach.
    pub distance: f32,
    /// Cascades per directional light (1-4).
    pub cascades: u32,
    /// PCF kernel radius in texels (0 = hard shadows, 1 = 3x3, 2 = 5x5).
    pub softness: u32,
}

impl Default for ShadowSettings {
    fn default() -> Self {
        Self {
            enabled: false,
            map_size: 2048,
            distance: 60.0,
            cascades: 3,
            softness: 1,
        }
    }
}

/// One mesh rendered into the shadow maps.
#[derive(Debug, Clone, Copy)]
pub struct ShadowCaster {
    pub geometry: GeometryId,
    pub object_offset: u32,
    pub center: Vec3,
    pub radius: f32,
}

/// A shadow map layer planned for this frame.
#[derive(Debug, Clone, Copy)]
pub struct ShadowLayer {
    pub view_projection: Mat4,
}

/// A light as collected by the renderer, before it is uploaded.
#[derive(Debug, Clone, Copy)]
pub struct LightSource {
    pub light: Light,
    pub world: Mat4,
}

pub struct ShadowMaps {
    size: u32,
    texture: wgpu::Texture,
    array_view: wgpu::TextureView,
    layer_views: Vec<wgpu::TextureView>,
    sampler: wgpu::Sampler,
    uniform_buffer: wgpu::Buffer,
    pass_buffer: wgpu::Buffer,
    pass_bind_group: wgpu::BindGroup,
    pass_stride: u32,
    pipeline: wgpu::RenderPipeline,
    /// Bumped whenever the texture is recreated, so dependent bind groups rebuild.
    generation: u64,
    layers: Vec<ShadowLayer>,
}

impl std::fmt::Debug for ShadowMaps {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ShadowMaps")
            .field("size", &self.size)
            .field("layers", &self.layers.len())
            .finish()
    }
}

impl ShadowMaps {
    pub fn new(device: &wgpu::Device, object_layout: &wgpu::BindGroupLayout) -> Self {
        let pass_stride = {
            let alignment = device.limits().min_uniform_buffer_offset_alignment;
            (size_of::<ShadowPassUniform>() as u32).div_ceil(alignment) * alignment
        };
        let pass_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.layout.shadow_pass"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: true,
                    min_binding_size: wgpu::BufferSize::new(size_of::<ShadowPassUniform>() as u64),
                },
                count: None,
            }],
        });
        let pass_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("threenet.shadow.pass_uniform"),
            size: (pass_stride as usize * MAX_SHADOW_LAYERS) as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let pass_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("threenet.bind_group.shadow_pass"),
            layout: &pass_layout,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                    buffer: &pass_buffer,
                    offset: 0,
                    size: wgpu::BufferSize::new(size_of::<ShadowPassUniform>() as u64),
                }),
            }],
        });

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.shadow"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/shadow.wgsl").into()),
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("threenet.pipeline_layout.shadow"),
            bind_group_layouts: &[Some(&pass_layout), Some(object_layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("threenet.pipeline.shadow"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_shadow"),
                buffers: &[Some(Vertex::LAYOUT)],
                compilation_options: Default::default(),
            },
            fragment: None,
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleList,
                // No culling: thin and open geometry (planes, leaves) must still cast.
                cull_mode: None,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format: DEPTH_FORMAT,
                depth_write_enabled: Some(true),
                depth_compare: Some(wgpu::CompareFunction::LessEqual),
                stencil: wgpu::StencilState::default(),
                // Slope scaled bias removes most acne before the shader offsets kick in.
                bias: wgpu::DepthBiasState {
                    constant: 2,
                    slope_scale: 2.0,
                    clamp: 0.0,
                },
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("threenet.shadow.sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            // Linear comparison filtering gives a 2x2 hardware PCF per tap.
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            compare: Some(wgpu::CompareFunction::LessEqual),
            ..Default::default()
        });

        let uniform_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("threenet.shadow.uniform"),
            contents: bytemuck::bytes_of(&ShadowUniform::default()),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        let (texture, array_view, layer_views) = create_texture(device, 1);
        Self {
            size: 1,
            texture,
            array_view,
            layer_views,
            sampler,
            uniform_buffer,
            pass_buffer,
            pass_bind_group,
            pass_stride,
            pipeline,
            generation: 0,
            layers: Vec::new(),
        }
    }

    #[inline]
    pub fn array_view(&self) -> &wgpu::TextureView {
        &self.array_view
    }

    #[inline]
    pub fn sampler(&self) -> &wgpu::Sampler {
        &self.sampler
    }

    #[inline]
    pub fn uniform_buffer(&self) -> &wgpu::Buffer {
        &self.uniform_buffer
    }

    #[inline]
    pub fn generation(&self) -> u64 {
        self.generation
    }

    #[inline]
    pub fn layer_count(&self) -> usize {
        self.layers.len()
    }

    /// Reallocates the layers when the requested size changes. Disabled shadows
    /// keep a 1x1 array bound so the lighting bind group layout stays identical.
    pub fn ensure_size(&mut self, device: &wgpu::Device, settings: &ShadowSettings) {
        let size = if settings.enabled {
            settings.map_size.clamp(256, 8192)
        } else {
            1
        };
        if size == self.size {
            return;
        }
        let (texture, array_view, layer_views) = create_texture(device, size);
        self.texture = texture;
        self.array_view = array_view;
        self.layer_views = layer_views;
        self.size = size;
        self.generation += 1;
    }

    /// Assigns shadow layers to lights, computes their matrices and uploads the
    /// shadow uniform. `lights` are patched with their first layer index.
    #[allow(clippy::too_many_arguments)]
    pub fn plan(
        &mut self,
        queue: &wgpu::Queue,
        settings: &ShadowSettings,
        sources: &[LightSource],
        lights: &mut [LightUniform],
        camera: &Camera,
        camera_world: &Mat4,
        aspect: f32,
        caster_bounds: &Aabb,
    ) {
        self.layers.clear();
        let mut uniform = ShadowUniform::default();
        let cascades = settings.cascades.clamp(1, MAX_CASCADES as u32) as usize;

        if settings.enabled && !caster_bounds.is_empty() {
            let near = camera.near().max(0.01);
            let far = camera.far().min(settings.distance).max(near + 0.1);
            let splits = cascade_splits(near, far, cascades);
            uniform.cascade_splits = splits;
            let projection = camera.projection_matrix(aspect);

            for (index, source) in sources.iter().enumerate() {
                let light = &source.light;
                if !light.cast_shadow || index >= lights.len() {
                    continue;
                }

                let direction = crate::math::Mat3::from_mat4(source.world)
                    .mul_vec3(Vec3::NEG_Z)
                    .normalize_or(Vec3::NEG_Z);

                match light.kind {
                    LightKind::Directional => {
                        if self.layers.len() + cascades > MAX_SHADOW_LAYERS {
                            continue;
                        }
                        let first = self.layers.len();
                        let mut slice_near = near;
                        for (cascade, split) in splits.iter().take(cascades).enumerate() {
                            let (view_projection, texel_world) = directional_cascade(
                                &projection,
                                camera_world,
                                slice_near,
                                *split,
                                direction,
                                self.size,
                                caster_bounds,
                            );
                            set_texel(&mut uniform, first + cascade, texel_world);
                            uniform.matrices[first + cascade] = view_projection.to_cols_array_2d();
                            self.layers.push(ShadowLayer { view_projection });
                            slice_near = *split;
                        }
                        lights[index].shadow[0] = first as f32;
                        lights[index].shadow[1] = cascades as f32;
                    }
                    LightKind::Spot => {
                        if self.layers.len() >= MAX_SHADOW_LAYERS {
                            continue;
                        }
                        let layer = self.layers.len();
                        let position = source.world.w_axis.truncate();
                        let range = if light.range > 0.0 {
                            light.range
                        } else {
                            settings.distance
                        };
                        let fov = (light.outer_cone_angle * 2.0 + 0.05).clamp(0.1, 3.0);
                        let view = glam::camera::rh::view::look_to_mat4(
                            position,
                            direction,
                            stable_up(direction),
                        );
                        let proj =
                            glam::camera::rh::proj::directx::perspective(fov, 1.0, 0.05, range);
                        let view_projection = proj * view;
                        // Texel footprint at a quarter of the range, a typical receiver distance.
                        let texel_world = 2.0 * (fov * 0.5).tan() * range * 0.25 / self.size as f32;
                        set_texel(&mut uniform, layer, texel_world);
                        uniform.matrices[layer] = view_projection.to_cols_array_2d();
                        self.layers.push(ShadowLayer { view_projection });
                        lights[index].shadow[0] = layer as f32;
                        lights[index].shadow[1] = 1.0;
                    }
                    _ => {}
                }
            }

            uniform.params = [
                1.0 / self.size as f32,
                settings.softness.min(3) as f32,
                0.1,
                if self.layers.is_empty() { 0.0 } else { 1.0 },
            ];
        }

        queue.write_buffer(&self.uniform_buffer, 0, bytemuck::bytes_of(&uniform));
    }

    /// Renders every planned layer. Returns the number of draw calls.
    pub fn render(
        &self,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        casters: &[ShadowCaster],
        resources: &ResourceCache,
        object_bind_group: &wgpu::BindGroup,
    ) -> u32 {
        let mut draw_calls = 0;
        for (index, layer) in self.layers.iter().enumerate() {
            let offset = self.pass_stride * index as u32;
            queue.write_buffer(
                &self.pass_buffer,
                offset as u64,
                bytemuck::bytes_of(&ShadowPassUniform {
                    view_projection: layer.view_projection.to_cols_array_2d(),
                }),
            );

            let frustum = Frustum::from_view_projection(&layer.view_projection);
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("threenet.pass.shadow"),
                color_attachments: &[],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: &self.layer_views[index],
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
            pass.set_pipeline(&self.pipeline);
            pass.set_bind_group(0, &self.pass_bind_group, &[offset]);

            for caster in casters {
                if !frustum.intersects_sphere(caster.center, caster.radius) {
                    continue;
                }
                let Some(mesh) = resources.mesh(caster.geometry) else {
                    continue;
                };
                if mesh.topology != Topology::TriangleList {
                    continue;
                }
                pass.set_bind_group(1, object_bind_group, &[caster.object_offset]);
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
        draw_calls
    }
}

fn create_texture(
    device: &wgpu::Device,
    size: u32,
) -> (wgpu::Texture, wgpu::TextureView, Vec<wgpu::TextureView>) {
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("threenet.shadow.maps"),
        size: wgpu::Extent3d {
            width: size,
            height: size,
            depth_or_array_layers: MAX_SHADOW_LAYERS as u32,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: DEPTH_FORMAT,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
        view_formats: &[],
    });
    let array_view = texture.create_view(&wgpu::TextureViewDescriptor {
        label: Some("threenet.shadow.array_view"),
        dimension: Some(wgpu::TextureViewDimension::D2Array),
        ..Default::default()
    });
    let layer_views = (0..MAX_SHADOW_LAYERS as u32)
        .map(|layer| {
            texture.create_view(&wgpu::TextureViewDescriptor {
                label: Some("threenet.shadow.layer_view"),
                dimension: Some(wgpu::TextureViewDimension::D2),
                base_array_layer: layer,
                array_layer_count: Some(1),
                ..Default::default()
            })
        })
        .collect();
    (texture, array_view, layer_views)
}

fn set_texel(uniform: &mut ShadowUniform, layer: usize, texel_world: f32) {
    uniform.texel_world[layer / 4][layer % 4] = texel_world;
}

fn stable_up(direction: Vec3) -> Vec3 {
    if direction.y.abs() > 0.99 {
        Vec3::Z
    } else {
        Vec3::Y
    }
}

/// Practical split scheme: a blend of logarithmic (close cascades get more
/// resolution) and uniform splits. Returns the far distance of each cascade.
pub fn cascade_splits(near: f32, far: f32, count: usize) -> [f32; 4] {
    const LAMBDA: f32 = 0.75;
    let mut splits = [far; 4];
    for (i, split) in splits.iter_mut().enumerate().take(count) {
        let p = (i + 1) as f32 / count as f32;
        let logarithmic = near * (far / near).powf(p);
        let uniform = near + (far - near) * p;
        *split = LAMBDA * logarithmic + (1.0 - LAMBDA) * uniform;
    }
    splits
}

/// Fits one cascade: a sphere around the camera frustum slice, an orthographic
/// projection around it and texel snapping. Returns the view-projection and
/// the world space size of one texel.
fn directional_cascade(
    camera_projection: &Mat4,
    camera_world: &Mat4,
    slice_near: f32,
    slice_far: f32,
    direction: Vec3,
    map_size: u32,
    caster_bounds: &Aabb,
) -> (Mat4, f32) {
    let corners = frustum_slice_corners(camera_projection, camera_world, slice_near, slice_far);
    let center = corners.iter().copied().sum::<Vec3>() / corners.len() as f32;
    let mut radius = corners
        .iter()
        .map(|corner| corner.distance(center))
        .fold(0.0f32, f32::max);
    // Quantised radius: the projection size stays constant frame to frame.
    radius = (radius * 16.0).ceil() / 16.0;

    // Pull the eye back far enough to include every caster between the light
    // and the cascade.
    let backoff = caster_bounds.center().distance(center) + caster_bounds.radius() + radius;
    let eye = center - direction * backoff;
    let view = glam::camera::rh::view::look_to_mat4(eye, direction, stable_up(direction));
    let mut projection = glam::camera::rh::proj::directx::orthographic(
        -radius,
        radius,
        -radius,
        radius,
        0.0,
        backoff + radius * 2.0,
    );

    // Snap the world origin to the texel grid so the shadow does not crawl
    // when the camera translates.
    let origin = (projection * view) * Vec4::W;
    let half = map_size as f32 * 0.5;
    let texel_offset =
        (origin.truncate().truncate() * half).round() / half - origin.truncate().truncate();
    projection.w_axis.x += texel_offset.x;
    projection.w_axis.y += texel_offset.y;

    (projection * view, 2.0 * radius / map_size as f32)
}

/// World space corners of the camera frustum between two view depths.
fn frustum_slice_corners(projection: &Mat4, camera_world: &Mat4, near: f32, far: f32) -> [Vec3; 8] {
    let p = projection.to_cols_array_2d();
    let orthographic = p[3][3] > 0.5;
    let mut corners = [Vec3::ZERO; 8];
    let mut i = 0;
    for depth in [near, far] {
        for (nx, ny) in [(-1.0f32, -1.0f32), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0)] {
            let view_point = if orthographic {
                Vec3::new((nx - p[3][0]) / p[0][0], (ny - p[3][1]) / p[1][1], -depth)
            } else {
                Vec3::new(nx * depth / p[0][0], ny * depth / p[1][1], -depth)
            };
            corners[i] = camera_world.transform_point3(view_point);
            i += 1;
        }
    }
    corners
}

/// Groups the planned layers by light for diagnostics.
pub fn layer_usage(lights: &[LightUniform]) -> HashMap<usize, (usize, usize)> {
    lights
        .iter()
        .enumerate()
        .filter(|(_, light)| light.shadow[0] >= 0.0)
        .map(|(index, light)| (index, (light.shadow[0] as usize, light.shadow[1] as usize)))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_are_increasing_and_end_at_far() {
        let splits = cascade_splits(0.1, 100.0, 4);
        assert!(splits.windows(2).all(|w| w[0] < w[1]));
        assert!((splits[3] - 100.0).abs() < 1e-3);
        // Logarithmic weighting keeps the first cascade short.
        assert!(splits[0] < 25.0);
    }

    #[test]
    fn cascade_covers_the_slice() {
        let projection = glam::camera::rh::proj::directx::perspective(1.0, 1.5, 0.1, 100.0);
        let camera_world = Mat4::from_translation(Vec3::new(0.0, 2.0, 10.0));
        let bounds = Aabb {
            min: Vec3::splat(-20.0),
            max: Vec3::splat(20.0),
        };
        let direction = Vec3::new(-1.0, -1.0, -0.5).normalize();
        let (view_projection, texel) = directional_cascade(
            &projection,
            &camera_world,
            0.1,
            10.0,
            direction,
            1024,
            &bounds,
        );
        assert!(texel > 0.0);
        for corner in frustum_slice_corners(&projection, &camera_world, 0.1, 10.0) {
            let clip = view_projection * corner.extend(1.0);
            let ndc = clip.truncate() / clip.w;
            assert!(
                ndc.x.abs() <= 1.01 && ndc.y.abs() <= 1.01,
                "corner outside the cascade: {ndc:?}"
            );
            assert!(
                (0.0..=1.0).contains(&ndc.z),
                "corner outside the depth range: {ndc:?}"
            );
        }
    }
}
