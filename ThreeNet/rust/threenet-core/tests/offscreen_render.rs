//! End to end smoke test: build a small scene, render it offscreen and verify
//! that the framebuffer actually contains the lit object.
//!
//! The test is skipped (not failed) when the machine has no usable GPU adapter,
//! so it stays useful in CI containers.

use threenet_core::geometry::primitives;
use threenet_core::light::Light;
use threenet_core::material::Material;
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{Renderer, RendererConfig, ToneMapping};
use threenet_core::scene::Scene;

fn build_scene() -> (Scene, u32) {
    let mut scene = Scene::new();
    scene.environment.background = [0.0, 0.0, 0.0, 1.0];

    let geometry = scene.add_geometry(primitives::sphere(1.0, 32, 16));
    let material = scene.add_material(Material::pbr(Vec4::new(0.9, 0.2, 0.15, 1.0), 0.0, 0.35));
    scene.add_mesh(None, geometry, material).unwrap();

    let light_node = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(light_node).unwrap();
        node.light = Some(Light::directional(Vec3::ONE, 4.0));
        node.transform.look_at(Vec3::new(-1.0, -1.0, -1.0), Vec3::Y);
    }

    let camera_node = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(camera_node).unwrap();
        node.camera = Some(threenet_core::camera::Camera::perspective(
            std::f32::consts::FRAC_PI_4,
            0.1,
            100.0,
        ));
        node.transform.translation = Vec3::new(0.0, 0.0, 4.0);
    }
    scene.mark_dirty(scene.root());
    (scene, camera_node)
}

fn renderer() -> Option<Renderer> {
    let config = RendererConfig {
        width: 128,
        height: 128,
        msaa_samples: 4,
        tone_mapping: ToneMapping::Aces,
        bloom: true,
        ..Default::default()
    };
    match Renderer::new_offscreen(config) {
        Ok(renderer) => Some(renderer),
        Err(error) => {
            eprintln!("skipping GPU test: {error}");
            None
        }
    }
}

#[test]
fn renders_a_lit_sphere_offscreen() {
    let Some(mut renderer) = renderer() else {
        return;
    };
    let (mut scene, camera) = build_scene();

    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();
    assert_eq!(pixels.len(), 128 * 128 * 4);

    // The centre pixel must show the red sphere, a corner must stay background.
    let center = (64 * 128 + 64) * 4;
    let corner = 0;
    assert!(
        pixels[center] > 60,
        "the sphere should be visible at the centre, got {:?}",
        &pixels[center..center + 4]
    );
    assert!(
        pixels[center] > pixels[center + 1],
        "the sphere is red, so the red channel should dominate"
    );
    assert!(
        pixels[corner] < 20,
        "the corner should keep the black background, got {:?}",
        &pixels[corner..corner + 4]
    );

    let stats = renderer.stats();
    assert_eq!(stats.draw_calls, 1);
    assert_eq!(stats.lights, 1);
}

#[test]
fn culls_objects_outside_the_frustum() {
    let Some(mut renderer) = renderer() else {
        return;
    };
    let (mut scene, camera) = build_scene();

    // Move the mesh far behind the camera; it must be culled away.
    let mesh_node = scene
        .nodes()
        .find(|(_, node)| node.mesh.is_some())
        .map(|(id, _)| id)
        .unwrap();
    scene.node_mut(mesh_node).unwrap().transform.translation = Vec3::new(0.0, 0.0, 500.0);
    scene.mark_dirty(mesh_node);

    renderer.render(&mut scene, Some(camera)).unwrap();
    assert_eq!(renderer.stats().draw_calls, 0);
    assert_eq!(renderer.stats().culled_nodes, 1);
}
