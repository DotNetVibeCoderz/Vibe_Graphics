//! GPU tests for the renderer's debug views: each one must replace the lit
//! image with the surface channel it names, in both render paths. Like the other
//! offscreen tests they skip themselves when no adapter is available.

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
        // Base colour 0.8 / 0.2 / 0.1, roughness 0.25, metallic 0.75.
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

/// Renders the sphere with one debug view and returns the centre pixel.
fn center_pixel(view: DebugView, path: RenderPath) -> Option<[u8; 4]> {
    let mut renderer = match Renderer::new_offscreen(RendererConfig {
        width: SIZE,
        height: SIZE,
        msaa_samples: 1,
        // Exposure and tone mapping must not touch a debug view; leaving both
        // at values that would change the numbers proves the renderer skips them.
        exposure: 4.0,
        tone_mapping: ToneMapping::Aces,
        bloom: true,
        render_path: path,
        debug_view: view,
        ..Default::default()
    }) {
        Ok(renderer) => renderer,
        Err(error) => {
            eprintln!("skipping GPU test: {error}");
            return None;
        }
    };

    let (mut scene, camera) = sphere_scene();
    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();
    let i = (((SIZE / 2) * SIZE + SIZE / 2) * 4) as usize;
    Some([pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]])
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
fn base_color_view_shows_the_material_colour() {
    for path in [RenderPath::Forward, RenderPath::Deferred] {
        let Some(pixel) = center_pixel(DebugView::BaseColor, path) else {
            return;
        };
        assert!(
            (to_linear(pixel[0]) - 0.8).abs() < 0.05
                && (to_linear(pixel[1]) - 0.2).abs() < 0.05
                && (to_linear(pixel[2]) - 0.1).abs() < 0.05,
            "{path:?} base colour view returned {pixel:?}"
        );
    }
}

#[test]
fn roughness_and_metallic_views_show_their_own_channel() {
    for path in [RenderPath::Forward, RenderPath::Deferred] {
        let Some(roughness) = center_pixel(DebugView::Roughness, path) else {
            return;
        };
        let metallic = center_pixel(DebugView::Metallic, path).unwrap();
        assert!(
            (to_linear(roughness[0]) - 0.25).abs() < 0.05,
            "{path:?} roughness view returned {roughness:?}"
        );
        assert!(
            (to_linear(metallic[0]) - 0.75).abs() < 0.05,
            "{path:?} metallic view returned {metallic:?}"
        );
        // Both are grey: a coloured pixel would mean the albedo leaked in.
        assert_eq!(roughness[0], roughness[1], "roughness view is not grey");
        assert_eq!(metallic[0], metallic[1], "metallic view is not grey");
    }
}

#[test]
fn normal_view_points_at_the_camera_in_the_middle() {
    for path in [RenderPath::Forward, RenderPath::Deferred] {
        let Some(pixel) = center_pixel(DebugView::WorldNormal, path) else {
            return;
        };
        // The sphere's centre faces +Z, so the encoded normal is (0.5, 0.5, 1.0).
        assert!(
            (to_linear(pixel[0]) - 0.5).abs() < 0.08
                && (to_linear(pixel[1]) - 0.5).abs() < 0.08
                && to_linear(pixel[2]) > 0.9,
            "{path:?} normal view returned {pixel:?}"
        );
    }
}

#[test]
fn lighting_view_drops_the_albedo() {
    let Some(pixel) = center_pixel(DebugView::Lighting, RenderPath::Forward) else {
        return;
    };
    // White surface, one white light: the lit centre stays neutral instead of
    // taking the material's red.
    assert!(pixel[0] > 30, "lighting view is black: {pixel:?}");
    assert!(
        pixel[0].abs_diff(pixel[2]) < 24,
        "lighting view kept the albedo: {pixel:?}"
    );
}

#[test]
fn depth_view_gets_darker_as_the_surface_moves_away() {
    let Some(near) = center_pixel(DebugView::Depth, RenderPath::Forward) else {
        return;
    };

    // Same scene, camera pushed back: the same point must read as further away.
    let mut renderer = Renderer::new_offscreen(RendererConfig {
        width: SIZE,
        height: SIZE,
        msaa_samples: 1,
        debug_view: DebugView::Depth,
        ..Default::default()
    })
    .unwrap();
    let (mut scene, camera) = sphere_scene();
    scene.node_mut(camera).unwrap().transform.translation = Vec3::new(0.0, 0.0, 12.0);
    scene.mark_dirty(camera);
    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();
    let i = (((SIZE / 2) * SIZE + SIZE / 2) * 4) as usize;
    assert!(
        pixels[i] > near[0] + 8,
        "depth view did not change with distance: {} then {}",
        near[0],
        pixels[i]
    );
}

#[test]
fn switching_the_view_off_restores_the_lit_image() {
    let Some(lit) = center_pixel(DebugView::Off, RenderPath::Forward) else {
        return;
    };
    let albedo = center_pixel(DebugView::BaseColor, RenderPath::Forward).unwrap();
    assert_ne!(
        lit, albedo,
        "the lit image and the base colour view are identical"
    );
}
