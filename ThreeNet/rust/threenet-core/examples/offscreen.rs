//! Renders one frame offscreen and writes `offscreen.png`.
//! Useful to check the pipeline without opening a window:
//! `cargo run -p threenet-core --example offscreen`

use threenet_core::geometry::primitives;
use threenet_core::light::Light;
use threenet_core::material::Material;
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{Renderer, RendererConfig};
use threenet_core::scene::Scene;

fn main() {
    env_logger::init();
    println!("creating renderer...");
    let mut renderer = Renderer::new_offscreen(RendererConfig {
        width: 256,
        height: 256,
        msaa_samples: 4,
        bloom: true,
        ..Default::default()
    })
    .expect("renderer");
    println!("adapter: {}", renderer.adapter_name());

    let mut scene = Scene::new();
    let geometry = scene.add_geometry(primitives::sphere(1.0, 32, 16));
    let material = scene.add_material(Material::pbr(Vec4::new(0.9, 0.2, 0.15, 1.0), 0.0, 0.35));
    scene.add_mesh(None, geometry, material).unwrap();

    let light = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(light).unwrap();
        node.light = Some(Light::directional(Vec3::ONE, 4.0));
        node.transform.look_at(Vec3::new(-1.0, -1.0, -1.0), Vec3::Y);
    }

    let camera = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(camera).unwrap();
        node.camera = Some(threenet_core::camera::Camera::perspective(
            std::f32::consts::FRAC_PI_4,
            0.1,
            100.0,
        ));
        node.transform.translation = Vec3::new(0.0, 0.0, 4.0);
    }
    scene.mark_dirty(scene.root());

    println!("rendering...");
    renderer.render(&mut scene, Some(camera)).expect("render");
    println!("stats: {:?}", renderer.stats());

    println!("reading pixels...");
    let pixels = renderer.read_pixels().expect("read pixels");
    image::save_buffer(
        "offscreen.png",
        &pixels,
        renderer.width(),
        renderer.height(),
        image::ColorType::Rgba8,
    )
    .expect("save png");
    println!("wrote offscreen.png");
}
