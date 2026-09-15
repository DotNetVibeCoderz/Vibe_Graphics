//! The wgpu backed renderer: device management, the forward scene pass and the
//! post-processing chain.

pub mod pipeline;
pub mod post;
pub mod resources;
pub mod uniforms;

use std::time::Instant;

use wgpu::util::DeviceExt;

use crate::camera::Camera;
use crate::error::{Error, Result};
use crate::math::{Frustum, Mat4, Vec3};
use crate::renderer::pipeline::{DEPTH_FORMAT, HDR_FORMAT, Layouts, PipelineCache, PipelineKey};
use crate::renderer::post::{PostProcess, PostSettings};
use crate::renderer::resources::{DefaultTextures, MipmapGenerator, ResourceCache};
use crate::renderer::uniforms::{
    FrameUniform, LightUniform, LightsUniform, MAX_LIGHTS, ObjectUniform,
};
use crate::scene::{GeometryId, MaterialId, NodeId, Scene};

pub use crate::renderer::post::ToneMapping;

/// Adapter selection hint.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(u32)]
pub enum PowerPreference {
    #[default]
    HighPerformance = 0,
    LowPower = 1,
}

impl PowerPreference {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => PowerPreference::LowPower,
            _ => PowerPreference::HighPerformance,
        }
    }

    fn to_wgpu(self) -> wgpu::PowerPreference {
        match self {
            PowerPreference::HighPerformance => wgpu::PowerPreference::HighPerformance,
            PowerPreference::LowPower => wgpu::PowerPreference::LowPower,
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct RendererConfig {
    pub width: u32,
    pub height: u32,
    pub vsync: bool,
    /// 1, 2, 4 or 8. Silently clamped to what the adapter supports.
    pub msaa_samples: u32,
    pub exposure: f32,
    pub tone_mapping: ToneMapping,
    pub bloom: bool,
    pub bloom_intensity: f32,
    pub bloom_threshold: f32,
    pub frustum_culling: bool,
    pub power_preference: PowerPreference,
    /// Offscreen renderers only: produce BGRA instead of RGBA pixels, which is
    /// what most UI toolkits (Avalonia, WPF, WinForms) expect from a bitmap.
    pub bgra_output: bool,
}

impl Default for RendererConfig {
    fn default() -> Self {
        Self {
            width: 1280,
            height: 720,
            vsync: true,
            msaa_samples: 4,
            exposure: 1.0,
            tone_mapping: ToneMapping::Aces,
            bloom: false,
            bloom_intensity: 0.6,
            bloom_threshold: 1.0,
            frustum_culling: true,
            power_preference: PowerPreference::HighPerformance,
            bgra_output: false,
        }
    }
}

/// Per frame counters, useful for the gallery HUD and for profiling.
#[derive(Debug, Clone, Copy, Default)]
pub struct FrameStats {
    pub draw_calls: u32,
    pub triangles: u32,
    pub visible_nodes: u32,
    pub culled_nodes: u32,
    pub lights: u32,
    pub cpu_time_ms: f32,
}

#[derive(Debug, Clone, Copy)]
struct DrawItem {
    geometry: GeometryId,
    material: MaterialId,
    world: Mat4,
    depth: f32,
    transparent: bool,
    render_order: i32,
    object_offset: u32,
}

/// Render targets that depend on the surface size.
#[derive(Debug)]
struct Targets {
    hdr: wgpu::TextureView,
    depth: wgpu::TextureView,
    /// Multisampled colour buffer, resolved into `hdr`.
    msaa: Option<wgpu::TextureView>,
    /// Offscreen output plus its readback staging buffer.
    offscreen: Option<wgpu::Texture>,
    offscreen_view: Option<wgpu::TextureView>,
}

pub struct Renderer {
    instance: wgpu::Instance,
    adapter: wgpu::Adapter,
    device: wgpu::Device,
    queue: wgpu::Queue,
    surface: Option<wgpu::Surface<'static>>,
    surface_config: Option<wgpu::SurfaceConfiguration>,
    output_format: wgpu::TextureFormat,
    config: RendererConfig,
    samples: u32,
    layouts: Layouts,
    pipelines: PipelineCache,
    post: PostProcess,
    mipmaps: MipmapGenerator,
    resources: ResourceCache,
    defaults: DefaultTextures,
    targets: Targets,
    frame_buffer: wgpu::Buffer,
    lights_buffer: wgpu::Buffer,
    object_buffer: wgpu::Buffer,
    object_capacity: u32,
    object_stride: u32,
    frame_bind_group: wgpu::BindGroup,
    frame_bind_group_environment: Option<u32>,
    object_bind_group: wgpu::BindGroup,
    draw_items: Vec<DrawItem>,
    object_data: Vec<u8>,
    stats: FrameStats,
    start: Instant,
}

impl std::fmt::Debug for Renderer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Renderer")
            .field("size", &(self.config.width, self.config.height))
            .field("format", &self.output_format)
            .field("samples", &self.samples)
            .field("offscreen", &self.surface.is_none())
            .finish()
    }
}

