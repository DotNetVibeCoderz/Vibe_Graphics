//! Keyframe and skeletal animation.
//!
//! Clips hold channels that drive a node's translation, rotation or scale with
//! step, linear or cubic spline interpolation (the glTF model, also used by the
//! FBX importer and by clips built through the API). Players advance clips and
//! blend them by weight. Skinned meshes are deformed on the CPU after the pose
//! is applied: every pass (shadows, SSAO, deferred, picking) sees the posed
//! geometry without shader variants, and only the vertex buffer is rewritten.

use crate::geometry::Vertex;
use crate::math::{Mat4, Quat, Vec3};
use crate::scene::{NodeId, Scene};

pub type AnimationId = u32;
pub type SkinId = u32;
pub type PlayerId = u32;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum Interpolation {
    Step = 0,
    Linear = 1,
    /// Values are stored as (in tangent, value, out tangent) triples.
    CubicSpline = 2,
}

impl Interpolation {
    pub fn from_u32(value: u32) -> Self {
        match value {
            0 => Interpolation::Step,
            2 => Interpolation::CubicSpline,
            _ => Interpolation::Linear,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum TargetPath {
    Translation = 0,
    /// Quaternion (x, y, z, w).
    Rotation = 1,
    Scale = 2,
}

impl TargetPath {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => TargetPath::Rotation,
            2 => TargetPath::Scale,
            _ => TargetPath::Translation,
        }
    }

    #[inline]
    pub fn components(self) -> usize {
        if self == TargetPath::Rotation { 4 } else { 3 }
    }
}

/// One animated property of one node.
#[derive(Debug, Clone)]
pub struct Channel {
    pub target: NodeId,
    pub path: TargetPath,
    pub interpolation: Interpolation,
    /// Key times in seconds, ascending.
    pub times: Vec<f32>,
    /// Flattened key values (3 or 4 components, times 3 for cubic splines).
    pub values: Vec<f32>,
}

impl Channel {
    /// Samples the channel at `time` (clamped to the key range).
    pub fn sample(&self, time: f32) -> [f32; 4] {
        let components = self.path.components();
        let stride = if self.interpolation == Interpolation::CubicSpline { components * 3 } else { components };
        let count = self.times.len();
        if count == 0 || self.values.len() < stride {
            return [0.0, 0.0, 0.0, 1.0];
        }

        let value_at = |key: usize, part: usize| -> [f32; 4] {
            let start = key * stride + part * components;
            let mut out = [0.0, 0.0, 0.0, 1.0];
            out[..components].copy_from_slice(&self.values[start..start + components]);
            out
        };
        let middle = if self.interpolation == Interpolation::CubicSpline { 1 } else { 0 };

        if count == 1 || time <= self.times[0] {
            return value_at(0, middle);
        }
        if time >= self.times[count - 1] {
            return value_at(count - 1, middle);
        }

        // Last key whose time is <= `time`.
        let next = self.times.partition_point(|t| *t <= time).min(count - 1);
        let key = next - 1;
        let t0 = self.times[key];
        let t1 = self.times[next];
        let span = (t1 - t0).max(1e-6);
        let u = ((time - t0) / span).clamp(0.0, 1.0);

        match self.interpolation {
            Interpolation::Step => value_at(key, 0),
            Interpolation::Linear => {
                let a = value_at(key, 0);
                let b = value_at(next, 0);
                if self.path == TargetPath::Rotation {
                    Quat::from_array(a).normalize().slerp(Quat::from_array(b).normalize(), u).to_array()
                } else {
                    let mut out = [0.0; 4];
                    for c in 0..4 {
                        out[c] = a[c] + (b[c] - a[c]) * u;
                    }
                    out
                }
            }
            Interpolation::CubicSpline => {
                // Hermite spline with tangents scaled by the key span.
                let p0 = value_at(key, 1);
                let m0 = value_at(key, 2);
                let p1 = value_at(next, 1);
                let m1 = value_at(next, 0);
                let u2 = u * u;
                let u3 = u2 * u;
                let h00 = 2.0 * u3 - 3.0 * u2 + 1.0;
                let h10 = u3 - 2.0 * u2 + u;
                let h01 = -2.0 * u3 + 3.0 * u2;
                let h11 = u3 - u2;
                let mut out = [0.0; 4];
                for c in 0..components {
                    out[c] = h00 * p0[c] + h10 * span * m0[c] + h01 * p1[c] + h11 * span * m1[c];
                }
                if self.path == TargetPath::Rotation {
                    out = Quat::from_array(out).normalize().to_array();
                }
                out
            }
        }
    }
}

#[derive(Debug, Clone, Default)]
pub struct AnimationClip {
    pub name: String,
    pub channels: Vec<Channel>,
}

impl AnimationClip {
    /// Length of the clip: the last key time of any channel.
    pub fn duration(&self) -> f32 {
        self.channels
            .iter()
            .filter_map(|c| c.times.last().copied())
            .fold(0.0, f32::max)
    }
}

/// Joints of a skeleton and their inverse bind matrices.
#[derive(Debug, Clone, Default)]
pub struct Skin {
    pub name: String,
    pub joints: Vec<NodeId>,
    pub inverse_bind_matrices: Vec<Mat4>,
}

/// Per vertex skinning data kept on a geometry, with the bind pose it deforms from.
#[derive(Debug, Clone, Default)]
pub struct SkinWeights {
    pub joints: Vec<[u16; 4]>,
    pub weights: Vec<[f32; 4]>,
    pub bind_pose: Vec<Vertex>,
}

#[derive(Debug, Clone, Copy)]
pub struct AnimationPlayer {
    pub clip: AnimationId,
    pub time: f32,
    pub speed: f32,
    /// 0..1: how strongly the clip overrides the current pose (for cross fades).
    pub weight: f32,
    pub looping: bool,
    pub playing: bool,
}

impl AnimationPlayer {
    pub fn new(clip: AnimationId) -> Self {
        Self {
            clip,
            time: 0.0,
            speed: 1.0,
            weight: 1.0,
            looping: true,
            playing: true,
        }
    }
}

impl Scene {
    /// Advances every playing animation by `delta` seconds, applies the pose
    /// and deforms skinned meshes. Call once per frame before rendering.
    pub fn update_animations(&mut self, delta: f32) {
        // ------------------------------------------------------------ players
        let mut poses: Vec<(NodeId, TargetPath, [f32; 4], f32)> = Vec::new();
        let player_ids: Vec<PlayerId> = self.players.iter().map(|(id, _)| id).collect();
        for id in player_ids {
            let Some(player) = self.players.get(id).copied() else {
                continue;
            };
            let Some(clip) = self.animations.get(player.clip) else {
                continue;
            };
            let duration = clip.duration();
            let mut time = player.time;
            let mut playing = player.playing;
            if playing {
                time += delta * player.speed;
                if player.looping && duration > 0.0 {
                    time = time.rem_euclid(duration);
                } else if time >= duration || time < 0.0 {
                    time = time.clamp(0.0, duration);
                    playing = false;
                }
            }
            if player.weight > 0.0 {
                for channel in &clip.channels {
                    poses.push((channel.target, channel.path, channel.sample(time), player.weight.min(1.0)));
                }
            }
            if let Some(stored) = self.players.get_mut(id) {
                stored.time = time;
                stored.playing = playing;
            }
        }

        let mut touched: Vec<NodeId> = Vec::new();
        for (node_id, path, value, weight) in poses {
            let Some(node) = self.nodes.get_mut(node_id) else {
                continue;
            };
            let transform = &mut node.transform;
            match path {
                TargetPath::Translation => {
                    transform.translation = transform.translation.lerp(Vec3::new(value[0], value[1], value[2]), weight);
                }
                TargetPath::Scale => {
                    transform.scale = transform.scale.lerp(Vec3::new(value[0], value[1], value[2]), weight);
                }
                TargetPath::Rotation => {
                    let target = Quat::from_array(value).normalize();
                    transform.rotation = if weight >= 1.0 { target } else { transform.rotation.slerp(target, weight) };
                }
            }
            touched.push(node_id);
        }
        touched.sort_unstable();
        touched.dedup();
        for node in touched {
            self.mark_dirty(node);
        }
        self.update_world_transforms();

        // ----------------------------------------------------------- skinning
        let skinned: Vec<(NodeId, u32, SkinId)> = self
            .nodes
            .iter()
            .filter_map(|(id, node)| {
                let mesh = node.mesh?;
                Some((id, mesh.geometry, mesh.skin?))
            })
            .collect();

        for (node_id, geometry_id, skin_id) in skinned {
            let Some(skin) = self.skins.get(skin_id) else {
                continue;
            };
            let Some(node) = self.nodes.get(node_id) else {
                continue;
            };
            let inverse_node = node.world.inverse();
            let joint_matrices: Vec<Mat4> = skin
                .joints
                .iter()
                .enumerate()
                .map(|(index, joint)| {
                    let joint_world = self.nodes.get(*joint).map(|n| n.world).unwrap_or(Mat4::IDENTITY);
                    let inverse_bind = skin.inverse_bind_matrices.get(index).copied().unwrap_or(Mat4::IDENTITY);
                    inverse_node * joint_world * inverse_bind
                })
                .collect();

            let Some(geometry) = self.geometries.get_mut(geometry_id) else {
                continue;
            };
            let Some(skin_weights) = geometry.skin.as_ref() else {
                continue;
            };
            let count = skin_weights.bind_pose.len().min(geometry.vertices.len());
            for i in 0..count {
                let bind = skin_weights.bind_pose[i];
                let joints = skin_weights.joints[i];
                let weights = skin_weights.weights[i];
                let mut matrix = Mat4::ZERO;
                let mut total = 0.0f32;
                for k in 0..4 {
                    let w = weights[k];
                    if w > 0.0
                        && let Some(joint) = joint_matrices.get(joints[k] as usize)
                    {
                        matrix += *joint * w;
                        total += w;
                    }
                }
                if total <= 0.0 {
                    continue;
                }
                if (total - 1.0).abs() > 1e-3 {
                    matrix *= 1.0 / total;
                }

                let position = matrix.transform_point3(Vec3::from_array(bind.position));
                let normal = matrix.transform_vector3(Vec3::from_array(bind.normal)).normalize_or(Vec3::Y);
                let tangent = matrix
                    .transform_vector3(Vec3::new(bind.tangent[0], bind.tangent[1], bind.tangent[2]))
                    .normalize_or(Vec3::X);
                let vertex = &mut geometry.vertices[i];
                vertex.position = position.to_array();
                vertex.normal = normal.to_array();
                vertex.tangent = [tangent.x, tangent.y, tangent.z, bind.tangent[3]];
            }
            geometry.compute_bounds();
            geometry.touch();
        }
    }

