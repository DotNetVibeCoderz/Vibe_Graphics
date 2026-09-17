//! Gamepads through gilrs (XInput / Windows.Gaming.Input, evdev, IOKit).
//!
//! [`Gamepads`] is polled once per frame. Every controller gets a stable slot
//! for as long as the process runs; the state keeps the previous frame's
//! buttons so callers can detect presses and releases. Virtual pads (tests,
//! on-screen controls, network replay) can be injected into free slots.

use gilrs::ff::{BaseEffect, BaseEffectType, EffectBuilder, Replay, Ticks};
use gilrs::{Axis, Button, EventType, Gilrs};

/// Button bits of [`GamepadState::buttons`], matching `GamepadButton` in .NET.
pub mod buttons {
    pub const SOUTH: u32 = 1 << 0;
    pub const EAST: u32 = 1 << 1;
    pub const WEST: u32 = 1 << 2;
    pub const NORTH: u32 = 1 << 3;
    pub const LEFT_BUMPER: u32 = 1 << 4;
    pub const RIGHT_BUMPER: u32 = 1 << 5;
    pub const SELECT: u32 = 1 << 6;
    pub const START: u32 = 1 << 7;
    pub const GUIDE: u32 = 1 << 8;
    pub const LEFT_STICK: u32 = 1 << 9;
    pub const RIGHT_STICK: u32 = 1 << 10;
    pub const DPAD_UP: u32 = 1 << 11;
    pub const DPAD_DOWN: u32 = 1 << 12;
    pub const DPAD_LEFT: u32 = 1 << 13;
    pub const DPAD_RIGHT: u32 = 1 << 14;
    pub const LEFT_TRIGGER: u32 = 1 << 15;
    pub const RIGHT_TRIGGER: u32 = 1 << 16;
}

const BUTTON_MAP: [(Button, u32); 15] = [
    (Button::South, buttons::SOUTH),
    (Button::East, buttons::EAST),
    (Button::West, buttons::WEST),
    (Button::North, buttons::NORTH),
    (Button::LeftTrigger, buttons::LEFT_BUMPER),
    (Button::RightTrigger, buttons::RIGHT_BUMPER),
    (Button::Select, buttons::SELECT),
    (Button::Start, buttons::START),
    (Button::Mode, buttons::GUIDE),
    (Button::LeftThumb, buttons::LEFT_STICK),
    (Button::RightThumb, buttons::RIGHT_STICK),
    (Button::DPadUp, buttons::DPAD_UP),
    (Button::DPadDown, buttons::DPAD_DOWN),
    (Button::DPadLeft, buttons::DPAD_LEFT),
    (Button::DPadRight, buttons::DPAD_RIGHT),
];

/// Snapshot of one controller.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct GamepadState {
    pub connected: bool,
    pub is_virtual: bool,
    pub name: String,
    /// Buttons held this frame.
    pub buttons: u32,
    /// Buttons held the previous frame.
    pub previous_buttons: u32,
    /// Left stick x/y, right stick x/y (y up), left and right trigger 0..1.
    pub axes: [f32; 6],
}

pub struct Gamepads {
    gilrs: Option<Gilrs>,
    slots: Vec<GamepadState>,
    /// Slot owned by each physical controller.
    physical: Vec<(gilrs::GamepadId, usize)>,
    pub dead_zone: f32,
    /// Trigger value above which the digital trigger bit is set.
    pub trigger_threshold: f32,
    pub init_error: Option<String>,
}

impl std::fmt::Debug for Gamepads {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Gamepads").field("slots", &self.slots).finish()
    }
}

impl Default for Gamepads {
    fn default() -> Self {
        Self::new()
    }
}

impl Gamepads {
    /// Opens the platform backend. Failing to open it (no udev, sandbox) is
    /// not an error: the object works with virtual pads only.
    pub fn new() -> Self {
        let (gilrs, init_error) = match Gilrs::new() {
            Ok(gilrs) => (Some(gilrs), None),
            Err(gilrs::Error::NotImplemented(gilrs)) => (Some(gilrs), Some("gamepads are not supported on this platform".into())),
            Err(error) => (None, Some(error.to_string())),
        };
        let mut pads = Self {
            gilrs,
            slots: Vec::new(),
            physical: Vec::new(),
            dead_zone: 0.12,
            trigger_threshold: 0.35,
            init_error,
        };
        // Controllers connected before start-up do not send a Connected event.
        let ids: Vec<_> = pads
            .gilrs
            .as_ref()
            .map(|g| g.gamepads().map(|(id, _)| id).collect())
            .unwrap_or_default();
        for id in ids {
            pads.attach(id);
        }
        pads
    }

    fn attach(&mut self, id: gilrs::GamepadId) -> usize {
        if let Some(&(_, slot)) = self.physical.iter().find(|(known, _)| *known == id) {
            self.slots[slot].connected = true;
            return slot;
        }
        let slot = self
            .slots
            .iter()
            .position(|s| !s.connected && !s.is_virtual)
            .unwrap_or_else(|| {
                self.slots.push(GamepadState::default());
                self.slots.len() - 1
            });
        self.physical.push((id, slot));
        let name = self.gilrs.as_ref().map(|g| g.gamepad(id).name().to_string()).unwrap_or_default();
        self.slots[slot] = GamepadState {
            connected: true,
            name,
            ..Default::default()
        };
        slot
    }