impl Renderer {
    /// Creates a renderer drawing into a platform window.
    pub fn new_with_surface(
        target: wgpu::SurfaceTarget<'static>,
        config: RendererConfig,
    ) -> Result<Self> {
        let instance = create_instance();
        let surface = instance
            .create_surface(target)
            .map_err(|e| Error::Surface(e.to_string()))?;
        Self::build(instance, Some(surface), config)
    }

    /// Creates a renderer drawing into a window identified by raw handles.
    /// Used by the .NET hosts (Avalonia, WinForms, WPF) which own the window.
    ///
    /// # Safety
    /// The handles must stay valid for the whole lifetime of the renderer.
    pub unsafe fn new_with_raw_handles(
        display: raw_window_handle::RawDisplayHandle,
        window: raw_window_handle::RawWindowHandle,
        config: RendererConfig,
    ) -> Result<Self> {
        let instance = create_instance();
        let surface = unsafe {
            instance.create_surface_unsafe(wgpu::SurfaceTargetUnsafe::RawHandle {
                raw_display_handle: Some(display),
                raw_window_handle: window,
            })
        }
        .map_err(|e| Error::Surface(e.to_string()))?;
        Self::build(instance, Some(surface), config)
    }

    /// Creates a headless renderer that draws into a texture. The result can be
    /// read back with [`Renderer::read_pixels`], which is how the Avalonia
    /// control and the screenshot tooling get their frames.
    pub fn new_offscreen(config: RendererConfig) -> Result<Self> {
        let instance = create_instance();
        Self::build(instance, None, config)
    }

