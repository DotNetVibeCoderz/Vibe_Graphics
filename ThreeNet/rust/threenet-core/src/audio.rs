//! Spatial audio: decoded clips, a software mixer with 3D panning, distance
//! attenuation, Doppler and air absorption, and a cpal output stream.
//!
//! The mixer can also render offline (no device), which the tests and
//! `tn_audio_render` use. Sources can follow scene nodes; the listener usually
//! follows the camera ([`AudioEngine::sync_scene`]).

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use crate::error::{Error, Result};
use crate::math::Vec3;
use crate::scene::{Arena, NodeId, Scene};

pub type ClipId = u32;
pub type SourceId = u32;

/// Decoded audio kept in memory as interleaved f32.
#[derive(Debug, Clone)]
pub struct AudioClip {
    pub samples: Arc<Vec<f32>>,
    pub channels: u16,
    pub sample_rate: u32,
}

impl AudioClip {
    pub fn frames(&self) -> usize {
        self.samples.len() / self.channels.max(1) as usize
    }

    pub fn duration(&self) -> f32 {
        self.frames() as f32 / self.sample_rate.max(1) as f32
    }

    /// Wraps raw interleaved samples (procedural sounds, synthesis).
    pub fn from_samples(samples: Vec<f32>, channels: u16, sample_rate: u32) -> Result<Self> {
        if !(1..=2).contains(&channels) || sample_rate == 0 || samples.len() % channels as usize != 0 {
            return Err(Error::InvalidArgument("clips need 1 or 2 channels, a sample rate and whole frames".into()));
        }
        Ok(Self { samples: Arc::new(samples), channels, sample_rate })
    }

    /// Decodes WAV, OGG Vorbis, MP3 or FLAC. More than two channels are mixed down to stereo.
    pub fn decode(bytes: Vec<u8>) -> Result<Self> {
        use symphonia::core::codecs::audio::AudioDecoderOptions;
        use symphonia::core::formats::probe::Hint;
        use symphonia::core::formats::{FormatOptions, TrackType};
        use symphonia::core::io::MediaSourceStream;
        use symphonia::core::meta::MetadataOptions;

        let fail = |message: String| Error::Asset(format!("cannot decode audio: {message}"));
        let stream = MediaSourceStream::new(Box::new(std::io::Cursor::new(bytes)), Default::default());
        let mut format = symphonia::default::get_probe()
            .probe(&Hint::new(), stream, FormatOptions::default(), MetadataOptions::default())
            .map_err(|e| fail(e.to_string()))?;
        let track = format.default_track(TrackType::Audio).ok_or_else(|| fail("no audio track".into()))?;
        let params = track
            .codec_params
            .as_ref()
            .and_then(|p| p.audio())
            .ok_or_else(|| fail("not an audio track".into()))?
            .clone();
        let track_id = track.id;
        let mut decoder = symphonia::default::get_codecs()
            .make_audio_decoder(&params, &AudioDecoderOptions::default())
            .map_err(|e| fail(e.to_string()))?;

        let mut interleaved: Vec<f32> = Vec::new();
        let mut channels = 0usize;
        let mut sample_rate = params.sample_rate.unwrap_or(0);
        let mut scratch: Vec<f32> = Vec::new();
        loop {
            let packet = match format.next_packet() {
                Ok(Some(packet)) => packet,
                Ok(None) => break,
                Err(error) => return Err(fail(error.to_string())),
            };
            if packet.track_id != track_id {
                continue;
            }
            let buffer = match decoder.decode(&packet) {
                Ok(buffer) => buffer,
                Err(symphonia::core::errors::Error::DecodeError(_)) => continue,
                Err(error) => return Err(fail(error.to_string())),
            };
            let spec = buffer.spec();
            let buffer_channels = spec.channels().count().max(1);
            channels = buffer_channels;
            sample_rate = spec.rate();
            scratch.resize(buffer.samples_interleaved(), 0.0);
            buffer.copy_to_slice_interleaved(&mut scratch);
            interleaved.extend_from_slice(&scratch);
        }
        if channels == 0 || sample_rate == 0 {
            return Err(fail("no audio frames".into()));
        }
        if channels > 2 {
            // Fold everything into stereo: even channels left, odd channels right.
            let frames = interleaved.len() / channels;
            let mut stereo = Vec::with_capacity(frames * 2);
            for frame in interleaved.chunks_exact(channels) {
                let (mut left, mut right) = (0.0, 0.0);
                for (index, sample) in frame.iter().enumerate() {
                    if index % 2 == 0 { left += sample } else { right += sample }
                }
                let scale = 2.0 / channels as f32;
                stereo.push(left * scale);
                stereo.push(right * scale);
            }
            interleaved = stereo;
            channels = 2;
        }
        Ok(Self { samples: Arc::new(interleaved), channels: channels as u16, sample_rate })
    }
}

