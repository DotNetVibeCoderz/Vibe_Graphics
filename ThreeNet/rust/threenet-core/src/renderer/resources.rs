//! GPU side caches for geometry, textures and materials. Everything is keyed by
//! the scene arena id and re-uploaded only when the CPU side version changes.

use std::collections::HashMap;

use wgpu::util::DeviceExt;

use crate::geometry::{Geometry, Topology};
use crate::material::Material;
use crate::renderer::uniforms::MaterialUniform;
use crate::scene::{GeometryId, MaterialId, Scene, TextureId};
use crate::texture::{SamplerDesc, Texture};

/// Vertex and index buffers for one [`Geometry`].
#[derive(Debug)]
pub struct GpuMesh {
    pub vertex_buffer: wgpu::Buffer,
    pub index_buffer: Option<wgpu::Buffer>,
    pub vertex_count: u32,
    pub index_count: u32,
    pub topology: Topology,
    version: u32,
}

#[derive(Debug)]
pub struct GpuTexture {
    pub texture: wgpu::Texture,
    pub view: wgpu::TextureView,
    pub sampler: wgpu::Sampler,
    version: u32,
}

#[derive(Debug)]
pub struct GpuMaterial {
    pub buffer: wgpu::Buffer,
    pub bind_group: wgpu::BindGroup,
    version: u32,
    /// Combined version of every bound texture, so the bind group is rebuilt
    /// when a texture is swapped or re-uploaded.
    texture_signature: u64,
}

/// Default 1x1 textures bound to material slots that have no texture.
#[derive(Debug)]
pub struct DefaultTextures {
    pub white_srgb: wgpu::TextureView,
    pub white_linear: wgpu::TextureView,
    pub black_srgb: wgpu::TextureView,
    /// Flat tangent space normal (0.5, 0.5, 1.0).
    pub flat_normal: wgpu::TextureView,
    pub sampler: wgpu::Sampler,
}

impl DefaultTextures {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        let make = |label: &str, pixel: [u8; 4], format: wgpu::TextureFormat| {
            let texture = device.create_texture_with_data(
                queue,
                &wgpu::TextureDescriptor {
                    label: Some(label),
                    size: wgpu::Extent3d {
                        width: 1,
                        height: 1,
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format,
                    usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                    view_formats: &[],
                },
                wgpu::util::TextureDataOrder::LayerMajor,
                &pixel,
            );
            texture.create_view(&wgpu::TextureViewDescriptor::default())
        };
        Self {
            white_srgb: make(
                "threenet.default.white_srgb",
                [255, 255, 255, 255],
                wgpu::TextureFormat::Rgba8UnormSrgb,
            ),
            white_linear: make(
                "threenet.default.white_linear",
                [255, 255, 255, 255],
                wgpu::TextureFormat::Rgba8Unorm,
            ),
            black_srgb: make(
                "threenet.default.black_srgb",
                [0, 0, 0, 255],
                wgpu::TextureFormat::Rgba8UnormSrgb,
            ),
            flat_normal: make(
                "threenet.default.flat_normal",
                [128, 128, 255, 255],
                wgpu::TextureFormat::Rgba8Unorm,
            ),
            sampler: device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("threenet.default.sampler"),
                address_mode_u: wgpu::AddressMode::Repeat,
                address_mode_v: wgpu::AddressMode::Repeat,
                address_mode_w: wgpu::AddressMode::Repeat,
                mag_filter: wgpu::FilterMode::Linear,
                min_filter: wgpu::FilterMode::Linear,
                mipmap_filter: wgpu::MipmapFilterMode::Linear,
                ..Default::default()
            }),
        }
    }
}

/// Renders mip levels by successively downsampling the previous level.
#[derive(Debug)]
pub struct MipmapGenerator {
    shader: wgpu::ShaderModule,
    layout: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    pipelines: HashMap<wgpu::TextureFormat, wgpu::RenderPipeline>,
}