    fn build(
        instance: wgpu::Instance,
        surface: Option<wgpu::Surface<'static>>,
        config: RendererConfig,
    ) -> Result<Self> {
        let adapter = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: config.power_preference.to_wgpu(),
            compatible_surface: surface.as_ref(),
            force_fallback_adapter: false,
            ..Default::default()
        }))
        .map_err(|_| Error::NoAdapter)?;

        let mut features = wgpu::Features::empty();
        let polygon_mode_line = adapter
            .features()
            .contains(wgpu::Features::POLYGON_MODE_LINE);
        if polygon_mode_line {
            features |= wgpu::Features::POLYGON_MODE_LINE;
        }

        let (device, queue) = pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
            label: Some("threenet.device"),
            required_features: features,
            required_limits: adapter.limits(),
            experimental_features: wgpu::ExperimentalFeatures::disabled(),
            memory_hints: wgpu::MemoryHints::Performance,
            trace: wgpu::Trace::Off,
        }))
        .map_err(|e| Error::Device(e.to_string()))?;

        let width = config.width.max(1);
        let height = config.height.max(1);

        let (surface_config, output_format) = match &surface {
            Some(surface) => {
                let caps = surface.get_capabilities(&adapter);
                let format = caps
                    .formats
                    .iter()
                    .copied()
                    .find(|f| f.is_srgb())
                    .unwrap_or(caps.formats[0]);
                let present_mode = if config.vsync {
                    wgpu::PresentMode::AutoVsync
                } else {
                    wgpu::PresentMode::AutoNoVsync
                };
                let surface_config = wgpu::SurfaceConfiguration {
                    usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
                    format,
                    width,
                    height,
                    present_mode,
                    alpha_mode: caps.alpha_modes[0],
                    view_formats: vec![],
                    desired_maximum_frame_latency: 2,
                    color_space: wgpu::SurfaceColorSpace::Auto,
                };
                surface.configure(&device, &surface_config);
                (Some(surface_config), format)
            }
            None if config.bgra_output => (None, wgpu::TextureFormat::Bgra8UnormSrgb),
            None => (None, wgpu::TextureFormat::Rgba8UnormSrgb),
        };

        let samples = clamp_samples(&adapter, config.msaa_samples);
        let layouts = Layouts::new(&device);
        let scene_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.scene"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/scene.wgsl").into()),
        });
        let post_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.post"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/post.wgsl").into()),
        });
        let post_shader_for_mips = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.mipmap"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/post.wgsl").into()),
        });

        let pipelines = PipelineCache::new(&device, &layouts, scene_shader, polygon_mode_line);
        let mut post = PostProcess::new(&device, post_shader);
        post.resize(&device, width, height, config.bloom);
        let mipmaps = MipmapGenerator::new(&device, post_shader_for_mips);
        let defaults = DefaultTextures::new(&device, &queue);

        let frame_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("threenet.frame.uniform"),
            contents: bytemuck::bytes_of(&FrameUniform::zeroed()),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let lights_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("threenet.lights.uniform"),
            contents: bytemuck::bytes_of(&LightsUniform::default()),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        let object_stride = object_stride(&device);
        let object_capacity = 256u32;
        let object_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("threenet.object.uniform"),
            size: (object_stride * object_capacity) as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let object_bind_group = create_object_bind_group(&device, &layouts, &object_buffer);
        let frame_bind_group = create_frame_bind_group(
            &device,
            &layouts,
            &frame_buffer,
            &lights_buffer,
            &defaults.black_srgb,
            &defaults.sampler,
        );

        let targets = create_targets(&device, width, height, samples, output_format, surface.is_none());

        Ok(Self {
            instance,
            adapter,
            device,
            queue,
            surface,
            surface_config,
            output_format,
            config,
            samples,
            layouts,
            pipelines,
            post,
            mipmaps,
            resources: ResourceCache::default(),
            defaults,
            targets,
            frame_buffer,
            lights_buffer,
            object_buffer,
            object_capacity,
            object_stride,
            frame_bind_group,
            frame_bind_group_environment: None,
            object_bind_group,
            draw_items: Vec::new(),
            object_data: Vec::new(),
            stats: FrameStats::default(),
            start: Instant::now(),
        })
    }

    #[inline]
    pub fn width(&self) -> u32 {
        self.config.width
    }

    #[inline]
    pub fn height(&self) -> u32 {
        self.config.height
    }

    #[inline]
    pub fn aspect_ratio(&self) -> f32 {
        self.config.width as f32 / self.config.height.max(1) as f32
    }

    #[inline]
    pub fn stats(&self) -> FrameStats {
        self.stats
    }

    #[inline]
    pub fn config(&self) -> RendererConfig {
        self.config
    }

    pub fn adapter_name(&self) -> String {
        let info = self.adapter.get_info();
        format!("{} ({:?}, {:?})", info.name, info.device_type, info.backend)
    }

    /// Applies new settings. Changing MSAA or bloom reallocates the targets.
    pub fn set_config(&mut self, config: RendererConfig) {
        let samples = clamp_samples(&self.adapter, config.msaa_samples);
        let size_changed = config.width != self.config.width || config.height != self.config.height;
        let samples_changed = samples != self.samples;
        let vsync_changed = config.vsync != self.config.vsync;
        self.config = config;
        self.samples = samples;
        if samples_changed {
            // Pipelines bake the sample count in.
            self.pipelines.clear();
        }
        if size_changed || samples_changed {
            self.rebuild_targets();
        }
        if vsync_changed && let Some(surface_config) = &mut self.surface_config {
            surface_config.present_mode = if self.config.vsync {
                wgpu::PresentMode::AutoVsync
            } else {
                wgpu::PresentMode::AutoNoVsync
            };
            if let Some(surface) = &self.surface {
                surface.configure(&self.device, surface_config);
            }
        }
        self.post
            .resize(&self.device, self.config.width, self.config.height, self.config.bloom);
    }

    pub fn resize(&mut self, width: u32, height: u32) {
        let (width, height) = (width.max(1), height.max(1));
        if width == self.config.width && height == self.config.height {
            return;
        }
        self.config.width = width;
        self.config.height = height;
        if let (Some(surface), Some(surface_config)) = (&self.surface, &mut self.surface_config) {
            surface_config.width = width;
            surface_config.height = height;
            surface.configure(&self.device, surface_config);
        }
        self.rebuild_targets();
        self.post.resize(&self.device, width, height, self.config.bloom);
    }

    fn rebuild_targets(&mut self) {
        self.targets = create_targets(
            &self.device,
            self.config.width,
            self.config.height,
            self.samples,
            self.output_format,
            self.surface.is_none(),
        );
    }

    /// Drops every cached GPU resource; the next frame re-uploads what it needs.
    pub fn invalidate_resources(&mut self) {
        self.resources.clear();
    }

    /// Renders `scene` from `camera_node` (or the scene active camera).
    pub fn render(&mut self, scene: &mut Scene, camera_node: Option<NodeId>) -> Result<()> {
        let cpu_start = Instant::now();
        let camera_node = camera_node
            .or_else(|| scene.active_camera())
            .or_else(|| {
                scene
                    .nodes()
                    .find(|(_, node)| node.camera.is_some())
                    .map(|(id, _)| id)
            })
            .ok_or(Error::InvalidArgument("the scene has no camera".into()))?;

        scene.update_world_transforms();
        let camera_world = scene
            .node(camera_node)
            .map(|node| node.world_matrix())
            .ok_or(Error::InvalidHandle("camera node"))?;
        let camera = scene
            .node(camera_node)
            .and_then(|node| node.camera)
            .unwrap_or_else(|| Camera::perspective(std::f32::consts::FRAC_PI_4, 0.1, 1000.0));
        let camera_layers = scene.node(camera_node).map(|n| n.layers).unwrap_or(u32::MAX);

        let view = camera_world.inverse();
        let projection = camera.projection_matrix(self.aspect_ratio());
        let frustum = Frustum::from_view_projection(&(projection * view));
        let camera_position = camera_world.w_axis.truncate();

        // ---------------------------------------------------------- gather
        let lights = self.collect_lights(scene);
        self.collect_draw_items(scene, &frustum, camera_position, camera_layers);

        // ------------------------------------------------------- gpu upload
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("threenet.frame"),
            });

        self.resources.retain_live(scene);
        self.upload_resources(scene, &mut encoder);
        self.upload_frame_uniforms(
            scene,
            &view,
            &projection,
            camera_position,
            camera.near(),
            camera.far(),
            &lights,
        );
        self.upload_object_uniforms();

        // ------------------------------------------------------- scene pass
        let background = scene.environment.background;
        {
            let (color_view, resolve_target) = match &self.targets.msaa {
                Some(msaa) => (msaa, Some(&self.targets.hdr)),
                None => (&self.targets.hdr, None),
            };
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("threenet.pass.forward"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: color_view,
                    depth_slice: None,
                    resolve_target,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color {
                            r: background[0] as f64,
                            g: background[1] as f64,
                            b: background[2] as f64,
                            a: background[3] as f64,
                        }),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: &self.targets.depth,
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

            pass.set_bind_group(0, &self.frame_bind_group, &[]);

            let mut draw_calls = 0u32;
            let mut triangles = 0u32;
            let mut current_material = u32::MAX;
            let mut current_pipeline = None;

            for item in &self.draw_items {
                let Some(mesh) = self.resources.mesh(item.geometry) else {
                    continue;
                };
                let Some(material) = scene.material(item.material) else {
                    continue;
                };
                let Some(gpu_material) = self.resources.material(item.material) else {
                    continue;
                };

                let key = PipelineKey::for_material(
                    material,
                    mesh.topology,
                    self.samples,
                    HDR_FORMAT,
                );
                if current_pipeline != Some(key) {
                    let pipeline = self.pipelines.get(&self.device, key);
                    pass.set_pipeline(pipeline);
                    current_pipeline = Some(key);
                }
                if current_material != item.material {
                    pass.set_bind_group(1, &gpu_material.bind_group, &[]);
                    current_material = item.material;
                }
                pass.set_bind_group(2, &self.object_bind_group, &[item.object_offset]);
                pass.set_vertex_buffer(0, mesh.vertex_buffer.slice(..));
                match &mesh.index_buffer {
                    Some(index_buffer) => {
                        pass.set_index_buffer(index_buffer.slice(..), wgpu::IndexFormat::Uint32);
                        pass.draw_indexed(0..mesh.index_count, 0, 0..1);
                    }
                    None => pass.draw(0..mesh.vertex_count, 0..1),
                }
                draw_calls += 1;
                triangles += mesh.index_count / 3;
            }

            self.stats.draw_calls = draw_calls;
            self.stats.triangles = triangles;
            self.stats.lights = lights.len() as u32;
        }

        // ---------------------------------------------------- post + present
        let settings = PostSettings {
            exposure: self.config.exposure,
            tone_mapping: self.config.tone_mapping,
            bloom_intensity: if self.config.bloom {
                self.config.bloom_intensity
            } else {
                0.0
            },
            bloom_threshold: self.config.bloom_threshold,
            bloom_iterations: 2,
        };

        match &self.surface {
            Some(surface) => {
                let frame = match surface.get_current_texture() {
                    wgpu::CurrentSurfaceTexture::Success(frame) => Some(frame),
                    wgpu::CurrentSurfaceTexture::Suboptimal(frame) => Some(frame),
                    wgpu::CurrentSurfaceTexture::Outdated | wgpu::CurrentSurfaceTexture::Lost => {
                        if let Some(surface_config) = &self.surface_config {
                            surface.configure(&self.device, surface_config);
                        }
                        None
                    }
                    _ => None,
                };
                let Some(frame) = frame else {
                    // Skip this frame; the surface will be ready on the next one.
                    self.queue.submit(Some(encoder.finish()));
                    return Ok(());
                };
                let view = frame
                    .texture
                    .create_view(&wgpu::TextureViewDescriptor::default());
                self.post.execute(
                    &self.device,
                    &self.queue,
                    &mut encoder,
                    &self.targets.hdr,
                    &view,
                    self.output_format,
                    settings,
                );
                self.queue.submit(Some(encoder.finish()));
                self.queue.present(frame);
            }
            None => {
                let output = self
                    .targets
                    .offscreen_view
                    .as_ref()
                    .ok_or(Error::Surface("offscreen target missing".into()))?;
                self.post.execute(
                    &self.device,
                    &self.queue,
                    &mut encoder,
                    &self.targets.hdr,
                    output,
                    self.output_format,
                    settings,
                );
                self.queue.submit(Some(encoder.finish()));
            }
        }

        self.stats.cpu_time_ms = cpu_start.elapsed().as_secs_f32() * 1000.0;
        Ok(())
    }

    fn collect_lights(&mut self, scene: &Scene) -> Vec<LightUniform> {
        let mut lights = Vec::new();
        for (id, node) in scene.nodes() {
            let Some(light) = node.light else { continue };
            if !light.enabled || !scene.is_visible_in_hierarchy(id) {
                continue;
            }
            if lights.len() >= MAX_LIGHTS {
                log::warn!("more than {MAX_LIGHTS} lights in the scene, the extra ones are ignored");
                break;
            }
            lights.push(LightUniform::new(&light, &node.world_matrix()));
        }
        lights
    }

    fn collect_draw_items(
        &mut self,
        scene: &Scene,
        frustum: &Frustum,
        camera_position: Vec3,
        camera_layers: u32,
    ) {
        self.draw_items.clear();
        let mut visible = 0u32;
        let mut culled = 0u32;

        for (id, node) in scene.nodes() {
            let Some(binding) = node.mesh else { continue };
            if node.layers & camera_layers == 0 || !scene.is_visible_in_hierarchy(id) {
                continue;
            }
            let Some(geometry) = scene.geometry(binding.geometry) else {
                continue;
            };
            let Some(material) = scene.material(binding.material) else {
                continue;
            };
            if geometry.vertices.is_empty() {
                continue;
            }
            let world = node.world_matrix();
            let bounds = geometry.bounds.transformed(&world);
            if self.config.frustum_culling
                && !bounds.is_empty()
                && !frustum.intersects_sphere(bounds.center(), bounds.radius())
            {
                culled += 1;
                continue;
            }
            visible += 1;
            self.draw_items.push(DrawItem {
                geometry: binding.geometry,
                material: binding.material,
                world,
                depth: (bounds.center() - camera_position).length_squared(),
                transparent: material.is_transparent(),
                render_order: material.render_order,
                object_offset: 0,
            });
        }

        // Opaque front to back (early z), transparent back to front (blending).
        self.draw_items.sort_by(|a, b| {
            a.transparent
                .cmp(&b.transparent)
                .then(a.render_order.cmp(&b.render_order))
                .then_with(|| {
                    if a.transparent {
                        b.depth.total_cmp(&a.depth)
                    } else {
                        a.depth.total_cmp(&b.depth)
                    }
                })
        });

        for (index, item) in self.draw_items.iter_mut().enumerate() {
            item.object_offset = index as u32;
        }
        self.stats.visible_nodes = visible;
        self.stats.culled_nodes = culled;
    }

    fn upload_resources(&mut self, scene: &Scene, encoder: &mut wgpu::CommandEncoder) {
        // Textures first: materials bind their views.
        let mut used_textures: Vec<u32> = Vec::new();
        for item in &self.draw_items {
            if let Some(material) = scene.material(item.material) {
                for slot in [
                    material.textures.base_color,
                    material.textures.normal,
                    material.textures.metallic_roughness,
                    material.textures.emissive,
                    material.textures.occlusion,
                ]
                .into_iter()
                .flatten()
                {
                    used_textures.push(slot);
                }
            }
        }
        if let Some(environment) = scene.environment.environment_map {
            used_textures.push(environment);
        }
        used_textures.sort_unstable();
        used_textures.dedup();
        for id in used_textures {
            if let Some(texture) = scene.texture(id) {
                self.resources.ensure_texture(
                    &self.device,
                    &self.queue,
                    encoder,
                    &mut self.mipmaps,
                    id,
                    texture,
                );
            }
        }

        let items: Vec<(GeometryId, MaterialId)> = self
            .draw_items
            .iter()
            .map(|item| (item.geometry, item.material))
            .collect();
        for (geometry_id, material_id) in items {
            if let Some(geometry) = scene.geometry(geometry_id) {
                self.resources
                    .ensure_mesh(&self.device, &self.queue, geometry_id, geometry);
            }
            if let Some(material) = scene.material(material_id) {
                self.resources.ensure_material(
                    &self.device,
                    &self.queue,
                    &self.layouts.material,
                    &self.defaults,
                    scene,
                    material_id,
                    material,
                );
            }
        }

        // Rebuild the frame bind group when the environment map changes.
        if self.frame_bind_group_environment != scene.environment.environment_map {
            let environment_view = scene
                .environment
                .environment_map
                .and_then(|id| self.resources.texture(id))
                .map(|t| &t.view)
                .unwrap_or(&self.defaults.black_srgb);
            let environment_sampler = scene
                .environment
                .environment_map
                .and_then(|id| self.resources.texture(id))
                .map(|t| &t.sampler)
                .unwrap_or(&self.defaults.sampler);
            self.frame_bind_group = create_frame_bind_group(
                &self.device,
                &self.layouts,
                &self.frame_buffer,
                &self.lights_buffer,
                environment_view,
                environment_sampler,
            );
            self.frame_bind_group_environment = scene.environment.environment_map;
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn upload_frame_uniforms(
        &mut self,
        scene: &Scene,
        view: &Mat4,
        projection: &Mat4,
        camera_position: Vec3,
        near: f32,
        far: f32,
        lights: &[LightUniform],
    ) {
        let has_environment = scene.environment.environment_map.is_some();
        let frame = FrameUniform::new(
            *view,
            *projection,
            camera_position,
            self.config.exposure,
            &scene.environment,
            lights.len() as u32,
            has_environment,
            near,
            far,
            self.start.elapsed().as_secs_f32(),
        );
        self.queue
            .write_buffer(&self.frame_buffer, 0, bytemuck::bytes_of(&frame));

        let mut buffer = LightsUniform::default();
        buffer.lights[..lights.len()].copy_from_slice(lights);
        self.queue
            .write_buffer(&self.lights_buffer, 0, bytemuck::bytes_of(&buffer));
    }

    fn upload_object_uniforms(&mut self) {
        let count = self.draw_items.len() as u32;
        if count == 0 {
            return;
        }
        if count > self.object_capacity {
            // Grow geometrically so long scenes stop reallocating quickly.
            self.object_capacity = count.next_power_of_two();
            self.object_buffer = self.device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("threenet.object.uniform"),
                size: (self.object_stride * self.object_capacity) as u64,
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            self.object_bind_group =
                create_object_bind_group(&self.device, &self.layouts, &self.object_buffer);
        }

        let stride = self.object_stride as usize;
        self.object_data.clear();
        self.object_data.resize(stride * self.draw_items.len(), 0);
        for (index, item) in self.draw_items.iter_mut().enumerate() {
            let uniform = ObjectUniform::new(&item.world);
            let offset = index * stride;
            self.object_data[offset..offset + size_of::<ObjectUniform>()]
                .copy_from_slice(bytemuck::bytes_of(&uniform));
            item.object_offset = (offset) as u32;
        }
        self.queue
            .write_buffer(&self.object_buffer, 0, &self.object_data);
    }

    /// Copies the offscreen target back to the CPU as tightly packed RGBA8.
    /// Only available on renderers created with [`Renderer::new_offscreen`].
    pub fn read_pixels(&mut self) -> Result<Vec<u8>> {
        let texture = self
            .targets
            .offscreen
            .as_ref()
            .ok_or(Error::Surface("renderer is not offscreen".into()))?;
        let width = self.config.width;
        let height = self.config.height;
        let unpadded = width * 4;
        let align = wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
        let padded = unpadded.div_ceil(align) * align;

        let buffer = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("threenet.readback"),
            size: (padded * height) as u64,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("threenet.readback.encoder"),
            });
        encoder.copy_texture_to_buffer(
            wgpu::TexelCopyTextureInfo {
                texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyBufferInfo {
                buffer: &buffer,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(padded),
                    rows_per_image: Some(height),
                },
            },
            wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
        );
        self.queue.submit(Some(encoder.finish()));

        let slice = buffer.slice(..);
        let (sender, receiver) = std::sync::mpsc::channel();
        slice.map_async(wgpu::MapMode::Read, move |result| {
            let _ = sender.send(result);
        });
        self.device
            .poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: None,
            })
            .map_err(|e| Error::Device(e.to_string()))?;
        receiver
            .recv()
            .map_err(|e| Error::Device(e.to_string()))?
            .map_err(|e| Error::Device(e.to_string()))?;

        let data = slice
            .get_mapped_range()
            .map_err(|e| Error::Device(e.to_string()))?;
        let mut pixels = Vec::with_capacity((unpadded * height) as usize);
        for row in 0..height as usize {
            let start = row * padded as usize;
            pixels.extend_from_slice(&data[start..start + unpadded as usize]);
        }
        drop(data);
        buffer.unmap();
        Ok(pixels)
    }

    #[inline]
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }

    #[inline]
    pub fn queue(&self) -> &wgpu::Queue {
        &self.queue
    }

    #[inline]
    pub fn instance(&self) -> &wgpu::Instance {
        &self.instance
    }
}

