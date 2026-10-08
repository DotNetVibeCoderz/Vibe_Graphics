//! Morph targets: blending on the CPU, importing from glTF, and the way they
//! stack with skinning. None of these need a GPU.

use threenet_core::geometry::{Geometry, MorphTarget, MorphTargets, Vertex};
use threenet_core::math::{Vec2, Vec3};
use threenet_core::scene::Scene;

/// Two vertices, so a target that moves only the second one is easy to read.
fn pair() -> Geometry {
    Geometry::new(
        vec![
            Vertex {
                position: [0.0, 0.0, 0.0],
                normal: [0.0, 1.0, 0.0],
                uv: Vec2::ZERO.to_array(),
                tangent: [1.0, 0.0, 0.0, 1.0],
            },
            Vertex {
                position: [1.0, 0.0, 0.0],
                normal: [0.0, 1.0, 0.0],
                uv: Vec2::ZERO.to_array(),
                tangent: [1.0, 0.0, 0.0, 1.0],
            },
        ],
        vec![0, 1, 0],
    )
}

fn with_targets() -> Geometry {
    let mut geometry = pair();
    let base = geometry.vertices.clone();
    geometry.morph = Some(MorphTargets::new(
        base,
        vec![
            MorphTarget {
                name: "up".into(),
                positions: vec![Vec3::ZERO, Vec3::new(0.0, 2.0, 0.0)],
                normals: Vec::new(),
            },
            MorphTarget {
                name: "forward".into(),
                positions: vec![Vec3::ZERO, Vec3::new(0.0, 0.0, 4.0)],
                normals: Vec::new(),
            },
        ],
    ));
    geometry
}

#[test]
fn weights_blend_the_targets_into_the_vertices() {
    let mut geometry = with_targets();

    assert!(geometry.apply_morph(&[0.5, 0.0]));
    assert_eq!(geometry.vertices[1].position, [1.0, 1.0, 0.0]);
    // The untouched vertex stays where it was.
    assert_eq!(geometry.vertices[0].position, [0.0, 0.0, 0.0]);

    // Targets add up.
    assert!(geometry.apply_morph(&[1.0, 0.25]));
    assert_eq!(geometry.vertices[1].position, [1.0, 2.0, 1.0]);

    // Back to zero is back to the rest pose, not to wherever it drifted.
    assert!(geometry.apply_morph(&[0.0, 0.0]));
    assert_eq!(geometry.vertices[1].position, [1.0, 0.0, 0.0]);
}

#[test]
fn an_unchanged_pose_costs_nothing() {
    let mut geometry = with_targets();
    assert!(geometry.apply_morph(&[0.5, 0.5]));
    assert!(
        !geometry.apply_morph(&[0.5, 0.5]),
        "the same weights twice should be skipped"
    );
    assert!(geometry.apply_morph(&[0.5, 0.4]));
}

#[test]
fn normals_follow_the_shape_and_stay_unit_length() {
    let mut geometry = pair();
    let base = geometry.vertices.clone();
    geometry.morph = Some(MorphTargets::new(
        base,
        vec![MorphTarget {
            name: "tilt".into(),
            positions: vec![Vec3::ZERO, Vec3::ZERO],
            normals: vec![Vec3::ZERO, Vec3::new(1.0, 0.0, 0.0)],
        }],
    ));

    geometry.apply_morph(&[1.0]);
    let normal = Vec3::from_array(geometry.vertices[1].normal);
    assert!((normal.length() - 1.0).abs() < 1.0e-5, "normal was not renormalised");
    assert!(normal.x > 0.6 && normal.y > 0.6, "normal did not tilt: {normal:?}");
}

#[test]
fn the_scene_deforms_a_node_from_its_weights() {
    let mut scene = Scene::new();
    let geometry = scene.add_geometry(with_targets());
    let material = scene.add_material(threenet_core::material::Material::default());
    let node = scene.add_mesh(None, geometry, material).unwrap();

    scene.node_mut(node).unwrap().morph_weights = vec![1.0, 0.0];
    scene.update_animations(0.0);
    assert_eq!(
        scene.geometry(geometry).unwrap().vertices[1].position,
        [1.0, 2.0, 0.0]
    );

    // The bounds have to follow, or culling and picking read the rest pose.
    let bounds = scene.geometry(geometry).unwrap().bounds;
    assert!(bounds.max.y >= 2.0, "bounds did not grow with the shape");
}

#[test]
fn gltf_morph_targets_arrive_with_their_names() {
    let mut scene = Scene::new();
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/assets/morph-cube.glb");
    let result = threenet_core::loaders::load_gltf(&mut scene, path, None).expect("import");

    let geometry_id = *result.geometries.first().expect("one geometry");
    let geometry = scene.geometry(geometry_id).expect("geometry");
    let morph = geometry.morph.as_ref().expect("the cube has morph targets");

    assert_eq!(morph.len(), 2, "two shape keys were exported");
    assert_eq!(morph.name(0), "stretch");
    assert_eq!(morph.name(1), "lean");
    assert_eq!(morph.base.len(), geometry.vertices.len());

    // The node the mesh landed on carries one weight per target, all at rest.
    let node = result
        .nodes
        .iter()
        .copied()
        .find(|id| {
            scene
                .node(*id)
                .and_then(|n| n.mesh)
                .is_some_and(|mesh| mesh.geometry == geometry_id)
        })
        .expect("a node holds the mesh");
    assert_eq!(scene.node(node).unwrap().morph_weights.len(), 2);

    // 'stretch' lifts the top of the cube by one unit.
    let rest_top = scene.geometry(geometry_id).unwrap().bounds.max.y;
    scene.node_mut(node).unwrap().morph_weights = vec![1.0, 0.0];
    scene.update_animations(0.0);
    let stretched_top = scene.geometry(geometry_id).unwrap().bounds.max.y;
    assert!(
        stretched_top > rest_top + 0.9,
        "stretch did not raise the cube: {rest_top} -> {stretched_top}"
    );
}
