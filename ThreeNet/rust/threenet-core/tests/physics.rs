//! Rigid bodies, colliders, contacts, joints, raycasts and characters.

use threenet_core::geometry::primitives;
use threenet_core::math::{Quat, Vec3};
use threenet_core::physics::{BodyDesc, BodyKind, ColliderDesc, JointKind, ShapeDesc};
use threenet_core::scene::Scene;

fn ground(scene: &mut Scene) -> u32 {
    let ground = scene.create_node(None).unwrap();
    scene.node_mut(ground).unwrap().transform.translation = Vec3::new(0.0, -0.5, 0.0);
    scene.mark_dirty(ground);
    scene
        .physics_add_collider(ground, ColliderDesc::new(ShapeDesc::Box { half_extents: Vec3::new(20.0, 0.5, 20.0) }))
        .unwrap();
    ground
}

fn run(scene: &mut Scene, seconds: f32) {
    let frames = (seconds * 60.0) as usize;
    for _ in 0..frames {
        scene.physics_step(1.0 / 60.0);
    }
}

#[test]
fn falling_bodies_land_and_report_contacts() {
    let mut scene = Scene::new();
    let floor = ground(&mut scene);

    let ball = scene.create_node(None).unwrap();
    scene.node_mut(ball).unwrap().transform.translation = Vec3::new(0.0, 5.0, 0.0);
    scene.mark_dirty(ball);
    scene.physics_add_body(ball, BodyDesc::default()).unwrap();
    scene.physics_add_collider(ball, ColliderDesc::new(ShapeDesc::Sphere { radius: 0.5 })).unwrap();
    assert!(scene.physics_add_body(ball, BodyDesc::default()).is_err(), "one body per node");

    run(&mut scene, 0.5);
    let y = scene.node(ball).unwrap().transform.translation.y;
    assert!(y < 4.0 && y > 0.4, "falling: y = {y}");

    run(&mut scene, 3.0);
    let rest = scene.node(ball).unwrap().transform.translation.y;
    assert!((rest - 0.5).abs() < 0.05, "resting on the floor: y = {rest}");

    let events = scene.physics_take_events();
    assert!(
        events.iter().any(|e| e.started && [e.node_a, e.node_b].contains(&ball) && [e.node_a, e.node_b].contains(&floor)),
        "contact event {events:?}"
    );
    assert!(scene.physics_take_events().is_empty(), "events are drained");
}

#[test]
fn raycasts_forces_and_teleport() {
    let mut scene = Scene::new();
    let floor = ground(&mut scene);
    let crate_node = scene.create_node(None).unwrap();
    scene.node_mut(crate_node).unwrap().transform.translation = Vec3::new(3.0, 0.5, 0.0);
    scene.mark_dirty(crate_node);
    scene.physics_add_body(crate_node, BodyDesc { can_sleep: false, ..Default::default() }).unwrap();
    scene
        .physics_add_collider(crate_node, ColliderDesc { friction: 0.0, ..ColliderDesc::new(ShapeDesc::Box { half_extents: Vec3::splat(0.5) }) })
        .unwrap();
    scene.physics_step(1.0 / 60.0);

    let hit = scene.physics_raycast(Vec3::new(3.0, 10.0, 0.0), Vec3::NEG_Y, 100.0, None).unwrap();
    assert_eq!(hit.node, crate_node);
    assert!((hit.distance - 9.0).abs() < 0.05, "distance {}", hit.distance);
    assert!(hit.normal.y > 0.99);
    let below = scene.physics_raycast(Vec3::new(3.0, 10.0, 0.0), Vec3::NEG_Y, 100.0, Some(crate_node)).unwrap();
    assert_eq!(below.node, floor);

    // A crate pushed sideways slides (friction is averaged with the floor's, so it slows down).
    scene.physics_apply_impulse(crate_node, Vec3::new(5.0, 0.0, 0.0), Vec3::ZERO).unwrap();
    run(&mut scene, 1.0);
    let (velocity, _) = scene.physics_velocity(crate_node).unwrap();
    assert!(velocity.x > 1.0 && velocity.x < 5.0, "velocity {velocity}");
    assert!(scene.node(crate_node).unwrap().transform.translation.x > 5.0);

    scene.physics_teleport(crate_node, Vec3::new(-5.0, 2.0, 0.0), Quat::IDENTITY).unwrap();
    assert!((scene.node(crate_node).unwrap().transform.translation.x + 5.0).abs() < 1e-4);

    // Removing the node removes the body on the next step.
    scene.remove_node(crate_node).unwrap();
    scene.physics_step(1.0 / 60.0);
    assert!(!scene.physics_has_body(crate_node));
}