impl MipmapGenerator {
    pub fn new(device: &wgpu::Device, shader: wgpu::ShaderModule) -> Self {
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.mipmap.layout"),
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
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("threenet.mipmap.sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::MipmapFilterMode::Nearest,
            ..Default::default()
        });
        Self {
            shader,
            layout,
            sampler,
            pipelines: HashMap::new(),
        }
    }

    fn pipeline(&mut self, device: &wgpu::Device, format: wgpu::TextureFormat) -> &wgpu::RenderPipeline {
        self.pipelines.entry(format).or_insert_with(|| {
            let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("threenet.mipmap.pipeline_layout"),
                bind_group_layouts: &[Some(&self.layout)],
                immediate_size: 0,
            });
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("threenet.mipmap.pipeline"),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: &self.shader,
                    entry_point: Some("vs_fullscreen"),
                    buffers: &[],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: &self.shader,
                    entry_point: Some("fs_copy"),
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

    pub fn generate(
        &mut self,
        device: &wgpu::Device,
        encoder: &mut wgpu::CommandEncoder,
        texture: &wgpu::Texture,
        format: wgpu::TextureFormat,
        mip_count: u32,
    ) {
        if mip_count <= 1 {
            return;
        }
        let pipeline = self.pipeline(device, format).clone();
        let views: Vec<wgpu::TextureView> = (0..mip_count)
            .map(|level| {
                texture.create_view(&wgpu::TextureViewDescriptor {
                    label: Some("threenet.mipmap.view"),
                    base_mip_level: level,
                    mip_level_count: Some(1),
                    ..Default::default()
                })
            })
            .collect();

        for level in 1..mip_count as usize {
            let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("threenet.mipmap.bind_group"),
                layout: &self.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(&views[level - 1]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::Sampler(&self.sampler),
                    },
                ],
            });
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("threenet.mipmap.pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &views[level],
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
            pass.set_pipeline(&pipeline);
            pass.set_bind_group(0, &bind_group, &[]);
            pass.draw(0..3, 0..1);
        }
    }
}

/// All GPU resources derived from a [`Scene`].
#[derive(Debug, Default)]
pub struct ResourceCache {
    meshes: HashMap<GeometryId, GpuMesh>,
    textures: HashMap<TextureId, GpuTexture>,
    materials: HashMap<MaterialId, GpuMaterial>,
}

impl ResourceCache {
    #[inline]
    pub fn mesh(&self, id: GeometryId) -> Option<&GpuMesh> {
        self.meshes.get(&id)
    }

    #[inline]
    pub fn material(&self, id: MaterialId) -> Option<&GpuMaterial> {
        self.materials.get(&id)
    }

    #[inline]
    pub fn texture(&self, id: TextureId) -> Option<&GpuTexture> {
        self.textures.get(&id)
    }

    pub fn clear(&mut self) {
        self.meshes.clear();
        self.textures.clear();
        self.materials.clear();
    }

    /// Drops cached entries whose scene resource no longer exists.
    pub fn retain_live(&mut self, scene: &Scene) {
        self.meshes.retain(|id, _| scene.geometry(*id).is_some());
        self.textures.retain(|id, _| scene.texture(*id).is_some());
        self.materials.retain(|id, _| scene.material(*id).is_some());
    }

