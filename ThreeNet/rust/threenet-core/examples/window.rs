//! Opens a window and spins a cube. Pure Rust counterpart of the
//! `ThreeNet.Samples.HelloCube` .NET sample, useful to tell an engine bug from
//! an interop bug: `cargo run -p threenet-core --example window`

use threenet_core::camera::Camera;
use threenet_core::geometry::primitives;
use threenet_core::light::Light;
use threenet_core::material::Material;
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{Renderer, RendererConfig};
use threenet_core::scene::{NodeId, Scene};
use threenet_core::window::{AppHandler, InputEvent, WindowConfig, run_app};

struct Demo {
    scene: Scene,
    camera: NodeId,
    cube: NodeId,
    elapsed: f32,
    frames: u32,
}

impl AppHandler for Demo {
    fn on_init(&mut self, renderer: &mut Renderer) {
        println!("adapter: {}", renderer.adapter_name());
    }

    fn on_frame(&mut self, renderer: &mut Renderer, delta: f32) {
        self.elapsed += delta;
        self.frames += 1;

        if let Some(node) = self.scene.node_mut(self.cube) {
            node.transform
                .set_euler_angles(Vec3::new(self.elapsed * 0.7, self.elapsed, 0.0));
        }
        self.scene.mark_dirty(self.cube);

        if let Err(error) = renderer.render(&mut self.scene, Some(self.camera)) {
            eprintln!("render failed: {error}");
        }

        if self.frames == 120 {
            println!("120 frames rendered: {:?}", renderer.stats());
        }
    }

    fn on_event(&mut self, _renderer: &mut Renderer, _event: &InputEvent) {}
}

fn main() {
    env_logger::init();
    let mut scene = Scene::new();

    let geometry = scene.add_geometry(primitives::cuboid(1.2, 1.2, 1.2, 1));
    let material = scene.add_material(Material::pbr(Vec4::new(0.95, 0.45, 0.1, 1.0), 0.1, 0.35));
    let cube = scene.add_mesh(None, geometry, material).unwrap();

    let sun = scene.create_node(None).unwrap();
    if let Some(node) = scene.node_mut(sun) {
        node.light = Some(Light::directional(Vec3::ONE, 3.0));
        node.transform.translation = Vec3::new(4.0, 6.0, 4.0);
        node.transform.look_at(Vec3::ZERO, Vec3::Y);
    }

    let camera = scene.create_node(None).unwrap();
    if let Some(node) = scene.node_mut(camera) {
        node.camera = Some(Camera::perspective(55f32.to_radians(), 0.1, 100.0));
        node.transform.translation = Vec3::new(0.0, 2.0, 5.0);
        node.transform.look_at(Vec3::ZERO, Vec3::Y);
    }
    scene.mark_dirty(scene.root());

    let config = WindowConfig {
        title: String::from("Three.Net - window example"),
        renderer: RendererConfig {
            msaa_samples: 4,
            bloom: true,
            ..Default::default()
        },
        ..Default::default()
    };

    run_app(
        config,
        Demo {
            scene,
            camera,
            cube,
            elapsed: 0.0,
            frames: 0,
        },
    )
    .expect("the window loop failed");
}
