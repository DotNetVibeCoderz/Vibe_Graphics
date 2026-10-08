//! GPU tests for the sky pass: it has to fill what no geometry covers, stay
//! behind everything that does, and leave the debug views alone. Skips itself
//! when no adapter is available.

use threenet_core::camera::Camera;
use threenet_core::geometry::primitives;
use threenet_core::material::Material;
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{DebugView, RenderPath, Renderer, RendererConfig, ToneMapping};
use threenet_core::scene::{Scene, SkyMode};

const SIZE: u32 = 96;

fn renderer(path: RenderPath) -> Option<Renderer> {
    match Renderer::new_offscreen(RendererConfig {
        width: SIZE,
        height: SIZE,
        msaa_samples: 1,
        tone_mapping: ToneMapping::None,
        render_path: path,
        ..Default::default()
    }) {
        Ok(renderer) => Some(renderer),
        Err(error) => {
            eprintln!("skipping GPU test: {error}");
            None
        }
    }
}

/// A wide floor low in the frame, so the top half is sky and the bottom is not.
fn scene_with_floor(sky: SkyMode) -> (Scene, u32) {
    let mut scene = Scene::new();
    scene.environment.background = [0.0, 0.0, 0.0, 1.0];
    scene.environment.ambient_intensity = 1.0;
    scene.environment.sky = sky;
    // The sun sits behind the camera: looking straight at the disc would blow
    // the whole frame out, which is exactly what the disc is supposed to do.
    scene.environment.sun_direction = Vec3::new(0.45, 0.45, 1.0);

    let material = scene.add_material(Material::pbr(Vec4::new(0.8, 0.1, 0.1, 1.0), 0.0, 1.0));
    let floor = scene.add_geometry(primitives::cuboid(40.0, 1.0, 40.0, 1));
    let node = scene.add_mesh(None, floor, material).unwrap();
    scene.node_mut(node).unwrap().transform.translation = Vec3::new(0.0, -1.5, 0.0);

    let camera_node = scene.create_node(None).unwrap();
    {
        let node = scene.node_mut(camera_node).unwrap();
        node.camera = Some(Camera::perspective(std::f32::consts::FRAC_PI_3, 0.1, 100.0));
        node.transform.translation = Vec3::new(0.0, 0.0, 6.0);
    }
    scene.mark_dirty(scene.root());
    (scene, camera_node)
}

fn pixel(pixels: &[u8], x: u32, y: u32) -> [u8; 3] {
    let i = ((y * SIZE + x) * 4) as usize;
    [pixels[i], pixels[i + 1], pixels[i + 2]]
}

#[test]
fn the_procedural_sky_fills_the_background_in_both_paths() {
    for path in [RenderPath::Forward, RenderPath::Deferred] {
        let Some(mut renderer) = renderer(path) else {
            return;
        };

        let (mut scene, camera) = scene_with_floor(SkyMode::Color);
        renderer.render(&mut scene, Some(camera)).unwrap();
        let without = renderer.read_pixels().unwrap();
        let top_before = pixel(&without, SIZE / 2, 4);
        assert!(
            top_before.iter().all(|c| *c < 12),
            "{path:?} background should start black, got {top_before:?}"
        );

        scene.environment.sky = SkyMode::Procedural;
        renderer.render(&mut scene, Some(camera)).unwrap();
        let with = renderer.read_pixels().unwrap();
        let top = pixel(&with, SIZE / 2, 4);

        assert!(
            top[2] > 40 && top[2] > top[0],
            "{path:?} the sky should be blue overhead, got {top:?}"
        );

        // The floor still occludes it: the bottom of the frame stays red.
        let bottom = pixel(&with, SIZE / 2, SIZE - 4);
        assert!(
            bottom[0] > bottom[2] + 20,
            "{path:?} the sky painted over the floor, got {bottom:?}"
        );
    }
}

#[test]
fn the_horizon_is_brighter_than_the_zenith() {
    let Some(mut renderer) = renderer(RenderPath::Forward) else {
        return;
    };
    let (mut scene, camera) = scene_with_floor(SkyMode::Procedural);
    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();

    let zenith = pixel(&pixels, SIZE / 2, 2);
    // Just above the floor edge, which sits a little below the middle.
    let horizon = pixel(&pixels, SIZE / 2, SIZE / 2 - 6);
    let luminance = |c: [u8; 3]| c[0] as u32 + c[1] as u32 + c[2] as u32;
    assert!(
        luminance(horizon) > luminance(zenith),
        "horizon {horizon:?} should be brighter than zenith {zenith:?}"
    );
}

#[test]
fn a_debug_view_keeps_the_sky_out_of_the_picture() {
    let Some(mut renderer) = renderer(RenderPath::Forward) else {
        return;
    };
    let (mut scene, camera) = scene_with_floor(SkyMode::Procedural);

    let mut config = renderer.config();
    config.debug_view = DebugView::BaseColor;
    renderer.set_config(config);
    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();

    let top = pixel(&pixels, SIZE / 2, 4);
    assert!(
        top.iter().all(|c| *c < 12),
        "a debug view should show the background, not the sky: {top:?}"
    );
}
