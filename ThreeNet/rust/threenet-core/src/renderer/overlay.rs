//! GPU side of the screen space overlay: builds one vertex buffer per frame,
//! rasterises glyphs into an atlas and draws batches grouped by texture.

use std::collections::HashMap;

use ab_glyph::{Font, GlyphId, PxScale, ScaleFont};
use bytemuck::{Pod, Zeroable};

use crate::overlay::{OverlayContent, Rect, TextAlign};
use crate::renderer::resources::{DefaultTextures, ResourceCache};
use crate::scene::{Scene, TextureId};

const ATLAS_SIZE: u32 = 1024;

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct OverlayVertex {
    position: [f32; 2],
    uv: [f32; 2],
    color: [f32; 4],
    local: [f32; 4],
    border_color: [f32; 4],
    params: [f32; 4],
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct OverlayUniform {
    screen: [f32; 4],
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
enum BatchTexture {
    White,
    Atlas,
    Scene(TextureId),
}

#[derive(Debug, Clone, Copy)]
struct GlyphEntry {
    /// UV rect in the atlas.
    uv: [f32; 4],
    /// Pixel bounds relative to the pen position on the baseline.
    min: [f32; 2],
    size: [f32; 2],
}

#[derive(Debug)]
struct GlyphAtlas {
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    entries: HashMap<(u32, u16, u32), Option<GlyphEntry>>,
    cursor: [u32; 2],
    row_height: u32,
}

impl GlyphAtlas {
    fn new(device: &wgpu::Device) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("threenet.overlay.atlas"),
            size: wgpu::Extent3d { width: ATLAS_SIZE, height: ATLAS_SIZE, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::R8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        Self { texture, view, entries: HashMap::new(), cursor: [1, 1], row_height: 0 }
    }

    fn reset(&mut self) {
        self.entries.clear();
        self.cursor = [1, 1];
        self.row_height = 0;
    }

    /// Rasterises a glyph on first use. `None` for blank glyphs (spaces) or when the atlas is full.
    fn glyph(&mut self, queue: &wgpu::Queue, font_index: u32, font: &ab_glyph::FontArc, glyph: GlyphId, size: f32) -> Option<GlyphEntry> {
        let key = (font_index, glyph.0, (size * 4.0).round() as u32);
        if let Some(entry) = self.entries.get(&key) {
            return *entry;
        }
        let outlined = font.outline_glyph(glyph.with_scale_and_position(PxScale::from(size), ab_glyph::point(0.0, 0.0)));
        let Some(outlined) = outlined else {
            self.entries.insert(key, None);
            return None;
        };
        let bounds = outlined.px_bounds();
        let width = bounds.width().ceil() as u32;
        let height = bounds.height().ceil() as u32;
        if width == 0 || height == 0 || width + 2 > ATLAS_SIZE || height + 2 > ATLAS_SIZE {
            self.entries.insert(key, None);
            return None;
        }
        if self.cursor[0] + width + 1 > ATLAS_SIZE {
            self.cursor = [1, self.cursor[1] + self.row_height + 1];
            self.row_height = 0;
        }
        if self.cursor[1] + height + 1 > ATLAS_SIZE {
            // Full: the caller resets the atlas and retries next frame.
            return None;
        }
        let mut pixels = vec![0u8; (width * height) as usize];
        outlined.draw(|x, y, coverage| {
            if x < width && y < height {
                pixels[(y * width + x) as usize] = (coverage.clamp(0.0, 1.0) * 255.0) as u8;
            }
        });
        let [x, y] = self.cursor;
        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &self.texture,
                mip_level: 0,
                origin: wgpu::Origin3d { x, y, z: 0 },
                aspect: wgpu::TextureAspect::All,
            },
            &pixels,
            wgpu::TexelCopyBufferLayout { offset: 0, bytes_per_row: Some(width), rows_per_image: Some(height) },
            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
        );
        self.cursor[0] += width + 1;
        self.row_height = self.row_height.max(height);
        let atlas = ATLAS_SIZE as f32;
        let entry = GlyphEntry {
            uv: [x as f32 / atlas, y as f32 / atlas, (x + width) as f32 / atlas, (y + height) as f32 / atlas],
            min: [bounds.min.x, bounds.min.y],
            size: [width as f32, height as f32],
        };
        self.entries.insert(key, Some(entry));
        Some(entry)
    }
}

#[derive(Debug)]
pub struct OverlayRenderer {
    shader: wgpu::ShaderModule,
    uniform_layout: wgpu::BindGroupLayout,
    texture_layout: wgpu::BindGroupLayout,
    pipelines: HashMap<wgpu::TextureFormat, wgpu::RenderPipeline>,
    uniform_buffer: wgpu::Buffer,
    uniform_bind_group: wgpu::BindGroup,
    sampler: wgpu::Sampler,
    atlas: GlyphAtlas,
    vertex_buffer: Option<(wgpu::Buffer, u64)>,
}

