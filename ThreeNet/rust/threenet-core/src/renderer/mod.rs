//! The wgpu backed renderer: device management, the forward scene pass and the
//! post-processing chain.

pub mod deferred;
pub mod effects;
pub mod pipeline;
pub mod overlay;
mod post;
pub mod resources;
pub mod shadows;
pub mod ssao;
pub mod uniforms;

use std::time::Instant;

use wgpu::util::DeviceExt;

use crate::camera::Camera;
use crate::error::{Error, Result};
use crate::math::{Aabb, Frustum, Mat4, Vec3};
use crate::renderer::deferred::Deferred;
use crate::renderer::effects::{Effects, EffectsSettings};
use crate::renderer::pipeline::{DEPTH_FORMAT, HDR_FORMAT, Layouts, PipelineCache, PipelineKey};
use crate::renderer::post::{PostProcess, PostSettings};
use crate::renderer::resources::{DefaultTextures, MipmapGenerator, ResourceCache};
use crate::renderer::shadows::{LightSource, ShadowCaster, ShadowMaps, ShadowSettings};
use crate::renderer::ssao::{GBufferItem, Ssao, SsaoSettings};
use crate::renderer::uniforms::{
    FrameUniform, LightUniform, LightsUniform, MAX_LIGHTS, ObjectUniform,
};
use crate::scene::{GeometryId, MaterialId, NodeId, Scene};

pub use crate::renderer::post::ToneMapping;

/// How opaque geometry is lit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(u32)]
pub enum RenderPath {
    /// Every fragment is lit while it is rasterised (supports MSAA).
    #[default]
    Forward = 0,
    /// Surfaces go to a G-buffer and are lit once per pixel afterwards.
    /// Cheaper with many lights and heavy overdraw; MSAA is not applied.
    Deferred = 1,
}

impl RenderPath {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => RenderPath::Deferred,
            _ => RenderPath::Forward,
        }
    }
}

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
    /// Enables shadow maps for lights with `cast_shadow` set.
    pub shadows: bool,
    /// Resolution of each shadow map layer (256-8192).
    pub shadow_map_size: u32,
    /// How far from the camera directional light shadows reach.
    pub shadow_distance: f32,
    /// Cascades per directional light (1-4).
    pub shadow_cascades: u32,
    /// PCF radius in texels: 0 = hard, 1 = 3x3, 2 = 5x5, 3 = 7x7.
    pub shadow_softness: u32,
    /// Screen space ambient occlusion.
    pub ssao: bool,
    pub ssao_radius: f32,
    pub ssao_intensity: f32,
    pub ssao_bias: f32,
    pub ssao_samples: u32,
    /// How much AO also darkens direct light (0-1).
    pub ssao_direct_strength: f32,
    /// Forward or deferred shading.
    pub render_path: RenderPath,
    /// Bokeh depth of field around a focus plane.
    pub depth_of_field: bool,
    /// Distance from the camera that stays sharp, in world units.
    pub dof_focus_distance: f32,
    /// Depth band around the focus distance that stays sharp.
    pub dof_focus_range: f32,
    /// Largest blur radius in pixels (at 1080p; scaled with the target height).
    pub dof_max_blur: f32,
    /// Camera motion blur reconstructed from depth and the previous frame's view.
    pub motion_blur: bool,
    /// Fraction of the frame-to-frame motion that is smeared (1 = full shutter).
    pub motion_blur_strength: f32,
    /// Samples along the motion vector (4-32).
    pub motion_blur_samples: u32,
}

impl RendererConfig {
    pub fn shadow_settings(&self) -> ShadowSettings {
        ShadowSettings {
            enabled: self.shadows,
            map_size: self.shadow_map_size,
            distance: self.shadow_distance,
            cascades: self.shadow_cascades,
            softness: self.shadow_softness,
        }
    }