/// Playback settings of a source.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SourceDesc {
    pub clip: ClipId,
    pub gain: f32,
    /// Playback rate multiplier (1 = original pitch).
    pub pitch: f32,
    pub looping: bool,
    /// Spatial sources are panned and attenuated relative to the listener.
    pub spatial: bool,
    pub position: Vec3,
    pub velocity: Vec3,
    /// Full volume inside this distance.
    pub min_distance: f32,
    /// Attenuation stops changing beyond this distance.
    pub max_distance: f32,
    /// Inverse distance rolloff factor (1 = physically based).
    pub rolloff: f32,
    /// Follows this node's world position (and derives velocity from its motion).
    pub node: Option<NodeId>,
    pub paused: bool,
}

impl SourceDesc {
    pub fn new(clip: ClipId) -> Self {
        Self {
            clip,
            gain: 1.0,
            pitch: 1.0,
            looping: false,
            spatial: false,
            position: Vec3::ZERO,
            velocity: Vec3::ZERO,
            min_distance: 1.0,
            max_distance: 100.0,
            rolloff: 1.0,
            node: None,
            paused: false,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Listener {
    pub position: Vec3,
    pub forward: Vec3,
    pub up: Vec3,
    pub velocity: Vec3,
}

impl Default for Listener {
    fn default() -> Self {
        Self { position: Vec3::ZERO, forward: Vec3::NEG_Z, up: Vec3::Y, velocity: Vec3::ZERO }
    }
}

#[derive(Debug)]
struct Source {
    desc: SourceDesc,
    /// Read position in clip frames.
    cursor: f64,
    finished: bool,
    /// Smoothed gains so moving sources do not click.
    gains: [f32; 2],
    lowpass: [f32; 2],
    last_node_position: Option<Vec3>,
    /// Jump to the target gains instead of gliding (first placement).
    snap_gains: bool,
}

/// Gains, pitch factor and low-pass coefficient for a spatial source.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SpatialParams {
    pub left: f32,
    pub right: f32,
    pub doppler: f32,
    pub lowpass: f32,
}

#[derive(Debug)]
pub struct Mixer {
    pub sample_rate: u32,
    clips: Arena<AudioClip>,
    sources: Arena<Source>,
    pub listener: Listener,
    pub master_gain: f32,
    /// 0 disables the Doppler effect.
    pub doppler_factor: f32,
    pub speed_of_sound: f32,
}

impl Mixer {
    pub fn new(sample_rate: u32) -> Self {
        Self {
            sample_rate: sample_rate.max(1),
            clips: Arena::default(),
            sources: Arena::default(),
            listener: Listener::default(),
            master_gain: 1.0,
            doppler_factor: 1.0,
            speed_of_sound: 343.0,
        }
    }

    pub fn add_clip(&mut self, clip: AudioClip) -> ClipId {
        self.clips.insert(clip)
    }

    pub fn clip(&self, id: ClipId) -> Option<&AudioClip> {
        self.clips.get(id)
    }

    /// Removes a clip; sources still playing it stop.
    pub fn remove_clip(&mut self, id: ClipId) -> bool {
        let removed = self.clips.remove(id).is_some();
        if removed {
            for (_, source) in self.sources.iter_mut() {
                if source.desc.clip == id {
                    source.finished = true;
                }
            }
        }
        removed
    }

    pub fn play(&mut self, desc: SourceDesc) -> Result<SourceId> {
        if !self.clips.contains(desc.clip) {
            return Err(Error::InvalidHandle("audio clip"));
        }
        let initial = self.spatial_params(&desc);
        Ok(self.sources.insert(Source {
            desc,
            cursor: 0.0,
            finished: false,
            gains: [initial.left, initial.right],
            lowpass: [0.0; 2],
            last_node_position: None,
            snap_gains: desc.node.is_some(),
        }))
    }

    pub fn update_source(&mut self, id: SourceId, desc: SourceDesc) -> Result<()> {
        if !self.clips.contains(desc.clip) {
            return Err(Error::InvalidHandle("audio clip"));
        }
        let source = self.sources.get_mut(id).ok_or(Error::InvalidHandle("audio source"))?;
        if source.desc.clip != desc.clip {
            source.cursor = 0.0;
            source.finished = false;
        }
        source.desc = desc;
        Ok(())
    }

    pub fn source(&self, id: SourceId) -> Option<SourceDesc> {
        self.sources.get(id).map(|s| s.desc)
    }

    /// True while the source exists and has not reached the end of a one-shot clip.
    pub fn is_playing(&self, id: SourceId) -> bool {
        self.sources.get(id).is_some_and(|s| !s.finished)
    }

    /// Playback position in seconds.
    pub fn position_seconds(&self, id: SourceId) -> Option<f32> {
        let source = self.sources.get(id)?;
        let clip = self.clips.get(source.desc.clip)?;
        Some(source.cursor as f32 / clip.sample_rate as f32)
    }

    pub fn seek(&mut self, id: SourceId, seconds: f32) -> bool {
        let Some(source) = self.sources.get_mut(id) else { return false };
        let Some(clip) = self.clips.get(source.desc.clip) else { return false };
        source.cursor = (seconds.max(0.0) * clip.sample_rate as f32) as f64;
        source.finished = false;
        true
    }

    pub fn stop(&mut self, id: SourceId) -> bool {
        self.sources.remove(id).is_some()
    }

    pub fn source_count(&self) -> usize {
        self.sources.len()
    }

    /// Panning, attenuation, Doppler and absorption for a source against the listener.
    pub fn spatial_params(&self, desc: &SourceDesc) -> SpatialParams {
        let gain = desc.gain.max(0.0) * self.master_gain.max(0.0);
        if !desc.spatial {
            return SpatialParams { left: gain, right: gain, doppler: 1.0, lowpass: 1.0 };
        }
        let listener = &self.listener;
        let offset = desc.position - listener.position;
        let distance = offset.length();
        let min = desc.min_distance.max(0.01);
        let max = desc.max_distance.max(min);
        let clamped = distance.clamp(min, max);
        let attenuation = min / (min + desc.rolloff.max(0.0) * (clamped - min));

        let forward = listener.forward.try_normalize().unwrap_or(Vec3::NEG_Z);
        let up = listener.up.try_normalize().unwrap_or(Vec3::Y);
        let right = forward.cross(up).try_normalize().unwrap_or(Vec3::X);
        let direction = if distance > 1e-4 { offset / distance } else { forward };
        // Sources close to the listener pan less (they surround the head).
        let pan = direction.dot(right).clamp(-1.0, 1.0) * (distance / min).min(1.0);
        let angle = (pan + 1.0) * std::f32::consts::FRAC_PI_4;
        // Equal power panning, normalised so a centred source keeps unit gain per channel.
        let (left, right_gain) = (angle.cos() * std::f32::consts::SQRT_2, angle.sin() * std::f32::consts::SQRT_2);
        // Sounds behind the listener are slightly quieter.
        let behind = if direction.dot(forward) < 0.0 { 1.0 + 0.2 * direction.dot(forward) } else { 1.0 };

        let mut doppler = 1.0;
        if self.doppler_factor > 0.0 && distance > 1e-4 {
            let c = self.speed_of_sound.max(1.0);
            let listener_speed = listener.velocity.dot(direction) * self.doppler_factor;
            let source_speed = desc.velocity.dot(-direction) * self.doppler_factor;
            doppler = ((c + listener_speed) / (c - source_speed).max(1.0)).clamp(0.5, 2.0);
        }

        // Air absorption: gentle low-pass that closes with distance.
        let lowpass = (1.0 - distance / (max * 4.0)).clamp(0.15, 1.0);

        let g = gain * attenuation * behind;
        SpatialParams { left: g * left, right: g * right_gain, doppler, lowpass }
    }

    /// Mixes into interleaved `output` (`channels` = 1 or 2); returns frames written.
    pub fn render(&mut self, output: &mut [f32], channels: usize) -> usize {
        output.iter_mut().for_each(|s| *s = 0.0);
        let channels = channels.max(1);
        let frames = output.len() / channels;
        if frames == 0 {
            return 0;
        }

        let ids: Vec<SourceId> = self.sources.iter().map(|(id, _)| id).collect();
        for id in ids {
            let Some(desc) = self.sources.get(id).map(|s| s.desc) else { continue };
            let Some(clip) = self.clips.get(desc.clip).cloned() else { continue };
            let params = self.spatial_params(&desc);
            let sample_rate = self.sample_rate;
            let Some(source) = self.sources.get_mut(id) else { continue };
            if source.finished || desc.paused {
                continue;
            }

            let step = clip.sample_rate as f64 / sample_rate as f64 * (desc.pitch.max(0.01) * params.doppler) as f64;
            let clip_frames = clip.frames();
            if clip_frames == 0 {
                source.finished = true;
                continue;
            }
            let smoothing = 1.0 - (-1.0 / (0.01 * sample_rate as f32)).exp(); // ~10 ms
            if std::mem::take(&mut source.snap_gains) {
                source.gains = [params.left, params.right];
            }
            let alpha = params.lowpass;
            for frame in 0..frames {
                if source.cursor >= clip_frames as f64 {
                    if desc.looping {
                        source.cursor %= clip_frames as f64;
                    } else {
                        source.finished = true;
                        break;
                    }
                }
                let index = source.cursor as usize;
                let fraction = (source.cursor - index as f64) as f32;
                let next = if index + 1 < clip_frames { index + 1 } else if desc.looping { 0 } else { index };
                let read = |i: usize, c: usize| clip.samples[i * clip.channels as usize + c.min(clip.channels as usize - 1)];
                let mut sample = [0.0f32; 2];
                for (c, value) in sample.iter_mut().enumerate() {
                    *value = read(index, c) + (read(next, c) - read(index, c)) * fraction;
                }
                if desc.spatial && clip.channels == 2 {
                    // Spatial sources are mono point emitters.
                    let mono = (sample[0] + sample[1]) * 0.5;
                    sample = [mono, mono];
                }
                for c in 0..2 {
                    source.lowpass[c] += alpha * (sample[c] - source.lowpass[c]);
                    sample[c] = source.lowpass[c];
                }
                source.gains[0] += (params.left - source.gains[0]) * smoothing;
                source.gains[1] += (params.right - source.gains[1]) * smoothing;

                let base = frame * channels;
                if channels == 1 {
                    output[base] += (sample[0] * source.gains[0] + sample[1] * source.gains[1]) * 0.5;
                } else {
                    output[base] += sample[0] * source.gains[0];
                    output[base + 1] += sample[1] * source.gains[1];
                }
                source.cursor += step;
                if !desc.looping && source.cursor >= clip_frames as f64 {
                    source.finished = true;
                    break;
                }
            }
        }

        // Finished one-shots are released automatically.
        let finished: Vec<SourceId> = self.sources.iter().filter(|(_, s)| s.finished && !s.desc.looping).map(|(id, _)| id).collect();
        for id in finished {
            self.sources.remove(id);
        }
        // Soft clip to keep overlapping sounds from wrapping.
        for sample in output.iter_mut() {
            *sample = sample.clamp(-1.5, 1.5);
            if sample.abs() > 1.0 {
                *sample = sample.signum() * (1.0 - (-(sample.abs() - 1.0) * 2.0).exp() * 0.5).min(1.0);
            }
        }
        frames
    }
}

/// Mixer plus (optionally) a live output stream.
pub struct AudioEngine {
    pub mixer: Arc<Mutex<Mixer>>,
    stream: Option<cpal::Stream>,
    pub device_name: String,
    pub channels: u16,
    listener_node_last: Option<(NodeId, Vec3)>,
}

impl std::fmt::Debug for AudioEngine {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AudioEngine").field("device", &self.device_name).finish()
    }
}

impl AudioEngine {
    /// Opens the default output device.
    pub fn new() -> Result<Self> {
        use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
        let host = cpal::default_host();
        let device = host.default_output_device().ok_or_else(|| Error::Asset("no audio output device".into()))?;
        let supported = device.default_output_config().map_err(|e| Error::Asset(format!("audio config: {e}")))?;
        let sample_rate = supported.sample_rate();
        let channels = supported.channels();
        let config: cpal::StreamConfig = supported.config();
        let mixer = Arc::new(Mutex::new(Mixer::new(sample_rate)));
        let shared = Arc::clone(&mixer);
        let output_channels = channels as usize;
        let mut stereo: Vec<f32> = Vec::new();
        let stream = device
            .build_output_stream::<f32, _, _>(
                config,
                move |data: &mut [f32], _| {
                    let frames = data.len() / output_channels.max(1);
                    stereo.resize(frames * 2, 0.0);
                    match shared.lock() {
                        Ok(mut mixer) => {
                            mixer.render(&mut stereo, 2);
                        }
                        Err(_) => stereo.iter_mut().for_each(|s| *s = 0.0),
                    }
                    // Map stereo onto the device layout (extra channels stay silent).
                    for (frame, out) in data.chunks_exact_mut(output_channels.max(1)).enumerate() {
                        match out.len() {
                            1 => out[0] = (stereo[frame * 2] + stereo[frame * 2 + 1]) * 0.5,
                            _ => {
                                out.iter_mut().for_each(|s| *s = 0.0);
                                out[0] = stereo[frame * 2];
                                out[1] = stereo[frame * 2 + 1];
                            }
                        }
                    }
                },
                |error| log::warn!("audio stream error: {error}"),
                None,
            )
            .map_err(|e| Error::Asset(format!("cannot open the audio stream: {e}")))?;
        stream.play().map_err(|e| Error::Asset(format!("cannot start audio: {e}")))?;
        let device_name = device.description().map(|d| d.to_string()).unwrap_or_else(|_| "default".into());
        Ok(Self { mixer, stream: Some(stream), device_name, channels, listener_node_last: None })
    }

