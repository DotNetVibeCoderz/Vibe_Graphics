//! GPU tests for shadow maps and SSAO. Like the other offscreen tests they skip
//! themselves when no adapter is available.

use threenet_core::camera::Camera;
use threenet_core::geometry::primitives;
use threenet_core::light::Light;
use threenet_core::material::Material;
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{Renderer, RendererConfig, ToneMapping};
use threenet_core::scene::Scene;

const SIZE: u32 = 128;

fn renderer(config: RendererConfig) -> Option<Renderer> {
    match Renderer::new_offscreen(RendererConfig {
        width: SIZE,
        height: SIZE,
        tone_mapping: ToneMapping::None,
        ..config
    }) {
        Ok(renderer) => Some(renderer),
        Err(error) => {
            eprintln!("skipping GPU test: {error}");
            None
        }
    }
}

fn luminance(pixels: &[u8], x: u32, y: u32) -> f32 {
    let i = ((y * SIZE + x) * 4) as usize;
    0.2126 * pixels[i] as f32 + 0.7152 * pixels[i + 1] as f32 + 0.0722 * pixels[i + 2] as f32
}

/// A wall (XY plane at z = 0) seen head on from z = 10, with a cube floating in
/// front of it. The camera sees x in about [-4.1, 4.1] on the wall.
fn wall_scene(cube_z: f32) -> (Scene, u32, u32) {
    let mut scene = Scene::new();
    scene.environment.background = [0.0, 0.0, 0.0, 1.0];
    scene.environment.ambient_intensity = 0.0;

    let white = scene.add_material(Material::pbr(Vec4::new(0.8, 0.8, 0.8, 1.0), 0.0, 0.9));
    let wall = scene.add_geometry(primitives::plane(20.0, 20.0, 1, 1));
    scene.add_mesh(None, wall, white).unwrap();

    let cube = scene.add_geometry(primitives::cuboid(1.0, 1.0, 1.0, 1));
    let cube_node = scene.add_mesh(None, cube, white).unwrap();
    scene.node_mut(cube_node).unwrap().transform.translation = Vec3::new(0.0, 0.0, cube_z);

    let light_node = scene.create_node(None).unwrap();
    let camera_node = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(camera_node).unwrap();
        node.camera = Some(Camera::perspective(std::f32::consts::FRAC_PI_4, 0.1, 100.0));
        node.transform.translation = Vec3::new(0.0, 0.0, 10.0);
    }
    scene.mark_dirty(scene.root());
    (scene, camera_node, light_node)
}

/// Column of the wall point at world `x` (z = 0) for the camera above.
fn column(x: f32) -> u32 {
    let half_width = 10.0 * (std::f32::consts::FRAC_PI_8).tan();
    ((x / half_width * 0.5 + 0.5) * SIZE as f32) as u32
}

#[test]
fn directional_light_casts_a_shadow() {
    let config = RendererConfig {
        shadows: true,
        shadow_map_size: 1024,
        ..Default::default()
    };
    let Some(mut renderer) = renderer(config) else {
        return;
    };
    let (mut scene, camera, light) = wall_scene(2.0);
    {
        let node = scene.node_mut(light).unwrap();
        let mut sun = Light::directional(Vec3::ONE, 3.0);
        sun.cast_shadow = true;
        node.light = Some(sun);
        // 45 degrees towards +X: the cube's shadow lands at x = +2.
        node.transform.look_at(Vec3::new(1.0, 0.0, -1.0), Vec3::Y);
    }
    scene.mark_dirty(light);

    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();
    let shadowed = luminance(&pixels, column(2.0), SIZE / 2);
    let lit = luminance(&pixels, column(-2.0), SIZE / 2);
    assert!(lit > 40.0, "the wall should be lit, got {lit}");
    assert!(
        shadowed < lit * 0.35,
        "the shadow should be clearly darker: shadowed {shadowed}, lit {lit}"
    );
    let stats = renderer.stats();
    assert_eq!(stats.shadow_layers, 3, "one layer per default cascade");
    assert!(stats.shadow_draw_calls > 0);

    // The same frame without shadows keeps both points equally lit.
    let mut config = renderer.config();
    config.shadows = false;
    renderer.set_config(config);
    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();
    let unshadowed = luminance(&pixels, column(2.0), SIZE / 2);
    assert!(
        (unshadowed - lit).abs() < 8.0,
        "without shadows: {unshadowed} vs {lit}"
    );
    assert_eq!(renderer.stats().shadow_layers, 0);
}