/// Creates the wgpu instance with the platform default backends.
///
/// `WGPU_BACKEND` overrides the selection. On Windows the default is DX12
/// only: enumerating Vulkan there depends on the vendor loader entries and
/// crashes outright on machines with a stale registry, while DX12 is always
/// present.
fn create_instance() -> wgpu::Instance {
    let mut descriptor = wgpu::InstanceDescriptor::new_without_display_handle();
    descriptor.backends = match wgpu::Backends::from_env() {
        Some(backends) => backends,
        None if cfg!(target_os = "windows") => wgpu::Backends::DX12,
        None => wgpu::Backends::PRIMARY,
    };
    wgpu::Instance::new(descriptor)
}

fn clamp_samples(adapter: &wgpu::Adapter, requested: u32) -> u32 {
    let flags = adapter
        .get_texture_format_features(HDR_FORMAT)
        .flags;
    let mut samples = match requested {
        0 | 1 => 1,
        2 | 3 => 2,
        4..=7 => 4,
        _ => 8,
    };
    while samples > 1 && !flags.sample_count_supported(samples) {
        samples /= 2;
    }
    samples
}

fn object_stride(device: &wgpu::Device) -> u32 {
    let alignment = device.limits().min_uniform_buffer_offset_alignment;
    let size = size_of::<ObjectUniform>() as u32;
    size.div_ceil(alignment) * alignment
}