    /// A mixer without a device, rendered manually (tests, offline bouncing).
    pub fn offline(sample_rate: u32) -> Self {
        Self {
            mixer: Arc::new(Mutex::new(Mixer::new(sample_rate))),
            stream: None,
            device_name: String::from("offline"),
            channels: 2,
            listener_node_last: None,
        }
    }

    pub fn is_offline(&self) -> bool {
        self.stream.is_none()
    }

    pub fn with_mixer<R>(&self, f: impl FnOnce(&mut Mixer) -> R) -> R {
        let mut guard = self.mixer.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        f(&mut guard)
    }

    /// Moves the listener to `listener_node` (position, -Z forward, +Y up) and
    /// node-attached sources to their nodes, deriving velocities over `delta`.
    pub fn sync_scene(&mut self, scene: &mut Scene, listener_node: Option<NodeId>, delta: f32) {
        scene.update_world_transforms();
        let delta = delta.max(1e-4);
        let mut node_positions: HashMap<NodeId, Vec3> = HashMap::new();
        let listener = listener_node.and_then(|id| scene.node(id).map(|n| (id, n.world_matrix())));
        let last = self.listener_node_last;
        let mut listener_update = None;
        if let Some((id, matrix)) = listener {
            let position = matrix.w_axis.truncate();
            let velocity = match last {
                Some((last_id, last_position)) if last_id == id => (position - last_position) / delta,
                _ => Vec3::ZERO,
            };
            listener_update = Some(Listener {
                position,
                forward: (-matrix.z_axis.truncate()).try_normalize().unwrap_or(Vec3::NEG_Z),
                up: matrix.y_axis.truncate().try_normalize().unwrap_or(Vec3::Y),
                velocity,
            });
            self.listener_node_last = Some((id, position));
        }
        let mut guard = self.mixer.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if let Some(listener) = listener_update {
            guard.listener = listener;
        }
        let ids: Vec<SourceId> = guard.sources.iter().map(|(id, _)| id).collect();
        for id in ids {
            let Some(source) = guard.sources.get_mut(id) else { continue };
            let Some(node) = source.desc.node else { continue };
            let position = *node_positions
                .entry(node)
                .or_insert_with(|| scene.node(node).map(|n| n.world_matrix().w_axis.truncate()).unwrap_or(source.desc.position));
            source.desc.velocity = match source.last_node_position {
                Some(last) => (position - last) / delta,
                None => Vec3::ZERO,
            };
            source.desc.position = position;
            source.last_node_position = Some(position);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tone(mixer: &mut Mixer, seconds: f32) -> ClipId {
        let rate = 48_000;
        let samples = (0..(seconds * rate as f32) as usize)
            .map(|i| (i as f32 / rate as f32 * 440.0 * std::f32::consts::TAU).sin() * 0.5)
            .collect();
        mixer.add_clip(AudioClip::from_samples(samples, 1, rate).unwrap())
    }

    fn energy(buffer: &[f32], channel: usize) -> f32 {
        buffer.chunks_exact(2).map(|f| f[channel] * f[channel]).sum::<f32>()
    }

    #[test]
    fn panning_and_attenuation_follow_the_listener() {
        let mut mixer = Mixer::new(48_000);
        let clip = tone(&mut mixer, 1.0);
        let right = mixer
            .play(SourceDesc { spatial: true, looping: true, position: Vec3::new(5.0, 0.0, 0.0), ..SourceDesc::new(clip) })
            .unwrap();
        let mut buffer = vec![0.0; 9600];
        mixer.render(&mut buffer, 2);
        mixer.render(&mut buffer, 2);
        assert!(energy(&buffer, 1) > energy(&buffer, 0) * 10.0, "a source on the right is louder on the right");

        let near = energy(&buffer, 1);
        let mut far = mixer.source(right).unwrap();
        far.position = Vec3::new(40.0, 0.0, 0.0);
        mixer.update_source(right, far).unwrap();
        mixer.render(&mut buffer, 2);
        mixer.render(&mut buffer, 2);
        assert!(energy(&buffer, 1) < near * 0.1, "distance attenuates");

        // Turning the listener around swaps the sides.
        mixer.listener.forward = Vec3::Z;
        mixer.render(&mut buffer, 2);
        mixer.render(&mut buffer, 2);
        assert!(energy(&buffer, 0) > energy(&buffer, 1) * 10.0);
    }

    #[test]
    fn doppler_raises_pitch_of_approaching_sources() {
        let mixer = Mixer::new(48_000);
        let still = SourceDesc { spatial: true, position: Vec3::new(0.0, 0.0, -50.0), ..SourceDesc::new(1) };
        let approaching = SourceDesc { velocity: Vec3::new(0.0, 0.0, 30.0), ..still };
        let receding = SourceDesc { velocity: Vec3::new(0.0, 0.0, -30.0), ..still };
        assert_eq!(mixer.spatial_params(&still).doppler, 1.0);
        assert!(mixer.spatial_params(&approaching).doppler > 1.05);
        assert!(mixer.spatial_params(&receding).doppler < 0.95);
    }

    #[test]
    fn one_shots_finish_and_loops_continue() {
        let mut mixer = Mixer::new(48_000);
        let clip = tone(&mut mixer, 0.05);
        let shot = mixer.play(SourceDesc::new(clip)).unwrap();
        let looped = mixer.play(SourceDesc { looping: true, ..SourceDesc::new(clip) }).unwrap();
        let mut buffer = vec![0.0; 9600];
        mixer.render(&mut buffer, 2);
        assert!(!mixer.is_playing(shot));
        assert!(mixer.is_playing(looped));
        assert!(buffer[9000].abs() > 0.0 || buffer[9002].abs() > 0.0, "the loop keeps producing sound");
        assert!(mixer.play(SourceDesc::new(99)).is_err());
    }

    #[test]
    fn wav_files_decode() {
        // 16 bit mono WAV with 4 samples.
        let mut wav = Vec::new();
        wav.extend_from_slice(b"RIFF");
        wav.extend_from_slice(&44u32.to_le_bytes());
        wav.extend_from_slice(b"WAVEfmt ");
        wav.extend_from_slice(&16u32.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&8000u32.to_le_bytes());
        wav.extend_from_slice(&16000u32.to_le_bytes());
        wav.extend_from_slice(&2u16.to_le_bytes());
        wav.extend_from_slice(&16u16.to_le_bytes());
        wav.extend_from_slice(b"data");
        wav.extend_from_slice(&8u32.to_le_bytes());
        for value in [0i16, 16384, -16384, 32767] {
            wav.extend_from_slice(&value.to_le_bytes());
        }
        let clip = AudioClip::decode(wav).unwrap();
        assert_eq!((clip.channels, clip.sample_rate, clip.frames()), (1, 8000, 4));
        assert!((clip.samples[1] - 0.5).abs() < 0.01);
        assert!(AudioClip::decode(vec![1, 2, 3, 4]).is_err());
    }
}