    /// Drains platform events and refreshes every physical slot. Returns the
    /// number of connected controllers (virtual ones included).
    pub fn update(&mut self) -> usize {
        for slot in &mut self.slots {
            slot.previous_buttons = slot.buttons;
        }

        let mut events = Vec::new();
        if let Some(gilrs) = &mut self.gilrs {
            while let Some(event) = gilrs.next_event() {
                events.push((event.id, event.event));
            }
        }
        for (id, event) in events {
            match event {
                EventType::Connected => {
                    self.attach(id);
                }
                EventType::Disconnected => {
                    if let Some(&(_, slot)) = self.physical.iter().find(|(known, _)| *known == id) {
                        let state = &mut self.slots[slot];
                        state.connected = false;
                        state.buttons = 0;
                        state.axes = [0.0; 6];
                    }
                }
                _ => {}
            }
        }

        if let Some(gilrs) = &self.gilrs {
            for &(id, slot) in &self.physical {
                let Some(pad) = gilrs.connected_gamepad(id) else {
                    continue;
                };
                let state = &mut self.slots[slot];
                let mut held = 0;
                for (button, bit) in BUTTON_MAP {
                    if pad.is_pressed(button) {
                        held |= bit;
                    }
                }
                let trigger = |button| pad.button_data(button).map_or(0.0, |d| d.value());
                let axis = |a| pad.value(a);
                let (lx, ly) = radial_dead_zone(axis(Axis::LeftStickX), axis(Axis::LeftStickY), self.dead_zone);
                let (rx, ry) = radial_dead_zone(axis(Axis::RightStickX), axis(Axis::RightStickY), self.dead_zone);
                let lt = trigger(Button::LeftTrigger2);
                let rt = trigger(Button::RightTrigger2);
                if lt > self.trigger_threshold {
                    held |= buttons::LEFT_TRIGGER;
                }
                if rt > self.trigger_threshold {
                    held |= buttons::RIGHT_TRIGGER;
                }
                state.buttons = held;
                state.axes = [lx, ly, rx, ry, lt, rt];
            }
        }

        self.slots.iter().filter(|s| s.connected).count()
    }

    pub fn slot_count(&self) -> usize {
        self.slots.len()
    }

    pub fn state(&self, slot: usize) -> Option<&GamepadState> {
        self.slots.get(slot)
    }

    /// Creates or overwrites a virtual pad. `slot` may be one past the last
    /// slot to append; slots owned by physical controllers are rejected.
    pub fn set_virtual(&mut self, slot: usize, buttons: u32, axes: [f32; 6], connected: bool, name: &str) -> bool {
        if slot > self.slots.len() || self.physical.iter().any(|(_, owned)| *owned == slot) {
            return false;
        }
        if slot == self.slots.len() {
            self.slots.push(GamepadState::default());
        }
        let state = &mut self.slots[slot];
        state.is_virtual = true;
        state.connected = connected;
        state.name = name.to_string();
        state.buttons = if connected { buttons } else { 0 };
        state.axes = if connected { axes } else { [0.0; 6] };
        true
    }

    /// Plays a rumble effect: `strong` drives the low frequency motor, `weak`
    /// the high frequency one (0..1). Returns false when unsupported.
    pub fn rumble(&mut self, slot: usize, strong: f32, weak: f32, duration_ms: u32) -> bool {
        let Some(gilrs) = &mut self.gilrs else { return false };
        let Some(&(id, _)) = self.physical.iter().find(|(_, owned)| *owned == slot) else {
            return false;
        };
        if !gilrs.connected_gamepad(id).is_some_and(|pad| pad.is_ff_supported()) {
            return false;
        }
        let duration = Ticks::from_ms(duration_ms.max(1));
        let magnitude = |value: f32| (value.clamp(0.0, 1.0) * u16::MAX as f32) as u16;
        let effect = EffectBuilder::new()
            .add_effect(BaseEffect {
                kind: BaseEffectType::Strong { magnitude: magnitude(strong) },
                scheduling: Replay { play_for: duration, ..Default::default() },
                envelope: Default::default(),
            })
            .add_effect(BaseEffect {
                kind: BaseEffectType::Weak { magnitude: magnitude(weak) },
                scheduling: Replay { play_for: duration, ..Default::default() },
                envelope: Default::default(),
            })
            .gamepads(&[id])
            .finish(gilrs);
        match effect {
            Ok(effect) => effect.play().is_ok(),
            Err(_) => false,
        }
    }
}

/// Rescales stick input so the dead zone maps to 0 and the rim stays at 1.
pub fn radial_dead_zone(x: f32, y: f32, dead_zone: f32) -> (f32, f32) {
    let length = (x * x + y * y).sqrt();
    if length <= dead_zone || length == 0.0 {
        return (0.0, 0.0);
    }
    let scaled = ((length - dead_zone) / (1.0 - dead_zone).max(1e-3)).min(1.0);
    (x / length * scaled, y / length * scaled)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dead_zone_rescales_magnitude() {
        assert_eq!(radial_dead_zone(0.05, 0.05, 0.12), (0.0, 0.0));
        let (x, y) = radial_dead_zone(1.0, 0.0, 0.2);
        assert!((x - 1.0).abs() < 1e-5 && y == 0.0);
        let (x, _) = radial_dead_zone(0.6, 0.0, 0.2);
        assert!((x - 0.5).abs() < 1e-5);
    }

    #[test]
    fn virtual_pads_track_previous_buttons() {
        let mut pads = Gamepads::new();
        let slot = pads.slot_count();
        assert!(pads.set_virtual(slot, buttons::SOUTH, [0.5, 0.0, 0.0, 0.0, 1.0, 0.0], true, "virtual"));
        pads.update();
        pads.set_virtual(slot, buttons::SOUTH | buttons::START, [0.0; 6], true, "virtual");
        let state = pads.state(slot).unwrap();
        assert_eq!(state.previous_buttons, buttons::SOUTH);
        assert_eq!(state.buttons, buttons::SOUTH | buttons::START);
        assert!(!pads.set_virtual(slot + 5, 0, [0.0; 6], true, "gap"));
    }
}
