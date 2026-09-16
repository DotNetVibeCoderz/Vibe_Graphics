//! glTF animation import and playback, including CPU skinning, on Khronos
//! sample models (see `tests/assets/README.md` for attribution).

use threenet_core::animation::AnimationPlayer;
use threenet_core::loaders::load_gltf;
use threenet_core::scene::Scene;

fn asset(name: &str) -> String {
    format!("{}/tests/assets/{name}", env!("CARGO_MANIFEST_DIR"))
}

#[test]
fn keyframe_animation_moves_nodes() {
    let mut scene = Scene::new();
    let import = load_gltf(&mut scene, &asset("BoxAnimated.glb"), None).unwrap();
    assert!(!import.animations.is_empty(), "BoxAnimated has an animation");

    let clip = import.animations[0];
    let duration = scene.animation(clip).unwrap().duration();
    assert!(duration > 1.0, "duration {duration}");

    let snapshot = |scene: &Scene| -> Vec<[f32; 7]> {
        import
            .nodes
            .iter()
            .filter_map(|id| scene.node(*id))
            .map(|node| {
                let t = node.transform;
                [t.translation.x, t.translation.y, t.translation.z, t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w]
            })
            .collect()
    };

    let player = scene.play_animation(AnimationPlayer::new(clip)).unwrap();
    scene.update_animations(0.0);
    let start = snapshot(&scene);
    scene.update_animations(duration * 0.4);
    let later = snapshot(&scene);
    assert_ne!(start, later, "the animation should move at least one node");
    assert!((scene.player(player).unwrap().time - duration * 0.4).abs() < 1e-3);

    // Looping wraps the time back into the clip.
    scene.update_animations(duration);
    let time = scene.player(player).unwrap().time;
    assert!(time < duration, "looping time {time}");

    // A non looping player stops at the end.
    scene.player_mut(player).unwrap().looping = false;
    scene.update_animations(duration * 2.0);
    assert!(!scene.player(player).unwrap().playing);
}

#[test]
fn skinned_meshes_are_deformed() {
    let mut scene = Scene::new();
    let import = load_gltf(&mut scene, &asset("RiggedSimple.glb"), None).unwrap();
    assert_eq!(import.skins.len(), 1);
    assert!(!import.animations.is_empty());

    let geometry_id = import
        .nodes
        .iter()
        .filter_map(|id| scene.node(*id))
        .find_map(|node| node.mesh.filter(|m| m.skin.is_some()).map(|m| m.geometry))
        .expect("a skinned mesh node");
    assert!(scene.geometry(geometry_id).unwrap().skin.is_some());

    scene.play_animation(AnimationPlayer::new(import.animations[0])).unwrap();
    scene.update_animations(0.0);
    let before = scene.geometry(geometry_id).unwrap().vertices.clone();
    let version = scene.geometry(geometry_id).unwrap().version();
    scene.update_animations(1.0);
    let geometry = scene.geometry(geometry_id).unwrap();
    assert!(geometry.version() != version, "deformed geometry must be re-uploaded");

    let moved = before
        .iter()
        .zip(&geometry.vertices)
        .filter(|(a, b)| {
            let d = [a.position[0] - b.position[0], a.position[1] - b.position[1], a.position[2] - b.position[2]];
            (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt() > 1e-3
        })
        .count();
    assert!(moved > before.len() / 10, "only {moved} of {} vertices moved", before.len());
}