    pub fn ssao_settings(&self) -> SsaoSettings {
        SsaoSettings {
            enabled: self.ssao,
            radius: self.ssao_radius,
            bias: self.ssao_bias,
            intensity: self.ssao_intensity,
            samples: self.ssao_samples,
            direct_strength: self.ssao_direct_strength,
        }
    }
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
            shadows: false,
            shadow_map_size: 2048,
            shadow_distance: 60.0,
            shadow_cascades: 3,
            shadow_softness: 1,
            ssao: false,
            ssao_radius: 0.5,
            ssao_intensity: 1.5,
            ssao_bias: 0.025,
            ssao_samples: 16,
            ssao_direct_strength: 0.25,
            render_path: RenderPath::Forward,
            depth_of_field: false,
            dof_focus_distance: 10.0,
            dof_focus_range: 4.0,
            dof_max_blur: 14.0,
            motion_blur: false,
            motion_blur_strength: 0.6,
            motion_blur_samples: 12,
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
    /// Shadow map layers rendered this frame.
    pub shadow_layers: u32,
    /// Draw calls spent in the shadow and SSAO passes.
    pub shadow_draw_calls: u32,
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
    receive_shadow: bool,
    object_offset: u32,
}

/// A shadow caster that may or may not be visible to the camera.
#[derive(Debug, Clone, Copy)]
struct CasterItem {
    geometry: GeometryId,
    world: Mat4,
    center: Vec3,
    radius: f32,
    /// Index into `draw_items` when the camera also draws it.
    draw_index: Option<usize>,
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
    overlay: overlay::OverlayRenderer,
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
    /// Environment map, shadow map generation and SSAO generation the frame
    /// bind group was built with.
    frame_bind_group_key: Option<(Option<u32>, u64, u64)>,
    object_bind_group: wgpu::BindGroup,
    shadow_maps: ShadowMaps,
    ssao: Ssao,
    deferred: Deferred,
    effects: Effects,
    /// View-projection of the previous frame, for motion blur.
    previous_view_projection: Option<Mat4>,
    draw_items: Vec<DrawItem>,
    caster_items: Vec<CasterItem>,
    shadow_casters: Vec<ShadowCaster>,
    caster_bounds: Aabb,
    light_sources: Vec<LightSource>,
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
            // THREENET_FALLBACK_ADAPTER=1 picks the software adapter (WARP on
            // DX12), which is what GPU-less CI machines end up with.
            force_fallback_adapter: std::env::var_os("THREENET_FALLBACK_ADAPTER").is_some_and(|v| v != "0"),
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
        // Compressed texture formats are uploaded natively whenever the adapter
        // supports them (KTX2 blocks, transcoded Basis Universal).
        features |= adapter.features()
            & (wgpu::Features::TEXTURE_COMPRESSION_BC
                | wgpu::Features::TEXTURE_COMPRESSION_ETC2
                | wgpu::Features::TEXTURE_COMPRESSION_ASTC);

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
        let post_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.post"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/post.wgsl").into()),
        });
        let post_shader_for_mips = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("threenet.shader.mipmap"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/post.wgsl").into()),
        });

        let pipelines = PipelineCache::new(&device, &layouts, polygon_mode_line);
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
        let shadow_maps = ShadowMaps::new(&device, &layouts.object);
        let ssao = Ssao::new(&device, &queue, &layouts.object);
        let deferred = Deferred::new(&device, &layouts);
        let effects = Effects::new(&device);
        let frame_bind_group = create_frame_bind_group(
            &device,
            &layouts,
            &frame_buffer,
            &lights_buffer,
            &defaults.black_srgb,
            &defaults.sampler,
            &shadow_maps,
            &defaults.white_linear,
            ssao.linear_sampler(),
        );

        let targets = create_targets(
            &device,
            width,
            height,
            samples,
            output_format,
            surface.is_none(),
        );

        let overlay_renderer = overlay::OverlayRenderer::new(&device);
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
            overlay: overlay_renderer,
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
            frame_bind_group_key: None,
            object_bind_group,
            shadow_maps,
            ssao,
            deferred,
            effects,
            previous_view_projection: None,
            draw_items: Vec::new(),
            caster_items: Vec::new(),
            shadow_casters: Vec::new(),
            caster_bounds: Aabb::EMPTY,
            light_sources: Vec::new(),
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
        self.post.resize(
            &self.device,
            self.config.width,
            self.config.height,
            self.config.bloom,
        );
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
        self.post
            .resize(&self.device, width, height, self.config.bloom);
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
        scene.poll_streaming();
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
        let camera_layers = scene
            .node(camera_node)
            .map(|n| n.layers)
            .unwrap_or(u32::MAX);

        let view = camera_world.inverse();
        let projection = camera.projection_matrix(self.aspect_ratio());
        let frustum = Frustum::from_view_projection(&(projection * view));
        let camera_position = camera_world.w_axis.truncate();

        // ---------------------------------------------------------- gather
        let mut lights = self.collect_lights(scene);
        self.collect_draw_items(scene, &frustum, camera_position, camera_layers);
        let shadow_settings = self.config.shadow_settings();
        let ssao_settings = self.config.ssao_settings();
        self.shadow_maps.ensure_size(&self.device, &shadow_settings);
        let deferred = self.config.render_path == RenderPath::Deferred;
        // The forward path needs the depth prepass for its camera effects too.
        let camera_effects = self.config.depth_of_field || self.config.motion_blur;
        let prepass = ssao_settings.enabled || (!deferred && camera_effects);
        self.ssao.ensure_targets(
            &self.device,
            prepass,
            ssao_settings.enabled,
            self.config.width,
            self.config.height,
        );
        self.deferred.ensure_targets(&self.device, deferred, self.config.width, self.config.height);

        // ------------------------------------------------------- gpu upload
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("threenet.frame"),
            });

        self.resources.retain_live(scene);
        self.upload_resources(scene, &mut encoder);
        let aspect = self.aspect_ratio();
        self.shadow_maps.plan(
            &self.queue,
            &shadow_settings,
            &self.light_sources,
            &mut lights,
            &camera,
            &camera_world,
            aspect,
            &self.caster_bounds,
        );
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

        // ------------------------------------------------ shadow + AO passes
        let mut shadow_draw_calls = self.shadow_maps.render(
            &self.queue,
            &mut encoder,
            &self.shadow_casters,
            &self.resources,
            &self.object_bind_group,
        );
        self.stats.shadow_layers = self.shadow_maps.layer_count() as u32;
        if prepass {
            let items: Vec<GBufferItem> = self
                .draw_items
                .iter()
                .filter(|item| !item.transparent)
                .map(|item| GBufferItem {
                    geometry: item.geometry,
                    object_offset: item.object_offset,
                })
                .collect();
            shadow_draw_calls += self.ssao.render(
                &self.device,
                &self.queue,
                &mut encoder,
                &ssao_settings,
                &projection,
                &self.frame_buffer,
                &self.object_bind_group,
                &items,
                &self.resources,
            );
        }
        self.stats.shadow_draw_calls = shadow_draw_calls;

        // ------------------------------------------------------- scene pass
        let background = scene.environment.background;
        let clear = wgpu::Color {
            r: background[0] as f64,
            g: background[1] as f64,
            b: background[2] as f64,
            a: background[3] as f64,
        };

        let (draw_calls, triangles) = if deferred && let Some(gbuffer) = self.deferred.targets() {
            // 1. Geometry: opaque surfaces into the G-buffer.
            let mut counts = {
                let color_attachments: Vec<Option<wgpu::RenderPassColorAttachment>> = gbuffer
                    .colors
                    .iter()
                    .map(|view| {
                        Some(wgpu::RenderPassColorAttachment {
                            view,
                            depth_slice: None,
                            resolve_target: None,
                            ops: wgpu::Operations {
                                load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                                store: wgpu::StoreOp::Store,
                            },
                        })
                    })
                    .collect();
                let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                    label: Some("threenet.pass.gbuffer_geometry"),
                    color_attachments: &color_attachments,
                    depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                        view: &gbuffer.depth,
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
                draw_scene_items(
                    &mut pass,
                    &self.device,
                    &mut self.pipelines,
                    &self.resources,
                    &self.object_bind_group,
                    scene,
                    &self.draw_items,
                    1,
                    DrawFilter::DeferredOpaque,
                )
            };

            // 2. Lighting: clear to the background, then shade covered pixels.
            encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("threenet.pass.deferred_clear"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &self.targets.hdr,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(clear),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            self.deferred.light(&mut encoder, &self.targets.hdr, &self.frame_bind_group);

            // 3. Transparent (and unlit line/point) geometry, forward shaded on top.
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("threenet.pass.deferred_forward"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &self.targets.hdr,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Load,
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: &gbuffer.depth,
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
            pass.set_bind_group(0, &self.frame_bind_group, &[]);
            let late = draw_scene_items(
                &mut pass,
                &self.device,
                &mut self.pipelines,
                &self.resources,
                &self.object_bind_group,
                scene,
                &self.draw_items,
                1,
                DrawFilter::DeferredForward,
            );
            counts.0 += late.0;
            counts.1 += late.1;
            counts
        } else {
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
                        load: wgpu::LoadOp::Clear(clear),
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
            draw_scene_items(
                &mut pass,
                &self.device,
                &mut self.pipelines,
                &self.resources,
                &self.object_bind_group,
                scene,
                &self.draw_items,
                self.samples,
                DrawFilter::All,
            )
        };

        self.stats.draw_calls = draw_calls;
        self.stats.triangles = triangles;
        self.stats.lights = lights.len() as u32;

        // --------------------------------------------------- camera effects
        let view_projection = projection * view;
        let effects_settings = EffectsSettings {
            depth_of_field: self.config.depth_of_field,
            focus_distance: self.config.dof_focus_distance,
            focus_range: self.config.dof_focus_range,
            max_blur: self.config.dof_max_blur,
            motion_blur: self.config.motion_blur,
            motion_strength: self.config.motion_blur_strength,
            motion_samples: self.config.motion_blur_samples,
            projection,
            view_projection,
            previous_view_projection: self.previous_view_projection.unwrap_or(view_projection),
        };
        self.previous_view_projection = Some(view_projection);
        let effects_depth = if deferred {
            self.deferred.targets().map(|t| &t.depth)
        } else {
            self.ssao.depth_view()
        };
        let hdr_source = self.effects.run(
            &self.device,
            &self.queue,
            &mut encoder,
            &self.targets.hdr,
            effects_depth,
            self.config.width,
            self.config.height,
            &effects_settings,
        );

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
                    hdr_source,
                    &view,
                    self.output_format,
                    settings,
                );
                self.overlay.draw(
                    &self.device,
                    &self.queue,
                    &mut encoder,
                    &view,
                    self.output_format,
                    self.config.width,
                    self.config.height,
                    scene,
                    &self.resources,
                    &self.defaults,
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
                    hdr_source,
                    output,
                    self.output_format,
                    settings,
                );
                self.overlay.draw(
                    &self.device,
                    &self.queue,
                    &mut encoder,
                    output,
                    self.output_format,
                    self.config.width,
                    self.config.height,
                    scene,
                    &self.resources,
                    &self.defaults,
                );
                self.queue.submit(Some(encoder.finish()));
            }
        }

        self.stats.cpu_time_ms = cpu_start.elapsed().as_secs_f32() * 1000.0;
        Ok(())
    }

    fn collect_lights(&mut self, scene: &Scene) -> Vec<LightUniform> {
        let mut lights = Vec::new();
        self.light_sources.clear();
        for (id, node) in scene.nodes() {
            let Some(light) = node.light else { continue };
            if !light.enabled || !scene.is_visible_in_hierarchy(id) {
                continue;
            }
            if lights.len() >= MAX_LIGHTS {
                log::warn!(
                    "more than {MAX_LIGHTS} lights in the scene, the extra ones are ignored"
                );
                break;
            }
            let world = node.world_matrix();
            lights.push(LightUniform::new(&light, &world));
            self.light_sources.push(LightSource { light, world });
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
        self.caster_items.clear();
        self.caster_bounds = Aabb::EMPTY;
        let shadows = self.config.shadows;
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
            // Casters are gathered before culling: an object behind the camera
            // can still throw a shadow into the view.
            let casts = shadows
                && binding.cast_shadow
                && !material.is_transparent()
                && geometry.topology == crate::geometry::Topology::TriangleList;
            if casts {
                self.caster_bounds = self.caster_bounds.union(&bounds);
                self.caster_items.push(CasterItem {
                    geometry: binding.geometry,
                    world,
                    center: bounds.center(),
                    radius: bounds.radius(),
                    draw_index: None,
                });
            }
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
                receive_shadow: binding.receive_shadow,
                object_offset: 0,
            });
            if casts && let Some(caster) = self.caster_items.last_mut() {
                // The unsorted index for now; remapped after sorting below.
                caster.draw_index = Some(self.draw_items.len() - 1);
            }
        }

        // Remember the pre-sort position so casters can find their draw item.
        for (index, item) in self.draw_items.iter_mut().enumerate() {
            item.object_offset = index as u32;
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

        let mut sorted_position = vec![0usize; self.draw_items.len()];
        for (sorted, item) in self.draw_items.iter_mut().enumerate() {
            sorted_position[item.object_offset as usize] = sorted;
            item.object_offset = sorted as u32;
        }
        for caster in &mut self.caster_items {
            caster.draw_index = caster.draw_index.map(|index| sorted_position[index]);
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
        used_textures.extend(scene.overlay.texture_ids());
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

        for caster in &self.caster_items {
            if let Some(geometry) = scene.geometry(caster.geometry) {
                self.resources
                    .ensure_mesh(&self.device, &self.queue, caster.geometry, geometry);
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

        // Rebuild the frame bind group when the environment map, the shadow
        // maps or the SSAO targets change.
        let key = (
            scene.environment.environment_map,
            self.shadow_maps.generation(),
            self.ssao.generation(),
        );
        if self.frame_bind_group_key != Some(key) {
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
                &self.shadow_maps,
                self.ssao.ao_view().unwrap_or(&self.defaults.white_linear),
                self.ssao.linear_sampler(),
            );
            self.frame_bind_group_key = Some(key);
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
            [
                self.config.width as f32,
                self.config.height as f32,
                if self.ssao.ao_view().is_some() {
                    1.0
                } else {
                    0.0
                },
                self.config.ssao_direct_strength.clamp(0.0, 1.0),
            ],
        );
        self.queue
            .write_buffer(&self.frame_buffer, 0, bytemuck::bytes_of(&frame));

        let mut buffer = LightsUniform::default();
        buffer.lights[..lights.len()].copy_from_slice(lights);
        self.queue
            .write_buffer(&self.lights_buffer, 0, bytemuck::bytes_of(&buffer));
    }

    fn upload_object_uniforms(&mut self) {
        let extra_casters = self
            .caster_items
            .iter()
            .filter(|caster| caster.draw_index.is_none())
            .count();
        let count = (self.draw_items.len() + extra_casters) as u32;
        self.shadow_casters.clear();
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
        self.object_data.resize(stride * count as usize, 0);
        for (index, item) in self.draw_items.iter_mut().enumerate() {
            let uniform = ObjectUniform::new(&item.world, item.receive_shadow);
            let offset = index * stride;
            self.object_data[offset..offset + size_of::<ObjectUniform>()]
                .copy_from_slice(bytemuck::bytes_of(&uniform));
            item.object_offset = (offset) as u32;
        }
        // Off screen casters get their own slots after the draw items.
        let mut next_slot = self.draw_items.len();
        for caster in &self.caster_items {
            let object_offset = match caster.draw_index {
                Some(index) => self.draw_items[index].object_offset,
                None => {
                    let offset = next_slot * stride;
                    let uniform = ObjectUniform::new(&caster.world, false);
                    self.object_data[offset..offset + size_of::<ObjectUniform>()]
                        .copy_from_slice(bytemuck::bytes_of(&uniform));
                    next_slot += 1;
                    offset as u32
                }
            };
            self.shadow_casters.push(ShadowCaster {
                geometry: caster.geometry,
                object_offset,
                center: caster.center,
                radius: caster.radius,
            });
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

/// Which draw items a scene pass renders.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DrawFilter {
    /// Forward rendering: everything.
    All,
    /// Deferred geometry pass: opaque triangle meshes.
    DeferredOpaque,
    /// Deferred late pass: transparent meshes, lines and points.
    DeferredForward,
}

/// Issues the draw calls of one scene pass. Returns (draw calls, triangles).
#[allow(clippy::too_many_arguments)]
fn draw_scene_items(
    pass: &mut wgpu::RenderPass<'_>,
    device: &wgpu::Device,
    pipelines: &mut PipelineCache,
    resources: &ResourceCache,
    object_bind_group: &wgpu::BindGroup,
    scene: &Scene,
    items: &[DrawItem],
    samples: u32,
    filter: DrawFilter,
) -> (u32, u32) {
    let mut draw_calls = 0u32;
    let mut triangles = 0u32;
    let mut current_material = u32::MAX;
    let mut current_pipeline = None;

    for item in items {
        let Some(mesh) = resources.mesh(item.geometry) else {
            continue;
        };
        let Some(material) = scene.material(item.material) else {
            continue;
        };
        let Some(gpu_material) = resources.material(item.material) else {
            continue;
        };

        let deferrable = !item.transparent && mesh.topology == crate::geometry::Topology::TriangleList;
        match filter {
            DrawFilter::DeferredOpaque if !deferrable => continue,
            DrawFilter::DeferredForward if deferrable => continue,
            _ => {}
        }

        let custom = material.shader.and_then(|id| scene.shader(id));
        let mut key = PipelineKey::for_material(material, mesh.topology, samples, HDR_FORMAT, custom);
        if filter == DrawFilter::DeferredOpaque {
            key = key.gbuffer();
        }
        if current_pipeline != Some(key) {
            pass.set_pipeline(pipelines.get(device, key, custom));
            current_pipeline = Some(key);
        }
        if current_material != item.material {
            pass.set_bind_group(1, &gpu_material.bind_group, &[]);
            current_material = item.material;
        }
        pass.set_bind_group(2, object_bind_group, &[item.object_offset]);
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

    (draw_calls, triangles)
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
    let flags = adapter.get_texture_format_features(HDR_FORMAT).flags;
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

#[allow(clippy::too_many_arguments)]
fn create_frame_bind_group(
    device: &wgpu::Device,
    layouts: &Layouts,
    frame: &wgpu::Buffer,
    lights: &wgpu::Buffer,
    environment: &wgpu::TextureView,
    sampler: &wgpu::Sampler,
    shadow_maps: &ShadowMaps,
    ao: &wgpu::TextureView,
    ao_sampler: &wgpu::Sampler,
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
            wgpu::BindGroupEntry {
                binding: 4,
                resource: wgpu::BindingResource::TextureView(shadow_maps.array_view()),
            },
            wgpu::BindGroupEntry {
                binding: 5,
                resource: wgpu::BindingResource::Sampler(shadow_maps.sampler()),
            },
            wgpu::BindGroupEntry {
                binding: 6,
                resource: shadow_maps.uniform_buffer().as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 7,
                resource: wgpu::BindingResource::TextureView(ao),
            },
            wgpu::BindGroupEntry {
                binding: 8,
                resource: wgpu::BindingResource::Sampler(ao_sampler),
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