    pub fn ensure_mesh(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        id: GeometryId,
        geometry: &Geometry,
    ) {
        if let Some(existing) = self.meshes.get(&id)
            && existing.version == geometry.version()
        {
            return;
        }
        if geometry.vertices.is_empty() {
            self.meshes.remove(&id);
            return;
        }

        // Reuse the buffers when the sizes still match; this is the common case
        // for animated or procedurally updated geometry.
        let vertex_bytes: &[u8] = bytemuck::cast_slice(&geometry.vertices);
        let index_bytes: &[u8] = bytemuck::cast_slice(&geometry.indices);
        let reusable = self
            .meshes
            .get(&id)
            .filter(|mesh| {
                mesh.vertex_buffer.size() as usize == vertex_bytes.len()
                    && mesh
                        .index_buffer
                        .as_ref()
                        .map(|b| b.size() as usize)
                        .unwrap_or(0)
                        == index_bytes.len()
            })
            .is_some();

        if reusable {
            let mesh = self.meshes.get_mut(&id).expect("checked above");
            queue.write_buffer(&mesh.vertex_buffer, 0, vertex_bytes);
            if let Some(index_buffer) = &mesh.index_buffer {
                queue.write_buffer(index_buffer, 0, index_bytes);
            }
            mesh.vertex_count = geometry.vertices.len() as u32;
            mesh.index_count = geometry.index_count();
            mesh.topology = geometry.topology;
            mesh.version = geometry.version();
            return;
        }

        let vertex_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("threenet.geometry.vertices"),
            contents: vertex_bytes,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
        });
        let index_buffer = (!geometry.indices.is_empty()).then(|| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("threenet.geometry.indices"),
                contents: index_bytes,
                usage: wgpu::BufferUsages::INDEX | wgpu::BufferUsages::COPY_DST,
            })
        });
        self.meshes.insert(
            id,
            GpuMesh {
                vertex_buffer,
                index_buffer,
                vertex_count: geometry.vertices.len() as u32,
                index_count: geometry.index_count(),
                topology: geometry.topology,
                version: geometry.version(),
            },
        );
    }

    pub fn ensure_texture(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        mipmaps: &mut MipmapGenerator,
        id: TextureId,
        texture: &Texture,
    ) {
        if let Some(existing) = self.textures.get(&id)
            && existing.version == texture.version()
        {
            return;
        }

        if let Some(compressed) = &texture.compressed {
            match crate::compressed::resolve(compressed, texture.width, texture.height, device.features()) {
                Ok(crate::compressed::Resolved::Blocks { format, levels }) => {
                    self.upload_blocks(device, queue, id, texture, format, &levels);
                    return;
                }
                Ok(crate::compressed::Resolved::Rgba8(pixels)) => {
                    let mut decoded = texture.clone();
                    decoded.compressed = None;
                    decoded.pixels = pixels;
                    decoded.format = match texture.format {
                        crate::texture::TextureFormat::Rgba8UnormSrgb => crate::texture::TextureFormat::Rgba8UnormSrgb,
                        _ => crate::texture::TextureFormat::Rgba8Unorm,
                    };
                    return self.ensure_texture(device, queue, encoder, mipmaps, id, &decoded);
                }
                Err(error) => {
                    log::error!("texture '{}' could not be decoded: {error}", texture.name);
                    let mut fallback = crate::texture::Texture::solid([255, 0, 255, 255], true);
                    fallback.version = texture.version();
                    return self.ensure_texture(device, queue, encoder, mipmaps, id, &fallback);
                }
            }
        }

        let format = texture.format.to_wgpu();
        let mip_level_count = texture.mip_level_count();
        // Mip generation renders into the higher levels, so they need to be
        // attachable as well as sampleable.
        let mut usage = wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST;
        if mip_level_count > 1 {
            usage |= wgpu::TextureUsages::RENDER_ATTACHMENT;
        }
        let size = wgpu::Extent3d {
            width: texture.width,
            height: texture.height,
            depth_or_array_layers: 1,
        };
        let gpu_texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some(if texture.name.is_empty() {
                "threenet.texture"
            } else {
                texture.name.as_str()
            }),
            size,
            mip_level_count,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage,
            view_formats: &[],
        });
        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &gpu_texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            &texture.pixels,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(texture.width * texture.format.bytes_per_pixel()),
                rows_per_image: Some(texture.height),
            },
            size,
        );
        if mip_level_count > 1 {
            mipmaps.generate(device, encoder, &gpu_texture, format, mip_level_count);
        }

        let view = gpu_texture.create_view(&wgpu::TextureViewDescriptor::default());
        let sampler = create_sampler(device, &texture.sampler, mip_level_count);
        self.textures.insert(
            id,
            GpuTexture {
                texture: gpu_texture,
                view,
                sampler,
                version: texture.version(),
            },
        );
    }

    /// Uploads a block compressed mip chain as is.
    fn upload_blocks(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        id: TextureId,
        texture: &Texture,
        format: crate::compressed::CompressedFormat,
        levels: &[Vec<u8>],
    ) {
        let srgb = texture.format == crate::texture::TextureFormat::Rgba8UnormSrgb;
        // Only the levels a full chain would have, and never more than supplied.
        let max_levels = 32 - texture.width.max(texture.height).leading_zeros();
        let mip_level_count = (levels.len() as u32).clamp(1, max_levels);
        let gpu_texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some(if texture.name.is_empty() { "threenet.texture.compressed" } else { texture.name.as_str() }),
            size: wgpu::Extent3d {
                width: texture.width,
                height: texture.height,
                depth_or_array_layers: 1,
            },
            mip_level_count,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: format.to_wgpu(srgb),
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        for (level, data) in levels.iter().take(mip_level_count as usize).enumerate() {
            let width = (texture.width >> level).max(1);
            let height = (texture.height >> level).max(1);
            let (blocks_x, blocks_y) = (width.div_ceil(4), height.div_ceil(4));
            let expected = blocks_x as usize * blocks_y as usize * format.block_bytes();
            if data.len() < expected {
                log::warn!("compressed level {level} of '{}' is short; remaining levels skipped", texture.name);
                break;
            }
            queue.write_texture(
                wgpu::TexelCopyTextureInfo {
                    texture: &gpu_texture,
                    mip_level: level as u32,
                    origin: wgpu::Origin3d::ZERO,
                    aspect: wgpu::TextureAspect::All,
                },
                &data[..expected],
                wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(blocks_x * format.block_bytes() as u32),
                    rows_per_image: Some(blocks_y),
                },
                // Copies of block formats use the physical (block aligned) size.
                wgpu::Extent3d {
                    width: blocks_x * 4,
                    height: blocks_y * 4,
                    depth_or_array_layers: 1,
                },
            );
        }

        let view = gpu_texture.create_view(&wgpu::TextureViewDescriptor::default());
        let sampler = create_sampler(device, &texture.sampler, mip_level_count);
        self.textures.insert(
            id,
            GpuTexture {
                texture: gpu_texture,
                view,
                sampler,
                version: texture.version(),
            },
        );
    }

    /// Creates or refreshes the material uniform buffer and bind group.
    #[allow(clippy::too_many_arguments)]
    pub fn ensure_material(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        layout: &wgpu::BindGroupLayout,
        defaults: &DefaultTextures,
        scene: &Scene,
        id: MaterialId,
        material: &Material,
    ) {
        let signature = self.texture_signature(scene, material);
        if let Some(existing) = self.materials.get(&id)
            && existing.version == material.version()
            && existing.texture_signature == signature
        {
            return;
        }

        let uniform = MaterialUniform::from(material);
        let buffer = match self.materials.get(&id) {
            Some(existing) => {
                queue.write_buffer(&existing.buffer, 0, bytemuck::bytes_of(&uniform));
                existing.buffer.clone()
            }
            None => device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("threenet.material.uniform"),
                contents: bytemuck::bytes_of(&uniform),
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            }),
        };

        let textures = &self.textures;
        let base_color = view_or(textures, material.textures.base_color, &defaults.white_srgb);
        let normal = view_or(textures, material.textures.normal, &defaults.flat_normal);
        let metallic_roughness = view_or(
            textures,
            material.textures.metallic_roughness,
            &defaults.white_linear,
        );
        let emissive = view_or(textures, material.textures.emissive, &defaults.white_srgb);
        let occlusion = view_or(
            textures,
            material.textures.occlusion,
            &defaults.white_linear,
        );
        let sampler = material
            .textures
            .base_color
            .and_then(|id| self.textures.get(&id))
            .map(|t| &t.sampler)
            .unwrap_or(&defaults.sampler);

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("threenet.material.bind_group"),
            layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(base_color),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(normal),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::TextureView(metallic_roughness),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: wgpu::BindingResource::TextureView(emissive),
                },
                wgpu::BindGroupEntry {
                    binding: 5,
                    resource: wgpu::BindingResource::TextureView(occlusion),
                },
                wgpu::BindGroupEntry {
                    binding: 6,
                    resource: wgpu::BindingResource::Sampler(sampler),
                },
            ],
        });

        self.materials.insert(
            id,
            GpuMaterial {
                buffer,
                bind_group,
                version: material.version(),
                texture_signature: signature,
            },
        );
    }

    fn texture_signature(&self, scene: &Scene, material: &Material) -> u64 {
        let mut signature = 0u64;
        for slot in [
            material.textures.base_color,
            material.textures.normal,
            material.textures.metallic_roughness,
            material.textures.emissive,
            material.textures.occlusion,
        ] {
            let value = match slot {
                Some(id) => {
                    let version = scene.texture(id).map(|t| t.version()).unwrap_or(0);
                    ((id as u64) << 32) | version as u64
                }
                None => 0,
            };
            // FNV style mixing; collisions only cost an extra bind group rebuild.
            signature = signature.rotate_left(11) ^ value.wrapping_mul(0x9E37_79B9_7F4A_7C15);
        }
        signature
    }
}

