//! The deferred renderer must light a scene the same way the forward renderer
//! does. Skipped when no GPU adapter is available.

use threenet_core::camera::Camera;
use threenet_core::geometry::primitives;
use threenet_core::light::Light;
use threenet_core::material::{AlphaMode, Material, ShadingModel};
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{RenderPath, Renderer, RendererConfig, ToneMapping};
use threenet_core::scene::Scene;

const SIZE: u32 = 96;

fn renderer(path: RenderPath) -> Option<Renderer> {
    Renderer::new_offscreen(RendererConfig {
        width: SIZE,
        height: SIZE,
        msaa_samples: 1,
        tone_mapping: ToneMapping::Aces,
        shadows: true,
        render_path: path,
        ..Default::default()
    })
    .map_err(|error| eprintln!("skipping GPU test: {error}"))
    .ok()
}

fn scene() -> (Scene, u32) {
    let mut scene = Scene::new();
    scene.environment.background = [0.05, 0.05, 0.08, 1.0];

    let floor = scene.add_geometry(primitives::plane(12.0, 12.0, 1, 1));
    let floor_material = scene.add_material(Material::pbr(Vec4::new(0.7, 0.7, 0.7, 1.0), 0.0, 0.8));
    let floor_node = scene.add_mesh(None, floor, floor_material).unwrap();
    scene.node_mut(floor_node).unwrap().transform.set_euler_angles(Vec3::new(-std::f32::consts::FRAC_PI_2, 0.0, 0.0));

    let sphere = scene.add_geometry(primitives::sphere(1.0, 32, 16));
    let models = [ShadingModel::Pbr, ShadingModel::Phong, ShadingModel::Lambert];
    for (i, model) in models.iter().enumerate() {
        let mut sphere_material = Material::pbr(Vec4::new(0.9, 0.3 + (i as f32 * 0.2), 0.2, 1.0), 0.2, 0.4);
        sphere_material.shading = *model;
        let material = scene.add_material(sphere_material);
        let node = scene.add_mesh(None, sphere, material).unwrap();
        scene.node_mut(node).unwrap().transform.translation = Vec3::new(-2.5 + (i as f32 * 2.5), 1.0, 0.0);
    }

    // A glass pane exercises the forward pass that follows the deferred lighting.
    let mut glass_material = Material::pbr(Vec4::new(0.3, 0.6, 1.0, 0.4), 0.0, 0.1);
    glass_material.alpha_mode = AlphaMode::Blend;
    let glass = scene.add_material(glass_material);
    let pane = scene.add_geometry(primitives::cuboid(1.5, 1.5, 0.05, 1));
    let pane_node = scene.add_mesh(None, pane, glass).unwrap();
    scene.node_mut(pane_node).unwrap().transform.translation = Vec3::new(0.0, 1.0, 2.0);

    let sun = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(sun).unwrap();
        let mut light = Light::directional(Vec3::ONE, 2.5);
        light.cast_shadow = true;
        node.light = Some(light);
        node.transform.translation = Vec3::new(3.0, 6.0, 4.0);
        node.transform.look_at(Vec3::ZERO, Vec3::Y);
    }

    // Plenty of coloured point lights: the case deferred shading is for.
    for i in 0..24 {
        let angle = i as f32 / 24.0 * std::f32::consts::TAU;
        let node = scene.create_node(None).unwrap();
        let n = scene.node_mut(node).unwrap();
        n.light = Some(Light::point(Vec3::new(angle.sin().abs(), 0.5, angle.cos().abs()), 6.0, 6.0));
        n.transform.translation = Vec3::new(angle.cos() * 4.0, 0.6, angle.sin() * 4.0);
    }

    let camera = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(camera).unwrap();
        node.camera = Some(Camera::perspective(0.9, 0.1, 100.0));
        node.transform.translation = Vec3::new(0.0, 3.0, 8.0);
        node.transform.look_at(Vec3::new(0.0, 0.8, 0.0), Vec3::Y);
    }
    scene.mark_dirty(scene.root());
    (scene, camera)
}

#[test]
fn deferred_matches_forward() {
    let (Some(mut forward), Some(mut deferred)) = (renderer(RenderPath::Forward), renderer(RenderPath::Deferred)) else {
        return;
    };

    let (mut scene_a, camera_a) = scene();
    let (mut scene_b, camera_b) = scene();
    forward.render(&mut scene_a, Some(camera_a)).unwrap();
    deferred.render(&mut scene_b, Some(camera_b)).unwrap();
    let a = forward.read_pixels().unwrap();
    let b = deferred.read_pixels().unwrap();

    let mut total = 0u64;
    let mut worst = 0u8;
    for (x, y) in a.chunks_exact(4).zip(b.chunks_exact(4)) {
        for c in 0..3 {
            let diff = x[c].abs_diff(y[c]);
            total += diff as u64;
            worst = worst.max(diff);
        }
    }
    let mean = total as f64 / (SIZE * SIZE * 3) as f64;
    assert!(mean < 2.0, "mean channel difference {mean:.2} (worst {worst})");

    // The image must actually contain lit geometry, not just the background.
    let lit = a.chunks_exact(4).filter(|p| p[0] > 60).count();
    assert!(lit > 500, "only {lit} lit pixels");
    assert!(deferred.stats().draw_calls >= 5);
}
