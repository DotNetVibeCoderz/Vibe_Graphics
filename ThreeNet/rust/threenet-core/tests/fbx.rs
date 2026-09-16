//! FBX import against the assimp test models (see tests/assets/README.md).

use threenet_core::animation::AnimationPlayer;
use threenet_core::fbx::load_fbx;
use threenet_core::material::ShadingModel;
use threenet_core::scene::Scene;

fn asset(name: &str) -> String {
    format!("{}/tests/assets/{name}", env!("CARGO_MANIFEST_DIR"))
}

fn triangle_count(scene: &Scene, geometries: &[u32]) -> usize {
    geometries
        .iter()
        .filter_map(|id| scene.geometry(*id))
        .map(|g| g.indices.len() / 3)
        .sum()
}

#[test]
fn binary_box() {
    let mut scene = Scene::new();
    let import = load_fbx(&mut scene, &asset("box.fbx"), None).unwrap();
    assert!(!import.geometries.is_empty());
    assert_eq!(triangle_count(&scene, &import.geometries), 12, "a cube has 12 triangles");

    scene.update_world_transforms();
    let bounds = scene.compute_bounds(import.root);
    // The 200 unit cube carries a geometric rotation and a ~0.0017 geometric scale,
    // with UnitScaleFactor 100 (metres): roughly a 0.35 m box, tilted.
    let size = bounds.max - bounds.min;
    assert!(size.min_element() > 0.3 && size.max_element() < 0.7, "cube bounds {size:?}");
    assert!((size.x - size.y).abs() > 0.01, "the geometric rotation should tilt the box: {size:?}");
}

#[test]
fn compressed_arrays_decode() {
    let mut scene = Scene::new();
    let import = load_fbx(&mut scene, &asset("boxWithCompressedCTypeArray.FBX"), None).unwrap();
    assert!(triangle_count(&scene, &import.geometries) >= 12);
}

#[test]
fn ascii_scene_keeps_names_and_hierarchy() {
    let mut scene = Scene::new();
    let import = load_fbx(&mut scene, &asset("cubes_with_names.fbx"), None).unwrap();
    let names: Vec<String> = import
        .nodes
        .iter()
        .filter_map(|id| scene.node(*id))
        .map(|n| n.name.clone())
        .collect();
    assert!(names.iter().any(|n| n == "Cube2"), "names: {names:?}");
    assert!(names.iter().any(|n| n == "Cube3"), "names: {names:?}");
    assert!(triangle_count(&scene, &import.geometries) >= 24);
}

#[test]
fn phong_materials() {
    let mut scene = Scene::new();
    let import = load_fbx(&mut scene, &asset("phong_cube.fbx"), None).unwrap();
    assert!(
        import
            .materials
            .iter()
            .filter_map(|id| scene.material(*id))
            .any(|m| m.shading == ShadingModel::Phong),
        "a Phong material should be imported"
    );
}

#[test]
fn skeleton_animation_deforms_the_mesh() {
    let mut scene = Scene::new();
    let import = load_fbx(&mut scene, &asset("animation_with_skeleton.fbx"), None).unwrap();
    assert!(!import.animations.is_empty(), "the file has an animation stack");
    let clip = scene.animation(import.animations[0]).unwrap();
    assert!(clip.duration() > 0.0);

    scene.update_world_transforms();
    let before = scene.compute_bounds(import.root);
    scene.play_animation(AnimationPlayer::new(import.animations[0])).unwrap();
    scene.update_animations(clip_duration(&scene, import.animations[0]) * 0.5);
    let after = scene.compute_bounds(import.root);
    assert_ne!(before, after, "the animation should move the model");
}

fn clip_duration(scene: &Scene, id: u32) -> f32 {
    scene.animation(id).unwrap().duration()
}
