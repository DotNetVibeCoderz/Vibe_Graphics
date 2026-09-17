//! Rigid body physics with Rapier.
//!
//! Bodies and colliders are attached to scene nodes. [`Scene::physics_step`]
//! advances the simulation with a fixed timestep, feeds kinematic node
//! transforms into Rapier and writes dynamic body poses back to their nodes.
//! Collider shapes are given in world units; triangle meshes and convex hulls
//! take the node's world scale when they are created.

use std::collections::HashMap;
use std::sync::Mutex;
use std::sync::mpsc::{Receiver, channel};

use rapier3d::control::KinematicCharacterController;
use rapier3d::prelude::*;

use crate::error::{Error, Result};
use crate::math::{Mat4, Quat, Vec3};
use crate::scene::{GeometryId, NodeId, Scene};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum BodyKind {
    Dynamic = 0,
    Fixed = 1,
    /// Moved by its node (animation, gameplay code); pushes dynamic bodies.
    Kinematic = 2,
}

impl BodyKind {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => BodyKind::Fixed,
            2 => BodyKind::Kinematic,
            _ => BodyKind::Dynamic,
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct BodyDesc {
    pub kind: BodyKind,
    /// Extra mass added to what the colliders' densities give.
    pub additional_mass: f32,
    pub linear_damping: f32,
    pub angular_damping: f32,
    pub gravity_scale: f32,
    /// Continuous collision detection for fast objects.
    pub ccd: bool,
    pub lock_rotations: bool,
    pub can_sleep: bool,
    pub linear_velocity: Vec3,
    pub angular_velocity: Vec3,
}

impl Default for BodyDesc {
    fn default() -> Self {
        Self {
            kind: BodyKind::Dynamic,
            additional_mass: 0.0,
            linear_damping: 0.0,
            angular_damping: 0.05,
            gravity_scale: 1.0,
            ccd: false,
            lock_rotations: false,
            can_sleep: true,
            linear_velocity: Vec3::ZERO,
            angular_velocity: Vec3::ZERO,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum ShapeDesc {
    Box { half_extents: Vec3 },
    Sphere { radius: f32 },
    /// Along the local Y axis.
    Capsule { half_height: f32, radius: f32 },
    /// Along the local Y axis.
    Cylinder { half_height: f32, radius: f32 },
    /// Exact triangles of a geometry (best for static level geometry).
    TriMesh { geometry: GeometryId },
    ConvexHull { geometry: GeometryId },
}

#[derive(Debug, Clone, Copy)]
pub struct ColliderDesc {
    pub shape: ShapeDesc,
    /// Offset from the node origin, in the node's rotated frame.
    pub offset: Vec3,
    pub rotation: Quat,
    pub friction: f32,
    pub restitution: f32,
    pub density: f32,
    /// Sensors report contacts but do not collide.
    pub sensor: bool,
    /// Collision groups: this collider's groups and the groups it collides with.
    pub membership: u32,
    pub filter: u32,
}

impl ColliderDesc {
    pub fn new(shape: ShapeDesc) -> Self {
        Self {
            shape,
            offset: Vec3::ZERO,
            rotation: Quat::IDENTITY,
            friction: 0.5,
            restitution: 0.0,
            density: 1.0,
            sensor: false,
            membership: u32::MAX,
            filter: u32::MAX,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum JointKind {
    Fixed = 0,
    /// Ball and socket.
    Ball = 1,
    /// Rotates around `axis`.
    Hinge = 2,
    /// Slides along `axis`.
    Slider = 3,
}

impl JointKind {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => JointKind::Ball,
            2 => JointKind::Hinge,
            3 => JointKind::Slider,
            _ => JointKind::Fixed,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PhysicsHit {
    pub node: NodeId,
    pub distance: f32,
    pub point: Vec3,
    pub normal: Vec3,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ContactEvent {
    pub node_a: NodeId,
    pub node_b: NodeId,
    pub started: bool,
    pub sensor: bool,
}

pub struct PhysicsState {
    pub(crate) world: PhysicsWorld,
    bodies: HashMap<NodeId, RigidBodyHandle>,
    /// Colliders without a body (static geometry), by node.
    static_colliders: HashMap<NodeId, Vec<ColliderHandle>>,
    joints: Vec<Option<ImpulseJointHandle>>,
    events: Mutex<Receiver<CollisionEvent>>,
    collector: ChannelEventCollector,
    pending_events: Vec<ContactEvent>,
    controller: KinematicCharacterController,
    accumulator: f32,
    /// Seconds per simulation step.
    pub fixed_timestep: f32,
    pub max_substeps: u32,
}

impl std::fmt::Debug for PhysicsState {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PhysicsState")
            .field("bodies", &self.bodies.len())
            .field("fixed_timestep", &self.fixed_timestep)
            .finish()
    }
}

impl Default for PhysicsState {
    fn default() -> Self {
        let (collision_send, collision_receive) = channel();
        let (force_send, _force_receive) = channel();
        Self {
            world: PhysicsWorld::default(),
            bodies: HashMap::new(),
            static_colliders: HashMap::new(),
            joints: Vec::new(),
            events: Mutex::new(collision_receive),
            collector: ChannelEventCollector::new(collision_send, force_send),
            pending_events: Vec::new(),
            controller: KinematicCharacterController::default(),
            accumulator: 0.0,
            fixed_timestep: 1.0 / 60.0,
            max_substeps: 8,
        }
    }
}

impl PhysicsState {
    pub fn gravity(&self) -> Vec3 {
        self.world.gravity
    }

    pub fn set_gravity(&mut self, gravity: Vec3) {
        self.world.gravity = gravity;
    }

    pub fn body_count(&self) -> usize {
        self.bodies.len()
    }

    fn node_of_collider(&self, handle: ColliderHandle) -> Option<NodeId> {
        self.world.colliders.get(handle).map(|c| c.user_data as NodeId)
    }
}

fn pose_from_matrix(matrix: Mat4) -> (Pose, Vec3) {
    let (scale, rotation, translation) = matrix.to_scale_rotation_translation();
    (Pose::from_parts(translation, rotation.normalize()), scale)
}

impl Scene {
    fn node_world(&mut self, node: NodeId) -> Result<Mat4> {
        self.world_matrix(node).ok_or(Error::InvalidHandle("node"))
    }

    /// Adds a rigid body at the node's current world pose. One body per node.
    pub fn physics_add_body(&mut self, node: NodeId, desc: BodyDesc) -> Result<()> {
        let world = self.node_world(node)?;
        if self.physics.bodies.contains_key(&node) {
            return Err(Error::InvalidArgument("the node already has a rigid body".into()));
        }
        let (pose, _) = pose_from_matrix(world);
        let builder = match desc.kind {
            BodyKind::Dynamic => RigidBodyBuilder::dynamic(),
            BodyKind::Fixed => RigidBodyBuilder::fixed(),
            BodyKind::Kinematic => RigidBodyBuilder::kinematic_position_based(),
        };
        let mut builder = builder
            .pose(pose)
            .linear_damping(desc.linear_damping)
            .angular_damping(desc.angular_damping)
            .gravity_scale(desc.gravity_scale)
            .ccd_enabled(desc.ccd)
            .can_sleep(desc.can_sleep)
            .linvel(desc.linear_velocity)
            .angvel(desc.angular_velocity)
            .user_data(node as u128);
        if desc.additional_mass > 0.0 {
            builder = builder.additional_mass(desc.additional_mass);
        }
        if desc.lock_rotations {
            builder = builder.lock_rotations();
        }
        let handle = self.physics.world.insert_body(builder);
        self.physics.bodies.insert(node, handle);
        Ok(())
    }

    /// Adds a collider to the node's body, or a static collider when the node has none.
    pub fn physics_add_collider(&mut self, node: NodeId, desc: ColliderDesc) -> Result<()> {
        let world = self.node_world(node)?;
        let (node_pose, scale) = pose_from_matrix(world);
        let scaled_points = |scene: &Scene, geometry: GeometryId| -> Result<(Vec<Vector>, Vec<[u32; 3]>)> {
            let geometry = scene.geometry(geometry).ok_or(Error::InvalidHandle("geometry"))?;
            let points = geometry.vertices.iter().map(|v| Vec3::from(v.position) * scale).collect();
            let triangles = geometry.indices.chunks_exact(3).map(|t| [t[0], t[1], t[2]]).collect();
            Ok((points, triangles))
        };
        let builder = match desc.shape {
            ShapeDesc::Box { half_extents } => ColliderBuilder::cuboid(half_extents.x, half_extents.y, half_extents.z),
            ShapeDesc::Sphere { radius } => ColliderBuilder::ball(radius),
            ShapeDesc::Capsule { half_height, radius } => ColliderBuilder::capsule_y(half_height, radius),
            ShapeDesc::Cylinder { half_height, radius } => ColliderBuilder::cylinder(half_height, radius),
            ShapeDesc::TriMesh { geometry } => {
                let (points, triangles) = scaled_points(self, geometry)?;
                ColliderBuilder::trimesh(points, triangles).map_err(|e| Error::InvalidArgument(format!("triangle mesh: {e}")))?
            }
            ShapeDesc::ConvexHull { geometry } => {
                let (points, _) = scaled_points(self, geometry)?;
                ColliderBuilder::convex_hull(&points).ok_or_else(|| Error::InvalidArgument("convex hull failed (degenerate points)".into()))?
            }
        };
        let local = Pose::from_parts(desc.offset, desc.rotation.normalize());
        let groups = InteractionGroups::new(
            Group::from_bits_retain(desc.membership),
            Group::from_bits_retain(desc.filter),
            InteractionTestMode::And,
        );
        let builder = builder
            .friction(desc.friction)
            .restitution(desc.restitution)
            .density(desc.density.max(0.0))
            .sensor(desc.sensor)
            .collision_groups(groups)
            .active_events(ActiveEvents::COLLISION_EVENTS)
            .user_data(node as u128);

        let state = &mut self.physics;
        match state.bodies.get(&node) {
            Some(&body) => {
                state.world.insert_collider(builder.position(local), Some(body));
            }
            None => {
                let handle = state.world.insert_collider(builder.position(node_pose * local), None);
                state.static_colliders.entry(node).or_default().push(handle);
            }
        }
        Ok(())
    }

    /// Removes the node's body, its colliders and joints.
    pub fn physics_remove(&mut self, node: NodeId) -> bool {
        let state = &mut self.physics;
        let mut removed = false;
        if let Some(body) = state.bodies.remove(&node) {
            state.world.remove_body(body);
            removed = true;
        }
        if let Some(colliders) = state.static_colliders.remove(&node) {
            for collider in colliders {
                state.world.remove_collider(collider);
            }
            removed = true;
        }
        if removed {
            // Joints attached to a removed body are gone from Rapier too.
            let live: Vec<_> = state.world.impulse_joints().map(|(h, _)| h).collect();
            for joint in &mut state.joints {
                if joint.is_some_and(|h| !live.contains(&h)) {
                    *joint = None;
                }
            }
        }
        removed
    }

    pub fn physics_has_body(&self, node: NodeId) -> bool {
        self.physics.bodies.contains_key(&node)
    }

    /// Advances by `delta` seconds in fixed steps. Returns the number of steps taken.
    pub fn physics_step(&mut self, delta: f32) -> u32 {
        // Drop bodies whose nodes were deleted.
        let dead: Vec<NodeId> = self
            .physics
            .bodies
            .keys()
            .chain(self.physics.static_colliders.keys())
            .copied()
            .filter(|node| self.node(*node).is_none())
            .collect();
        for node in dead {
            self.physics_remove(node);
        }

        let step = self.physics.fixed_timestep.max(1e-4);
        self.physics.accumulator += delta.max(0.0);
        let mut steps = 0;
        if self.physics.accumulator >= step {
            self.update_world_transforms();
        }
        while self.physics.accumulator >= step && steps < self.physics.max_substeps {
            self.physics.accumulator -= step;
            steps += 1;

            // Kinematic bodies follow their nodes.
            let kinematic: Vec<(NodeId, RigidBodyHandle)> = self
                .physics
                .bodies
                .iter()
                .filter(|(_, h)| self.physics.world.bodies.get(**h).is_some_and(|b| b.is_kinematic()))
                .map(|(n, h)| (*n, *h))
                .collect();
            for (node, handle) in kinematic {
                if let Some(matrix) = self.node(node).map(|n| n.world_matrix()) {
                    let (pose, _) = pose_from_matrix(matrix);
                    if let Some(body) = self.physics.world.bodies.get_mut(handle) {
                        body.set_next_kinematic_position(pose);
                    }
                }
            }

            let state = &mut self.physics;
            state.world.integration_parameters.dt = step;
            let collector = &state.collector;
            state.world.step_with_events(&(), collector);
        }
        if steps == self.physics.max_substeps {
            // Too far behind: drop the backlog instead of spiralling.
            self.physics.accumulator = 0.0;
        }
        if steps == 0 {
            return 0;
        }

        self.collect_contact_events();
        self.write_back_dynamic_bodies();
        steps
    }

    fn collect_contact_events(&mut self) {
        let received: Vec<CollisionEvent> = match self.physics.events.lock() {
            Ok(receiver) => receiver.try_iter().collect(),
            Err(_) => Vec::new(),
        };
        for event in received {
            let (a, b, started, flags) = match event {
                CollisionEvent::Started(a, b, flags) => (a, b, true, flags),
                CollisionEvent::Stopped(a, b, flags) => (a, b, false, flags),
            };
            let (Some(node_a), Some(node_b)) = (self.physics.node_of_collider(a), self.physics.node_of_collider(b)) else {
                continue;
            };
            self.physics.pending_events.push(ContactEvent {
                node_a,
                node_b,
                started,
                sensor: flags.contains(CollisionEventFlags::SENSOR),
            });
        }
    }

    fn write_back_dynamic_bodies(&mut self) {
        let updates: Vec<(NodeId, Vec3, Quat)> = self
            .physics
            .bodies
            .iter()
            .filter_map(|(node, handle)| {
                let body = self.physics.world.bodies.get(*handle)?;
                (body.is_dynamic() && !body.is_sleeping()).then(|| (*node, body.translation(), *body.rotation()))
            })
            .collect();
        for (node, translation, rotation) in updates {
            let parent_world = self
                .node(node)
                .and_then(|n| n.parent())
                .and_then(|p| self.node(p))
                .map(|p| p.world_matrix())
                .unwrap_or(Mat4::IDENTITY);
            let Some(scale) = self.node(node).map(|n| n.transform.scale) else { continue };
            let world = Mat4::from_rotation_translation(rotation, translation);
            let (_, local_rotation, local_translation) = (parent_world.inverse() * world).to_scale_rotation_translation();
            if let Some(n) = self.node_mut(node) {
                n.transform.translation = local_translation;
                n.transform.rotation = local_rotation.normalize();
                n.transform.scale = scale;
            }
            self.mark_dirty(node);
        }
    }

    /// Contact start / stop events since the last call.
    pub fn physics_take_events(&mut self) -> Vec<ContactEvent> {
        std::mem::take(&mut self.physics.pending_events)
    }

    fn body_mut(&mut self, node: NodeId) -> Result<&mut RigidBody> {
        let handle = *self.physics.bodies.get(&node).ok_or(Error::InvalidHandle("rigid body"))?;
        self.physics.world.bodies.get_mut(handle).ok_or(Error::InvalidHandle("rigid body"))
    }

    pub fn physics_apply_impulse(&mut self, node: NodeId, impulse: Vec3, torque_impulse: Vec3) -> Result<()> {
        let body = self.body_mut(node)?;
        body.apply_impulse(impulse, true);
        body.apply_torque_impulse(torque_impulse, true);
        Ok(())
    }

    /// Continuous force and torque for the next step (cleared by Rapier after it is applied).
    pub fn physics_add_force(&mut self, node: NodeId, force: Vec3, torque: Vec3) -> Result<()> {
        let body = self.body_mut(node)?;
        body.reset_forces(false);
        body.reset_torques(false);
        body.add_force(force, true);
        body.add_torque(torque, true);
        Ok(())
    }

    pub fn physics_set_velocity(&mut self, node: NodeId, linear: Vec3, angular: Vec3) -> Result<()> {
        let body = self.body_mut(node)?;
        body.set_linvel(linear, true);
        body.set_angvel(angular, true);
        Ok(())
    }

    pub fn physics_velocity(&mut self, node: NodeId) -> Result<(Vec3, Vec3)> {
        let body = self.body_mut(node)?;
        Ok((body.linvel(), body.angvel()))
    }

    /// Moves a body (and its node) instantly, keeping its velocity.
    pub fn physics_teleport(&mut self, node: NodeId, position: Vec3, rotation: Quat) -> Result<()> {
        self.body_mut(node)?.set_position(Pose::from_parts(position, rotation.normalize()), true);
        self.write_back_dynamic_bodies();
        let parent_world = self
            .node(node)
            .and_then(|n| n.parent())
            .and_then(|p| self.node(p))
            .map(|p| p.world_matrix())
            .unwrap_or(Mat4::IDENTITY);
        let (_, local_rotation, local_translation) =
            (parent_world.inverse() * Mat4::from_rotation_translation(rotation, position)).to_scale_rotation_translation();
        if let Some(n) = self.node_mut(node) {
            n.transform.translation = local_translation;
            n.transform.rotation = local_rotation;
        }
        self.mark_dirty(node);
        Ok(())
    }

    pub fn physics_is_sleeping(&mut self, node: NodeId) -> Result<bool> {
        Ok(self.body_mut(node)?.is_sleeping())
    }

    /// Closest collider hit along a ray, excluding sensors and optionally one node's body.
    pub fn physics_raycast(&self, origin: Vec3, direction: Vec3, max_distance: f32, exclude: Option<NodeId>) -> Option<PhysicsHit> {
        let direction = direction.normalize_or_zero();
        if direction == Vec3::ZERO {
            return None;
        }
        let ray = Ray::new(origin, direction);
        let mut filter = QueryFilter::default().exclude_sensors();
        if let Some(body) = exclude.and_then(|n| self.physics.bodies.get(&n)) {
            filter = filter.exclude_rigid_body(*body);
        }
        let limit = if max_distance > 0.0 { max_distance } else { f32::MAX };
        let (collider, hit) = self.physics.world.cast_ray_and_get_normal(&ray, limit, true, filter)?;
        Some(PhysicsHit {
            node: self.physics.node_of_collider(collider)?,
            distance: hit.time_of_impact,
            point: origin + direction * hit.time_of_impact,
            normal: hit.normal,
        })
    }

    /// Adds a joint between two nodes' bodies. Anchors are in each node's local frame.
    pub fn physics_add_joint(
        &mut self,
        kind: JointKind,
        node_a: NodeId,
        node_b: NodeId,
        anchor_a: Vec3,
        anchor_b: Vec3,
        axis: Vec3,
    ) -> Result<u32> {
        let a = *self.physics.bodies.get(&node_a).ok_or(Error::InvalidHandle("rigid body A"))?;
        let b = *self.physics.bodies.get(&node_b).ok_or(Error::InvalidHandle("rigid body B"))?;
        let axis = axis.try_normalize().unwrap_or(Vec3::Y);
        let joint: GenericJoint = match kind {
            JointKind::Fixed => FixedJointBuilder::new().local_anchor1(anchor_a).local_anchor2(anchor_b).build().into(),
            JointKind::Ball => SphericalJointBuilder::new().local_anchor1(anchor_a).local_anchor2(anchor_b).build().into(),
            JointKind::Hinge => RevoluteJointBuilder::new(axis).local_anchor1(anchor_a).local_anchor2(anchor_b).build().into(),
            JointKind::Slider => PrismaticJointBuilder::new(axis).local_anchor1(anchor_a).local_anchor2(anchor_b).build().into(),
        };
        let handle = self.physics.world.insert_impulse_joint(a, b, joint);
        self.physics.joints.push(Some(handle));
        Ok(self.physics.joints.len() as u32)
    }

    pub fn physics_remove_joint(&mut self, id: u32) -> bool {
        let Some(slot) = id.checked_sub(1).and_then(|i| self.physics.joints.get_mut(i as usize)) else {
            return false;
        };
        match slot.take() {
            Some(handle) => self.physics.world.remove_impulse_joint(handle).is_some(),
            None => false,
        }
    }

    /// Moves a kinematic character (a node with a kinematic body and one
    /// collider) by `desired`, sliding along walls, climbing slopes up to 45°
    /// and snapping to the ground. Returns the applied translation and whether
    /// the character stands on the ground. The node is moved immediately.
    pub fn physics_move_character(&mut self, node: NodeId, desired: Vec3, delta: f32) -> Result<(Vec3, bool)> {
        let handle = *self.physics.bodies.get(&node).ok_or(Error::InvalidHandle("rigid body"))?;
        let state = &self.physics;
        let body = state.world.bodies.get(handle).ok_or(Error::InvalidHandle("rigid body"))?;
        if !body.is_kinematic() {
            return Err(Error::InvalidArgument("character bodies must be kinematic".into()));
        }
        let collider_handle = *body.colliders().first().ok_or_else(|| Error::InvalidArgument("the character has no collider".into()))?;
        let collider = state.world.colliders.get(collider_handle).ok_or(Error::InvalidHandle("collider"))?;
        let filter = QueryFilter::default().exclude_rigid_body(handle).exclude_sensors();
        let queries = state.world.query_pipeline_with_filter(filter);
        let movement = state.controller.move_shape(
            delta.max(1e-4),
            &queries,
            collider.shape(),
            collider.position(),
            desired,
            |_| {},
        );
        let translation = body.translation() + movement.translation;
        let rotation = *body.rotation();

        if let Some(body) = self.physics.world.bodies.get_mut(handle) {
            body.set_next_kinematic_position(Pose::from_parts(translation, rotation));
            // Keep collider positions current for queries before the next step.
            body.set_position(Pose::from_parts(translation, rotation), false);
        }
        let parent_world = self
            .node(node)
            .and_then(|n| n.parent())
            .and_then(|p| self.node(p))
            .map(|p| p.world_matrix())
            .unwrap_or(Mat4::IDENTITY);
        let local = parent_world.inverse().transform_point3(translation);
        if let Some(n) = self.node_mut(node) {
            n.transform.translation = local;
        }
        self.mark_dirty(node);
        Ok((movement.translation, movement.grounded))
    }

    /// Configures the character controller shared by all characters.
    pub fn physics_configure_character(&mut self, max_slope_degrees: f32, step_height: f32, snap_to_ground: f32) {
        use rapier3d::control::{CharacterAutostep, CharacterLength};
        let controller = &mut self.physics.controller;
        controller.max_slope_climb_angle = max_slope_degrees.to_radians();
        controller.min_slope_slide_angle = max_slope_degrees.to_radians();
        controller.autostep = (step_height > 0.0).then_some(CharacterAutostep {
            max_height: CharacterLength::Absolute(step_height),
            min_width: CharacterLength::Absolute(0.1),
            include_dynamic_bodies: false,
        });
        controller.snap_to_ground = (snap_to_ground > 0.0).then_some(CharacterLength::Absolute(snap_to_ground));
    }
}