/// Resolves a material texture slot, falling back to a default 1x1 texture.
fn view_or<'a>(
    textures: &'a HashMap<TextureId, GpuTexture>,
    id: Option<TextureId>,
    fallback: &'a wgpu::TextureView,
) -> &'a wgpu::TextureView {
    id.and_then(|id| textures.get(&id))
        .map(|t| &t.view)
        .unwrap_or(fallback)
}

pub fn create_sampler(
    device: &wgpu::Device,
    desc: &SamplerDesc,
    mip_level_count: u32,
) -> wgpu::Sampler {
    let filter = if desc.linear_filter {
        wgpu::FilterMode::Linear
    } else {
        wgpu::FilterMode::Nearest
    };
    // Anisotropic filtering requires linear min/mag/mip filtering.
    let anisotropy = if desc.linear_filter && desc.mipmaps && mip_level_count > 1 {
        desc.anisotropy.clamp(1, 16)
    } else {
        1
    };
    device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("threenet.sampler"),
        address_mode_u: desc.wrap_u.to_wgpu(),
        address_mode_v: desc.wrap_v.to_wgpu(),
        address_mode_w: wgpu::AddressMode::Repeat,
        mag_filter: filter,
        min_filter: filter,
        mipmap_filter: if desc.mipmaps {
            wgpu::MipmapFilterMode::Linear
        } else {
            wgpu::MipmapFilterMode::Nearest
        },
        anisotropy_clamp: anisotropy,
        ..Default::default()
    })
}