impl OverlayRenderer {
    pub fn new(device: &wgpu::Device) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.overlay.shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/overlay.wgsl").into()),
        });
        let uniform_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.overlay.uniform_layout"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                count: None,
            }],
        });
        let texture_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("threenet.overlay.texture_layout"),
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
        let uniform_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("threenet.overlay.uniform"),
            size: std::mem::size_of::<OverlayUniform>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let uniform_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("threenet.overlay.uniform_group"),
            layout: &uniform_layout,
            entries: &[wgpu::BindGroupEntry { binding: 0, resource: uniform_buffer.as_entire_binding() }],
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("threenet.overlay.sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            ..Default::default()
        });
        Self {
            shader,
            uniform_layout,
            texture_layout,
            pipelines: HashMap::new(),
            uniform_buffer,
            uniform_bind_group,
            sampler,
            atlas: GlyphAtlas::new(device),
            vertex_buffer: None,
        }
    }

    fn pipeline(&mut self, device: &wgpu::Device, format: wgpu::TextureFormat) -> &wgpu::RenderPipeline {
        self.pipelines.entry(format).or_insert_with(|| {
            let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("threenet.overlay.pipeline_layout"),
                bind_group_layouts: &[Some(&self.uniform_layout), Some(&self.texture_layout)],
                immediate_size: 0,
            });
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("threenet.overlay.pipeline"),
                layout: Some(&layout),
                vertex: wgpu::VertexState {
                    module: &self.shader,
                    entry_point: Some("vs_overlay"),
                    compilation_options: Default::default(),
                    buffers: &[Some(wgpu::VertexBufferLayout {
                        array_stride: std::mem::size_of::<OverlayVertex>() as u64,
                        step_mode: wgpu::VertexStepMode::Vertex,
                        attributes: &wgpu::vertex_attr_array![
                            0 => Float32x2, 1 => Float32x2, 2 => Float32x4,
                            3 => Float32x4, 4 => Float32x4, 5 => Float32x4
                        ],
                    })],
                },
                fragment: Some(wgpu::FragmentState {
                    module: &self.shader,
                    entry_point: Some("fs_overlay"),
                    compilation_options: Default::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format,
                        blend: Some(wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                primitive: wgpu::PrimitiveState::default(),
                depth_stencil: None,
                multisample: wgpu::MultisampleState::default(),
                multiview_mask: None,
                cache: None,
            })
        })
    }

    /// Draws the scene overlay on top of `target`. Returns the number of batches.
    #[allow(clippy::too_many_arguments)]
    pub fn draw(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        format: wgpu::TextureFormat,
        width: u32,
        height: u32,
        scene: &Scene,
        resources: &ResourceCache,
        defaults: &DefaultTextures,
    ) -> u32 {
        let overlay = &scene.overlay;
        if !overlay.enabled || overlay.is_empty() || width == 0 || height == 0 {
            return 0;
        }

        let mut vertices: Vec<OverlayVertex> = Vec::new();
        let mut batches: Vec<(BatchTexture, u32, u32)> = Vec::new();
        let push_quad = |vertices: &mut Vec<OverlayVertex>, batches: &mut Vec<(BatchTexture, u32, u32)>, texture: BatchTexture, corners: [OverlayVertex; 4]| {
            let start = vertices.len() as u32;
            let [a, b, c, d] = corners;
            vertices.extend_from_slice(&[a, b, c, a, c, d]);
            match batches.last_mut() {
                Some((last, _, count)) if *last == texture => *count += 6,
                _ => batches.push((texture, start, 6)),
            }
        };

        let mut atlas_full = false;
        for (id, rect) in overlay.layout(width as f32, height as f32) {
            let Some(element) = overlay.get(id) else { continue };
            let scale = overlay.scale.max(0.01);
            match &element.content {
                OverlayContent::Panel | OverlayContent::Image { .. } => {
                    let (texture, uv) = match &element.content {
                        OverlayContent::Image { texture, uv } if resources.texture(*texture).is_some() => (BatchTexture::Scene(*texture), *uv),
                        // An image whose texture is not uploaded yet draws nothing.
                        OverlayContent::Image { .. } => continue,
                        _ => (BatchTexture::White, [0.0, 0.0, 1.0, 1.0]),
                    };
                    let mode = if matches!(texture, BatchTexture::White) { 0.0 } else { 1.0 };
                    let has_border = element.border_width > 0.0 && element.border_color[3] > 0.0;
                    if element.color[3] <= 0.0 && !has_border {
                        continue;
                    }
                    let params = [element.corner_radius * scale, element.border_width * scale, mode, 0.0];
                    push_quad(&mut vertices, &mut batches, texture, rect_quad(rect, uv, element.color, element.border_color, params));
                }
                OverlayContent::Text { text, font, size, align, vertical_align, wrap } => {
                    let Some(font_ref) = overlay.font(*font) else { continue };
                    let pixel_size = size * scale;
                    let limit = (*wrap && element.size[0] > 0.0).then_some(rect.width);
                    let layout = overlay.layout_text(*font, pixel_size, text, limit);
                    let origin_x = rect.x + (rect.width - layout.width) * align_fraction(*align);
                    let origin_y = rect.y + (rect.height - layout.height) * align_fraction(*vertical_align);
                    for placed in &layout.glyphs {
                        let Some(entry) = self.atlas.glyph(queue, *font, font_ref, placed.glyph, pixel_size) else {
                            if self.atlas.cursor[1] + self.atlas.row_height + 1 >= ATLAS_SIZE {
                                atlas_full = true;
                            }
                            continue;
                        };
                        // Snap to whole pixels so glyphs stay crisp.
                        let x = (origin_x + placed.x + entry.min[0]).round();
                        let y = (origin_y + placed.baseline + entry.min[1]).round();
                        let glyph_rect = Rect { x, y, width: entry.size[0], height: entry.size[1] };
                        push_quad(
                            &mut vertices,
                            &mut batches,
                            BatchTexture::Atlas,
                            rect_quad(glyph_rect, entry.uv, element.color, [0.0; 4], [0.0, 0.0, 2.0, 0.0]),
                        );
                    }
                }
            }
        }
        if atlas_full {
            // Start over next frame with only the glyphs still on screen.
            self.atlas.reset();
        }
        if vertices.is_empty() {
            return 0;
        }

        let bytes: &[u8] = bytemuck::cast_slice(&vertices);
        let needed = bytes.len() as u64;
        if self.vertex_buffer.as_ref().is_none_or(|(_, capacity)| *capacity < needed) {
            let capacity = needed.next_power_of_two().max(4096);
            let buffer = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("threenet.overlay.vertices"),
                size: capacity,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            self.vertex_buffer = Some((buffer, capacity));
        }
        let (vertex_buffer, _) = self.vertex_buffer.as_ref().expect("allocated above");
        queue.write_buffer(vertex_buffer, 0, bytes);
        let srgb = if format.is_srgb() { 1.0 } else { 0.0 };
        queue.write_buffer(
            &self.uniform_buffer,
            0,
            bytemuck::bytes_of(&OverlayUniform { screen: [width as f32, height as f32, srgb, 0.0] }),
        );

        let mut groups: HashMap<BatchTexture, wgpu::BindGroup> = HashMap::new();
        for (texture, _, _) in &batches {
            if groups.contains_key(texture) {
                continue;
            }
            let view = match texture {
                BatchTexture::White => &defaults.white_linear,
                BatchTexture::Atlas => &self.atlas.view,
                BatchTexture::Scene(id) => match resources.texture(*id) {
                    Some(gpu) => &gpu.view,
                    None => continue,
                },
            };
            let group = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("threenet.overlay.texture_group"),
                layout: &self.texture_layout,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(view) },
                    wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.sampler) },
                ],
            });
            groups.insert(*texture, group);
        }

        self.pipeline(device, format);
        let pipeline = &self.pipelines[&format];
        let (vertex_buffer, _) = self.vertex_buffer.as_ref().expect("allocated above");
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("threenet.overlay.pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: target,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations { load: wgpu::LoadOp::Load, store: wgpu::StoreOp::Store },
            })],
            depth_stencil_attachment: None,
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, &self.uniform_bind_group, &[]);
        pass.set_vertex_buffer(0, vertex_buffer.slice(..needed));
        let mut drawn = 0;
        for (texture, start, count) in &batches {
            let Some(group) = groups.get(texture) else { continue };
            pass.set_bind_group(1, group, &[]);
            pass.draw(*start..start + count, 0..1);
            drawn += 1;
        }
        drawn
    }
}

fn align_fraction(align: TextAlign) -> f32 {
    match align {
        TextAlign::Start => 0.0,
        TextAlign::Center => 0.5,
        TextAlign::End => 1.0,
    }
}

fn rect_quad(rect: Rect, uv: [f32; 4], color: [f32; 4], border: [f32; 4], params: [f32; 4]) -> [OverlayVertex; 4] {
    let half = [rect.width * 0.5, rect.height * 0.5];
    let corner = |fx: f32, fy: f32| OverlayVertex {
        position: [rect.x + rect.width * fx, rect.y + rect.height * fy],
        uv: [uv[0] + (uv[2] - uv[0]) * fx, uv[1] + (uv[3] - uv[1]) * fy],
        color,
        local: [(fx - 0.5) * rect.width, (fy - 0.5) * rect.height, half[0], half[1]],
        border_color: border,
        params,
    };
    [corner(0.0, 0.0), corner(1.0, 0.0), corner(1.0, 1.0), corner(0.0, 1.0)]
}

// Silence the unused trait import on targets where ScaleFont is not needed.
#[allow(dead_code)]
fn _uses_scale_font<F: Font>(font: &F) -> f32 {
    font.as_scaled(PxScale::from(1.0)).ascent()
}
