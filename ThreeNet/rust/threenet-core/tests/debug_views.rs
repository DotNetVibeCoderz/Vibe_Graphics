//! GPU tests for the renderer's debug views: each one must replace the lit
//! image with the surface channel it names, in both render paths. Like the other
//! offscreen tests they skip themselves when no adapter is available.
//!
//! Each test creates a single renderer and switches views on it, because
//! creating a device per sample is what the software adapters choke on.

use threenet_core::camera::Camera;
use threenet_core::geometry::primitives;
use threenet_core::light::Light;
use threenet_core::material::Material;
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{DebugView, RenderPath, Renderer, RendererConfig, ToneMapping};
use threenet_core::scene::Scene;

const SIZE: u32 = 96;

/// A single sphere, lit from the right, filling the middle of the frame. Its
/// base colour, roughness and metallic are distinct values so a channel that
/// leaks from another one is visible.
fn sphere_scene() -> (Scene, u32) {
    let mut scene = Scene::new();
    scene.environment.background = [0.0, 0.0, 0.0, 1.0];
    scene.environment.ambient_intensity = 0.0;

    let material = scene.add_material(Material::pbr(
        // Base colour 0.8 / 0.2 / 0.1, metallic 0.75, roughness 0.25.
        Vec4::new(0.8, 0.2, 0.1, 1.0),
        0.75,
        0.25,
    ));
    let sphere = scene.add_geometry(primitives::sphere(2.0, 48, 24));
    scene.add_mesh(None, sphere, material).unwrap();

    let light_node = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(light_node).unwrap();
        node.light = Some(Light::directional(Vec3::new(1.0, 1.0, 1.0), 3.0));
        node.transform.translation = Vec3::new(4.0, 4.0, 4.0);
        node.transform.look_at(Vec3::ZERO, Vec3::Y);
    }

    let camera_node = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(camera_node).unwrap();
        node.camera = Some(Camera::perspective(std::f32::consts::FRAC_PI_4, 0.1, 50.0));
        node.transform.translation = Vec3::new(0.0, 0.0, 6.0);
    }
    scene.mark_dirty(scene.root());
    (scene, camera_node)
}

/// Exposure and tone mapping are left at values that would change the numbers:
/// a debug view has to bypass them.
fn config(path: RenderPath) -> RendererConfig {
    RendererConfig {
        width: SIZE,
        height: SIZE,
        msaa_samples: 1,
        exposure: 4.0,
        tone_mapping: ToneMapping::Aces,
        bloom: true,
        render_path: path,
        ..Default::default()
    }
}

/// One renderer, one scene, and a closure that samples views on them. Returns
/// `false` when the machine has no adapter, so the test can report success.
fn with_renderer(path: RenderPath, body: impl FnOnce(&mut Sampler)) -> bool {
    let renderer = match Renderer::new_offscreen(config(path)) {
        Ok(renderer) => renderer,
        Err(error) => {
            eprintln!("skipping GPU test: {error}");
            return false;
        }
    };

    let (scene, camera) = sphere_scene();
    let mut sampler = Sampler {
        renderer,
        scene,
        camera,
        path,
    };
    body(&mut sampler);
    true
}

struct Sampler {
    renderer: Renderer,
    scene: Scene,
    camera: u32,
    path: RenderPath,
}

impl Sampler {
    /// Renders with one debug view and returns the centre pixel.
    fn center(&mut self, view: DebugView) -> [u8; 4] {
        self.renderer.set_config(RendererConfig {
            debug_view: view,
            ..config(self.path)
        });
        self.renderer.render(&mut self.scene, Some(self.camera)).unwrap();
        let pixels = self.renderer.read_pixels().unwrap();
        let i = (((SIZE / 2) * SIZE + SIZE / 2) * 4) as usize;
        [pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]]
    }

    /// Moves the camera back, for the depth view.
    fn move_camera_to(&mut self, z: f32) {
        self.scene.node_mut(self.camera).unwrap().transform.translation = Vec3::new(0.0, 0.0, z);
        self.scene.mark_dirty(self.camera);
    }
}

/// sRGB encoded channel back to the linear value the shader wrote.
fn to_linear(byte: u8) -> f32 {
    let value = byte as f32 / 255.0;
    if value <= 0.04045 {
        value / 12.92
    } else {
        ((value + 0.055) / 1.055).powf(2.4)
    }
}

#[test]
fn debug_view_material_channels_show_their_own_value() {
    for path in [RenderPath::Forward, RenderPath::Deferred] {
        let ran = with_renderer(path, |sampler| {
            let base = sampler.center(DebugView::BaseColor);
            assert!(
                (to_linear(base[0]) - 0.8).abs() < 0.05
                    && (to_linear(base[1]) - 0.2).abs() < 0.05
                    && (to_linear(base[2]) - 0.1).abs() < 0.05,
                "{path:?} base colour view returned {base:?}"
            );

            let roughness = sampler.center(DebugView::Roughness);
            assert!(
                (to_linear(roughness[0]) - 0.25).abs() < 0.05,
                "{path:?} roughness view returned {roughness:?}"
            );
            assert_eq!(roughness[0], roughness[1], "roughness view is not grey");

            let metallic = sampler.center(DebugView::Metallic);
            assert!(
                (to_linear(metallic[0]) - 0.75).abs() < 0.05,
                "{path:?} metallic view returned {metallic:?}"
            );
            assert_eq!(metallic[0], metallic[1], "metallic view is not grey");

            // The sphere's centre faces +Z, so the encoded normal is (0.5, 0.5, 1).
            let normal = sampler.center(DebugView::WorldNormal);
            assert!(
                (to_linear(normal[0]) - 0.5).abs() < 0.08
                    && (to_linear(normal[1]) - 0.5).abs() < 0.08
                    && to_linear(normal[2]) > 0.9,
                "{path:?} normal view returned {normal:?}"
            );
        });

        if !ran {
            return;
        }
    }
}

#[test]
fn debug_view_lighting_drops_the_albedo_and_off_restores_the_image() {
    with_renderer(RenderPath::Forward, |sampler| {
        // White surface, one white light: the lit centre stays neutral instead
        // of taking the material's red.
        let lighting = sampler.center(DebugView::Lighting);
        assert!(lighting[0] > 30, "lighting view is black: {lighting:?}");
        assert!(
            lighting[0].abs_diff(lighting[2]) < 24,
            "lighting view kept the albedo: {lighting:?}"
        );

        let albedo = sampler.center(DebugView::BaseColor);
        let lit = sampler.center(DebugView::Off);
        assert_ne!(lit, albedo, "the lit image and the base colour view are identical");
    });
}

#[test]
fn debug_view_depth_gets_darker_as_the_surface_moves_away() {
    with_renderer(RenderPath::Forward, |sampler| {
        let near = sampler.center(DebugView::Depth);
        sampler.move_camera_to(12.0);
        let far = sampler.center(DebugView::Depth);
        assert!(
            far[0] > near[0] + 8,
            "depth view did not change with distance: {} then {}",
            near[0],
            far[0]
        );
    });
}