    pub fn add_animation(&mut self, clip: AnimationClip) -> AnimationId {
        self.animations.insert(clip)
    }

    pub fn animation(&self, id: AnimationId) -> Option<&AnimationClip> {
        self.animations.get(id)
    }

    pub fn animation_mut(&mut self, id: AnimationId) -> Option<&mut AnimationClip> {
        self.animations.get_mut(id)
    }

    pub fn remove_animation(&mut self, id: AnimationId) -> bool {
        self.animations.remove(id).is_some()
    }

    pub fn animation_ids(&self) -> Vec<AnimationId> {
        self.animations.iter().map(|(id, _)| id).collect()
    }

    pub fn add_skin(&mut self, skin: Skin) -> SkinId {
        self.skins.insert(skin)
    }

    pub fn skin(&self, id: SkinId) -> Option<&Skin> {
        self.skins.get(id)
    }

    pub fn play_animation(&mut self, player: AnimationPlayer) -> Option<PlayerId> {
        self.animations.contains(player.clip).then(|| self.players.insert(player))
    }

    pub fn player(&self, id: PlayerId) -> Option<&AnimationPlayer> {
        self.players.get(id)
    }

    pub fn player_mut(&mut self, id: PlayerId) -> Option<&mut AnimationPlayer> {
        self.players.get_mut(id)
    }