#[test]
fn trimesh_ground_hinges_and_kinematic_characters() {
    let mut scene = Scene::new();
    let terrain = scene.create_node(None).unwrap();
    let plane = scene.add_geometry(primitives::plane(40.0, 40.0, 4, 4));
    // Planes face +Z; lay it flat.
    scene.node_mut(terrain).unwrap().transform.rotation = Quat::from_rotation_x(-std::f32::consts::FRAC_PI_2);
    scene.mark_dirty(terrain);
    scene.physics_add_collider(terrain, ColliderDesc::new(ShapeDesc::TriMesh { geometry: plane })).unwrap();

    // A door hinged to a fixed post swings when pushed.
    let post = scene.create_node(None).unwrap();
    scene.node_mut(post).unwrap().transform.translation = Vec3::new(5.0, 1.0, 0.0);
    scene.mark_dirty(post);
    scene.physics_add_body(post, BodyDesc { kind: BodyKind::Fixed, ..Default::default() }).unwrap();
    let door = scene.create_node(None).unwrap();
    scene.node_mut(door).unwrap().transform.translation = Vec3::new(5.6, 1.0, 0.0);
    scene.mark_dirty(door);
    scene.physics_add_body(door, BodyDesc { gravity_scale: 0.0, can_sleep: false, ..Default::default() }).unwrap();
    scene
        .physics_add_collider(door, ColliderDesc::new(ShapeDesc::Box { half_extents: Vec3::new(0.5, 0.9, 0.05) }))
        .unwrap();
    let joint = scene
        .physics_add_joint(JointKind::Hinge, post, door, Vec3::ZERO, Vec3::new(-0.6, 0.0, 0.0), Vec3::Y)
        .unwrap();
    scene.physics_apply_impulse(door, Vec3::new(0.0, 0.0, 2.0), Vec3::ZERO).unwrap();
    run(&mut scene, 0.5);
    let door_position = scene.node(door).unwrap().transform.translation;
    let from_post = door_position - Vec3::new(5.0, 1.0, 0.0);
    assert!((from_post.length() - 0.6).abs() < 0.05, "the hinge keeps the distance: {from_post}");
    assert!(door_position.z > 0.05, "the door swung: {door_position}");
    assert!(scene.physics_remove_joint(joint));
    assert!(!scene.physics_remove_joint(joint));

    // Character: walks into a wall, slides, stays grounded on the trimesh.
    let wall = scene.create_node(None).unwrap();
    scene.node_mut(wall).unwrap().transform.translation = Vec3::new(-2.0, 1.0, 0.0);
    scene.mark_dirty(wall);
    scene
        .physics_add_collider(wall, ColliderDesc::new(ShapeDesc::Box { half_extents: Vec3::new(0.2, 1.0, 5.0) }))
        .unwrap();

    let player = scene.create_node(None).unwrap();
    scene.node_mut(player).unwrap().transform.translation = Vec3::new(0.0, 0.9, 0.0);
    scene.mark_dirty(player);
    scene.physics_add_body(player, BodyDesc { kind: BodyKind::Kinematic, ..Default::default() }).unwrap();
    scene
        .physics_add_collider(player, ColliderDesc::new(ShapeDesc::Capsule { half_height: 0.5, radius: 0.4 }))
        .unwrap();
    scene.physics_step(1.0 / 60.0);

    let mut grounded = false;
    for _ in 0..120 {
        // Walk left and forward with gravity.
        let (_, on_ground) = scene
            .physics_move_character(player, Vec3::new(-0.1, -0.05, 0.05), 1.0 / 60.0)
            .unwrap();
        grounded = on_ground;
        scene.physics_step(1.0 / 60.0);
    }
    let position = scene.node(player).unwrap().transform.translation;
    assert!(grounded, "character should stand on the ground");
    assert!(position.x > -1.45, "the wall stops the character: {position}");
    assert!(position.z > 2.0, "sliding along the wall still moves forward: {position}");
    assert!((position.y - 0.9).abs() < 0.1, "height {position}");
}
