//! Depth of field and motion blur, in both render paths. Blur is measured as
//! the loss of local contrast on a high frequency checkerboard.

use threenet_core::camera::Camera;
use threenet_core::geometry::primitives;
use threenet_core::material::Material;
use threenet_core::math::{Vec3, Vec4};
use threenet_core::renderer::{RenderPath, Renderer, RendererConfig, ToneMapping};
use threenet_core::scene::Scene;
use threenet_core::texture::{Texture, TextureFormat};

const SIZE: u32 = 128;

fn renderer(path: RenderPath, configure: impl Fn(&mut RendererConfig)) -> Option<Renderer> {
    let mut config = RendererConfig {
        width: SIZE,
        height: SIZE,
        msaa_samples: 1,
        tone_mapping: ToneMapping::None,
        render_path: path,
        ..Default::default()
    };
    configure(&mut config);
    Renderer::new_offscreen(config)
        .map_err(|error| eprintln!("skipping GPU test: {error}"))
        .ok()
}

/// Two checkerboard walls: one 3 m from the camera, one 30 m away.
fn scene() -> (Scene, u32) {
    let mut scene = Scene::new();
    scene.environment.background = [0.0, 0.0, 0.0, 1.0];
    scene.environment.ambient_intensity = 1.0;

    let mut pixels = Vec::with_capacity(64 * 64 * 4);
    for y in 0..64 {
        for x in 0..64 {
            let on = ((x / 4) + (y / 4)) % 2 == 0;
            let value = if on { 255 } else { 0 };
            pixels.extend_from_slice(&[value, value, value, 255]);
        }
    }
    let texture = scene.add_texture(Texture::new(64, 64, TextureFormat::Rgba8UnormSrgb, pixels).unwrap());
    let mut material = Material::basic(Vec4::ONE);
    material.textures.base_color = Some(texture);
    let checker = scene.add_material(material);

    let near = scene.add_geometry(primitives::plane(1.2, 2.4, 1, 1));
    let near_node = scene.add_mesh(None, near, checker).unwrap();
    scene.node_mut(near_node).unwrap().transform.translation = Vec3::new(-0.6, 0.0, -3.0);

    let far = scene.add_geometry(primitives::plane(12.0, 24.0, 1, 1));
    let far_node = scene.add_mesh(None, far, checker).unwrap();
    scene.node_mut(far_node).unwrap().transform.translation = Vec3::new(6.0, 0.0, -30.0);

    let camera = scene.create_node(None).unwrap();
    scene.node_mut(camera).unwrap().camera = Some(Camera::perspective(0.8, 0.1, 100.0));
    scene.mark_dirty(scene.root());
    (scene, camera)
}

/// Mean absolute difference between horizontal neighbours in a column band.
fn contrast(pixels: &[u8], x0: u32, x1: u32) -> f32 {
    let mut total = 0f32;
    let mut count = 0f32;
    for y in 30..98 {
        for x in x0..x1 {
            let a = pixels[((y * SIZE + x) * 4) as usize] as f32;
            let b = pixels[((y * SIZE + x + 1) * 4) as usize] as f32;
            total += (a - b).abs();
            count += 1.0;
        }
    }
    total / count
}

#[test]
fn depth_of_field_blurs_only_out_of_focus_areas() {
    for path in [RenderPath::Forward, RenderPath::Deferred] {
        let Some(mut sharp) = renderer(path, |_| {}) else {
            return;
        };
        let Some(mut dof) = renderer(path, |c| {
            c.depth_of_field = true;
            c.dof_focus_distance = 3.0;
            c.dof_focus_range = 1.0;
            c.dof_max_blur = 60.0;
        }) else {
            return;
        };

        let (mut scene_a, camera_a) = scene();
        let (mut scene_b, camera_b) = scene();
        sharp.render(&mut scene_a, Some(camera_a)).unwrap();
        dof.render(&mut scene_b, Some(camera_b)).unwrap();
        let a = sharp.read_pixels().unwrap();
        let b = dof.read_pixels().unwrap();

        // Left half shows the near (focused) wall, right half the far wall.
        let near_ratio = contrast(&b, 20, 44) / contrast(&a, 20, 44).max(1.0);
        let far_ratio = contrast(&b, 84, 108) / contrast(&a, 84, 108).max(1.0);
        assert!(near_ratio > 0.7, "{path:?}: the focused wall should stay sharp ({near_ratio:.2})");
        assert!(far_ratio < 0.6, "{path:?}: the distant wall should blur ({far_ratio:.2})");
    }
}

#[test]
fn motion_blur_smears_camera_motion() {
    for path in [RenderPath::Forward, RenderPath::Deferred] {
        let Some(mut renderer) = renderer(path, |c| {
            c.motion_blur = true;
            c.motion_blur_strength = 1.0;
            c.motion_blur_samples = 16;
        }) else {
            return;
        };
        let (mut scene, camera) = scene();

        // A still frame (twice, so the previous view equals the current one).
        renderer.render(&mut scene, Some(camera)).unwrap();
        renderer.render(&mut scene, Some(camera)).unwrap();
        let still = renderer.read_pixels().unwrap();

        // Pan the camera sideways between two frames.
        scene.node_mut(camera).unwrap().transform.set_euler_angles(Vec3::new(0.0, 0.08, 0.0));
        scene.mark_dirty(camera);
        renderer.render(&mut scene, Some(camera)).unwrap();
        let moving = renderer.read_pixels().unwrap();

        let ratio = contrast(&moving, 84, 108) / contrast(&still, 84, 108).max(1.0);
        assert!(ratio < 0.7, "{path:?}: panning should smear the checkerboard ({ratio:.2})");
    }
}
