//! Renders the shadow map + SSAO scene offscreen and writes `shadows.png`.
//! `cargo run -p threenet-core --example shadows -- [output.png]`

use threenet_core::camera::Camera;
use threenet_core::geometry::primitives;
use threenet_core::light::Light;
use threenet_core::material::Material;
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{Renderer, RendererConfig};
use threenet_core::scene::Scene;

fn main() {
    env_logger::init();
    let output = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "shadows.png".into());

    let mut renderer = Renderer::new_offscreen(RendererConfig {
        width: 1280,
        height: 720,
        msaa_samples: 4,
        shadows: true,
        shadow_map_size: 2048,
        shadow_cascades: 3,
        shadow_distance: 45.0,
        ssao: true,
        ssao_radius: 0.7,
        ssao_intensity: 1.6,
        ssao_direct_strength: 0.3,
        ..Default::default()
    })
    .expect("renderer");
    println!("adapter: {}", renderer.adapter_name());

    let mut scene = Scene::new();
    scene.environment.background = [0.02, 0.025, 0.035, 1.0];
    scene.environment.ambient_color = Vec3::new(0.35, 0.42, 0.6);
    scene.environment.ambient_intensity = 0.35;

    let floor_material =
        scene.add_material(Material::pbr(Vec4::new(0.35, 0.38, 0.43, 1.0), 0.0, 0.8));
    let floor_geometry = scene.add_geometry(primitives::plane(60.0, 60.0, 1, 1));
    let floor = scene
        .add_mesh(None, floor_geometry, floor_material)
        .unwrap();
    {
        let node = scene.node_mut(floor).unwrap();
        node.transform
            .set_euler_angles(Vec3::new(-std::f32::consts::FRAC_PI_2, 0.0, 0.0));
        // A floor receives shadows but never needs to cast them.
        node.mesh.as_mut().unwrap().cast_shadow = false;
    }

    let pillar_material =
        scene.add_material(Material::pbr(Vec4::new(0.8, 0.82, 0.85, 1.0), 0.05, 0.55));
    let pillar_geometry = scene.add_geometry(primitives::cuboid(0.8, 3.4, 0.8, 1));
    for i in -2..=2 {
        let pillar = scene
            .add_mesh(None, pillar_geometry, pillar_material)
            .unwrap();
        scene.node_mut(pillar).unwrap().transform.translation =
            Vec3::new(i as f32 * 2.8, 1.7, -2.5);
    }

    let sphere_material =
        scene.add_material(Material::pbr(Vec4::new(0.85, 0.35, 0.15, 1.0), 0.1, 0.35));
    let sphere_geometry = scene.add_geometry(primitives::sphere(0.7, 32, 24));
    for (i, angle) in [0.0f32, 2.1, 4.2].iter().enumerate() {
        let sphere = scene
            .add_mesh(None, sphere_geometry, sphere_material)
            .unwrap();
        let radius = 2.4 + i as f32 * 0.7;
        scene.node_mut(sphere).unwrap().transform.translation =
            Vec3::new(angle.cos() * radius, 0.7, angle.sin() * radius);
    }

    let torus_material = scene.add_material(Material::pbr(Vec4::new(0.2, 0.6, 0.7, 1.0), 0.9, 0.2));
    let torus_geometry =
        scene.add_geometry(primitives::torus(1.1, 0.35, 20, 72, std::f32::consts::TAU));
    let torus = scene
        .add_mesh(None, torus_geometry, torus_material)
        .unwrap();
    scene.node_mut(torus).unwrap().transform.translation = Vec3::new(0.0, 1.4, 1.5);

    let sun = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(sun).unwrap();
        let mut light = Light::directional(Vec3::new(1.0, 0.95, 0.88), 3.2);
        light.cast_shadow = true;
        node.light = Some(light);
        node.transform.translation = Vec3::new(6.0, 9.0, 6.0);
        node.transform.look_at(Vec3::ZERO, Vec3::Y);
    }

    let spot = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(spot).unwrap();
        let mut light = Light::spot(Vec3::new(0.56, 0.71, 1.0), 60.0, 30.0, 0.28, 0.45);
        light.cast_shadow = true;
        node.light = Some(light);
        node.transform.translation = Vec3::new(-5.0, 6.5, 4.0);
        node.transform.look_at(Vec3::new(0.0, 1.0, 0.0), Vec3::Y);
    }

    let camera = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(camera).unwrap();
        node.camera = Some(Camera::perspective(std::f32::consts::FRAC_PI_4, 0.1, 200.0));
        node.transform.translation = Vec3::new(7.0, 5.0, 11.0);
        node.transform.look_at(Vec3::new(0.0, 1.4, 0.0), Vec3::Y);
    }
    scene.mark_dirty(scene.root());

    renderer.render(&mut scene, Some(camera)).expect("render");
    println!("stats: {:?}", renderer.stats());

    let pixels = renderer.read_pixels().expect("read pixels");
    image::save_buffer(
        &output,
        &pixels,
        renderer.width(),
        renderer.height(),
        image::ColorType::Rgba8,
    )
    .expect("save png");
    println!("wrote {output}");
}