#[test]
fn receive_shadow_flag_opts_out() {
    let Some(mut renderer) = renderer(RendererConfig {
        shadows: true,
        ..Default::default()
    }) else {
        return;
    };
    let (mut scene, camera, light) = wall_scene(2.0);
    {
        let node = scene.node_mut(light).unwrap();
        let mut sun = Light::directional(Vec3::ONE, 3.0);
        sun.cast_shadow = true;
        node.light = Some(sun);
        node.transform.look_at(Vec3::new(1.0, 0.0, -1.0), Vec3::Y);
    }
    let wall = scene
        .nodes()
        .find(|(_, node)| node.mesh.is_some())
        .map(|(id, _)| id)
        .unwrap();
    scene
        .node_mut(wall)
        .unwrap()
        .mesh
        .as_mut()
        .unwrap()
        .receive_shadow = false;
    scene.mark_dirty(scene.root());

    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();
    let shadowed = luminance(&pixels, column(2.0), SIZE / 2);
    let lit = luminance(&pixels, column(-2.0), SIZE / 2);
    assert!(
        (shadowed - lit).abs() < 8.0,
        "receive_shadow = false: {shadowed} vs {lit}"
    );
}

#[test]
fn spot_light_casts_a_shadow() {
    let Some(mut renderer) = renderer(RendererConfig {
        shadows: true,
        ..Default::default()
    }) else {
        return;
    };
    let (mut scene, camera, light) = wall_scene(2.0);
    {
        let node = scene.node_mut(light).unwrap();
        let mut spot = Light::spot(Vec3::ONE, 80.0, 30.0, 0.6, 0.9);
        spot.cast_shadow = true;
        node.light = Some(spot);
        node.transform.translation = Vec3::new(-2.0, 0.0, 4.0);
        node.transform.look_at(Vec3::new(2.0, 0.0, 0.0), Vec3::Y);
    }
    scene.mark_dirty(light);

    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();
    // From (-2, 0, 4) through the cube centre (0, 0, 2) the shadow hits x = 2.
    let shadowed = luminance(&pixels, column(2.0), SIZE / 2);
    let lit = luminance(&pixels, column(2.0), SIZE / 2 - 30);
    assert!(lit > 20.0, "the spot should light the wall, got {lit}");
    assert!(
        shadowed < lit * 0.5,
        "spot shadow: shadowed {shadowed}, lit {lit}"
    );
    assert_eq!(renderer.stats().shadow_layers, 1);
}

#[test]
fn ssao_darkens_contact_areas_only() {
    let base = RendererConfig {
        ssao_radius: 0.6,
        ..Default::default()
    };
    let Some(mut renderer) = renderer(base) else {
        return;
    };
    // The cube touches the wall: its footprint is x in [-0.5, 0.5].
    let (mut scene, camera, _) = wall_scene(0.5);
    scene.environment.ambient_color = Vec3::ONE;
    scene.environment.ambient_intensity = 0.3;
    scene.mark_dirty(scene.root());

    renderer.render(&mut scene, Some(camera)).unwrap();
    let before = renderer.read_pixels().unwrap();

    renderer.set_config(RendererConfig {
        ssao: true,
        ..renderer.config()
    });
    renderer.render(&mut scene, Some(camera)).unwrap();
    let after = renderer.read_pixels().unwrap();

    // The crease next to the cube's right edge (x = 0.5 .. 0.8) gets darker.
    let darkest_ratio = (column(0.5)..=column(0.8))
        .map(|c| luminance(&after, c, SIZE / 2) / luminance(&before, c, SIZE / 2).max(1.0))
        .fold(f32::MAX, f32::min);
    assert!(
        darkest_ratio < 0.92,
        "SSAO should darken the crease, darkest ratio {darkest_ratio}"
    );

    // The open wall and the cube's front face stay untouched.
    for x in [-3.0, 3.0, 0.0] {
        let (b, a) = (
            luminance(&before, column(x), SIZE / 2),
            luminance(&after, column(x), SIZE / 2),
        );
        assert!(b > 40.0, "ambient should light the scene, got {b}");
        assert!(
            a > b * 0.95,
            "SSAO should leave open areas alone at x = {x}: {b} -> {a}"
        );
    }
}