    pub fn stop_animation(&mut self, id: PlayerId) -> bool {
        self.players.remove(id).is_some()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn channel(interpolation: Interpolation, path: TargetPath, values: Vec<f32>) -> Channel {
        Channel {
            target: 1,
            path,
            interpolation,
            times: vec![0.0, 1.0],
            values,
        }
    }

    #[test]
    fn linear_and_step_sampling() {
        let linear = channel(Interpolation::Linear, TargetPath::Translation, vec![0.0, 0.0, 0.0, 2.0, 4.0, 6.0]);
        assert_eq!(linear.sample(0.5)[..3], [1.0, 2.0, 3.0]);
        assert_eq!(linear.sample(5.0)[..3], [2.0, 4.0, 6.0]);
        let step = channel(Interpolation::Step, TargetPath::Translation, vec![0.0, 0.0, 0.0, 2.0, 4.0, 6.0]);
        assert_eq!(step.sample(0.99)[..3], [0.0, 0.0, 0.0]);
    }

    #[test]
    fn rotations_slerp() {
        let half_turn = Quat::from_rotation_y(std::f32::consts::FRAC_PI_2);
        let mut values = Quat::IDENTITY.to_array().to_vec();
        values.extend_from_slice(&half_turn.to_array());
        let rotation = channel(Interpolation::Linear, TargetPath::Rotation, values);
        let halfway = Quat::from_array(rotation.sample(0.5));
        assert!(halfway.angle_between(Quat::from_rotation_y(std::f32::consts::FRAC_PI_4)) < 1e-2);
    }

    #[test]
    fn cubic_spline_hits_its_keys() {
        // (in tangent, value, out tangent) per key.
        let values = vec![0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 3.0, 3.0, 3.0, 0.0, 0.0, 0.0];
        let spline = channel(Interpolation::CubicSpline, TargetPath::Scale, values);
        assert_eq!(spline.sample(0.0)[..3], [1.0, 1.0, 1.0]);
        assert_eq!(spline.sample(1.0)[..3], [3.0, 3.0, 3.0]);
        assert!((spline.sample(0.5)[0] - 2.0).abs() < 1e-4);
    }
}