fn create_object_bind_group(
    device: &wgpu::Device,
    layouts: &Layouts,
    buffer: &wgpu::Buffer,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("threenet.bind_group.object"),
        layout: &layouts.object,
        entries: &[wgpu::BindGroupEntry {
            binding: 0,
            resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                buffer,
                offset: 0,
                size: wgpu::BufferSize::new(size_of::<ObjectUniform>() as u64),
            }),
        }],
    })
}

fn create_frame_bind_group(
    device: &wgpu::Device,
    layouts: &Layouts,
    frame: &wgpu::Buffer,
    lights: &wgpu::Buffer,
    environment: &wgpu::TextureView,
    sampler: &wgpu::Sampler,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("threenet.bind_group.frame"),
        layout: &layouts.frame,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: frame.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: lights.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: wgpu::BindingResource::TextureView(environment),
            },
            wgpu::BindGroupEntry {
                binding: 3,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
        ],
    })
}

fn create_targets(
    device: &wgpu::Device,
    width: u32,
    height: u32,
    samples: u32,
    output_format: wgpu::TextureFormat,
    offscreen: bool,
) -> Targets {
    let size = wgpu::Extent3d {
        width,
        height,
        depth_or_array_layers: 1,
    };
    let hdr = device
        .create_texture(&wgpu::TextureDescriptor {
            label: Some("threenet.target.hdr"),
            size,
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: HDR_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        })
        .create_view(&wgpu::TextureViewDescriptor::default());

    let depth = device
        .create_texture(&wgpu::TextureDescriptor {
            label: Some("threenet.target.depth"),
            size,
            mip_level_count: 1,
            sample_count: samples,
            dimension: wgpu::TextureDimension::D2,
            format: DEPTH_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        })
        .create_view(&wgpu::TextureViewDescriptor::default());

    let msaa = (samples > 1).then(|| {
        device
            .create_texture(&wgpu::TextureDescriptor {
                label: Some("threenet.target.msaa"),
                size,
                mip_level_count: 1,
                sample_count: samples,
                dimension: wgpu::TextureDimension::D2,
                format: HDR_FORMAT,
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
                view_formats: &[],
            })
            .create_view(&wgpu::TextureViewDescriptor::default())
    });

    let (offscreen_texture, offscreen_view) = if offscreen {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("threenet.target.offscreen"),
            size,
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: output_format,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        (Some(texture), Some(view))
    } else {
        (None, None)
    };

    Targets {
        hdr,
        depth,
        msaa,
        offscreen: offscreen_texture,
        offscreen_view,
    }
}

impl FrameUniform {
    fn zeroed() -> Self {
        bytemuck::Zeroable::zeroed()
    }
}

