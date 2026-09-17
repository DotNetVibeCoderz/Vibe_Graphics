//! Optional windowing host built on winit. The managed side uses it for
//! standalone samples; embedded hosts (Avalonia, WPF, WinForms) create the
//! renderer from their own window handle instead.

#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
use std::sync::Arc;
#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
use std::time::Instant;

#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
use winit::application::ApplicationHandler;
#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
use winit::event::{ElementState, MouseScrollDelta, WindowEvent};
#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
use winit::keyboard::{KeyCode, PhysicalKey};
#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
use winit::window::{Window, WindowId};

use crate::error::{Error, Result};
use crate::renderer::{Renderer, RendererConfig};

/// Kind of input event delivered to the host.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum EventKind {
    Resized = 0,
    KeyDown = 1,
    KeyUp = 2,
    MouseDown = 3,
    MouseUp = 4,
    MouseMove = 5,
    MouseWheel = 6,
    TouchBegin = 7,
    TouchMove = 8,
    TouchEnd = 9,
    Focus = 10,
    CloseRequested = 11,
    ScaleFactorChanged = 12,
}

/// Platform independent key identifiers. The values are mirrored by the
/// `ThreeNet.Input.Key` enum on the managed side, so they must stay stable.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum Key {
    Unknown = 0,
    A = 1, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit0 = 27, Digit1, Digit2, Digit3, Digit4,
    Digit5, Digit6, Digit7, Digit8, Digit9,
    Escape = 37, Enter, Space, Tab, Backspace, Delete, Insert,
    Left = 44, Right, Up, Down,
    Home = 48, End, PageUp, PageDown,
    ShiftLeft = 52, ShiftRight, ControlLeft, ControlRight,
    AltLeft, AltRight, SuperLeft, SuperRight,
    F1 = 60, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Minus = 72, Equal, BracketLeft, BracketRight, Backslash,
    Semicolon, Quote, Comma, Period, Slash, Backquote,
    NumPad0 = 83, NumPad1, NumPad2, NumPad3, NumPad4,
    NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    NumPadAdd = 93, NumPadSubtract, NumPadMultiply, NumPadDivide, NumPadEnter,
}

#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
impl Key {
    pub fn from_physical(key: PhysicalKey) -> Self {
        let PhysicalKey::Code(code) = key else {
            return Key::Unknown;
        };
        match code {
            KeyCode::KeyA => Key::A,
            KeyCode::KeyB => Key::B,
            KeyCode::KeyC => Key::C,
            KeyCode::KeyD => Key::D,
            KeyCode::KeyE => Key::E,
            KeyCode::KeyF => Key::F,
            KeyCode::KeyG => Key::G,
            KeyCode::KeyH => Key::H,
            KeyCode::KeyI => Key::I,
            KeyCode::KeyJ => Key::J,
            KeyCode::KeyK => Key::K,
            KeyCode::KeyL => Key::L,
            KeyCode::KeyM => Key::M,
            KeyCode::KeyN => Key::N,
            KeyCode::KeyO => Key::O,
            KeyCode::KeyP => Key::P,
            KeyCode::KeyQ => Key::Q,
            KeyCode::KeyR => Key::R,
            KeyCode::KeyS => Key::S,
            KeyCode::KeyT => Key::T,
            KeyCode::KeyU => Key::U,
            KeyCode::KeyV => Key::V,
            KeyCode::KeyW => Key::W,
            KeyCode::KeyX => Key::X,
            KeyCode::KeyY => Key::Y,
            KeyCode::KeyZ => Key::Z,
            KeyCode::Digit0 => Key::Digit0,
            KeyCode::Digit1 => Key::Digit1,
            KeyCode::Digit2 => Key::Digit2,
            KeyCode::Digit3 => Key::Digit3,
            KeyCode::Digit4 => Key::Digit4,
            KeyCode::Digit5 => Key::Digit5,
            KeyCode::Digit6 => Key::Digit6,
            KeyCode::Digit7 => Key::Digit7,
            KeyCode::Digit8 => Key::Digit8,
            KeyCode::Digit9 => Key::Digit9,
            KeyCode::Escape => Key::Escape,
            KeyCode::Enter => Key::Enter,
            KeyCode::Space => Key::Space,
            KeyCode::Tab => Key::Tab,
            KeyCode::Backspace => Key::Backspace,
            KeyCode::Delete => Key::Delete,
            KeyCode::Insert => Key::Insert,
            KeyCode::ArrowLeft => Key::Left,
            KeyCode::ArrowRight => Key::Right,
            KeyCode::ArrowUp => Key::Up,
            KeyCode::ArrowDown => Key::Down,
            KeyCode::Home => Key::Home,
            KeyCode::End => Key::End,
            KeyCode::PageUp => Key::PageUp,
            KeyCode::PageDown => Key::PageDown,
            KeyCode::ShiftLeft => Key::ShiftLeft,
            KeyCode::ShiftRight => Key::ShiftRight,
            KeyCode::ControlLeft => Key::ControlLeft,
            KeyCode::ControlRight => Key::ControlRight,
            KeyCode::AltLeft => Key::AltLeft,
            KeyCode::AltRight => Key::AltRight,
            KeyCode::SuperLeft => Key::SuperLeft,
            KeyCode::SuperRight => Key::SuperRight,
            KeyCode::F1 => Key::F1,
            KeyCode::F2 => Key::F2,
            KeyCode::F3 => Key::F3,
            KeyCode::F4 => Key::F4,
            KeyCode::F5 => Key::F5,
            KeyCode::F6 => Key::F6,
            KeyCode::F7 => Key::F7,
            KeyCode::F8 => Key::F8,
            KeyCode::F9 => Key::F9,
            KeyCode::F10 => Key::F10,
            KeyCode::F11 => Key::F11,
            KeyCode::F12 => Key::F12,
            KeyCode::Minus => Key::Minus,
            KeyCode::Equal => Key::Equal,
            KeyCode::BracketLeft => Key::BracketLeft,
            KeyCode::BracketRight => Key::BracketRight,
            KeyCode::Backslash => Key::Backslash,
            KeyCode::Semicolon => Key::Semicolon,
            KeyCode::Quote => Key::Quote,
            KeyCode::Comma => Key::Comma,
            KeyCode::Period => Key::Period,
            KeyCode::Slash => Key::Slash,
            KeyCode::Backquote => Key::Backquote,
            KeyCode::Numpad0 => Key::NumPad0,
            KeyCode::Numpad1 => Key::NumPad1,
            KeyCode::Numpad2 => Key::NumPad2,
            KeyCode::Numpad3 => Key::NumPad3,
            KeyCode::Numpad4 => Key::NumPad4,
            KeyCode::Numpad5 => Key::NumPad5,
            KeyCode::Numpad6 => Key::NumPad6,
            KeyCode::Numpad7 => Key::NumPad7,
            KeyCode::Numpad8 => Key::NumPad8,
            KeyCode::Numpad9 => Key::NumPad9,
            KeyCode::NumpadAdd => Key::NumPadAdd,
            KeyCode::NumpadSubtract => Key::NumPadSubtract,
            KeyCode::NumpadMultiply => Key::NumPadMultiply,
            KeyCode::NumpadDivide => Key::NumPadDivide,
            KeyCode::NumpadEnter => Key::NumPadEnter,
            _ => Key::Unknown,
        }
    }
}

/// Input event handed to the host. Field meaning depends on [`InputEvent::kind`].
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct InputEvent {
    pub kind: u32,
    /// Key code, mouse button index or touch id.
    pub code: u32,
    /// Bit 0 = shift, 1 = control, 2 = alt, 3 = super.
    pub modifiers: u32,
    /// Cursor position, or the new size for `Resized`.
    pub x: f32,
    pub y: f32,
    /// Movement delta or wheel delta.
    pub delta_x: f32,
    pub delta_y: f32,
    /// Non-zero when a key event is an auto-repeat.
    pub repeat: u32,
}

impl InputEvent {
    #[cfg(not(any(target_os = "android", target_os = "emscripten")))]
    fn new(kind: EventKind) -> Self {
        Self {
            kind: kind as u32,
            code: 0,
            modifiers: 0,
            x: 0.0,
            y: 0.0,
            delta_x: 0.0,
            delta_y: 0.0,
            repeat: 0,
        }
    }
}

/// Callbacks implemented by the host application.
pub trait AppHandler {
    /// Called once the window and the renderer exist.
    fn on_init(&mut self, renderer: &mut Renderer);
    /// Called once per frame with the elapsed seconds since the previous frame.
    fn on_frame(&mut self, renderer: &mut Renderer, delta_seconds: f32);
    fn on_event(&mut self, renderer: &mut Renderer, event: &InputEvent);
    /// Return `false` to keep the window open on a close request.
    fn on_close(&mut self) -> bool {
        true
    }
}

#[derive(Debug, Clone)]
pub struct WindowConfig {
    pub title: String,
    pub width: u32,
    pub height: u32,
    pub resizable: bool,
    pub decorations: bool,
    pub renderer: RendererConfig,
}

impl Default for WindowConfig {
    fn default() -> Self {
        Self {
            title: String::from("Three.Net"),
            width: 1280,
            height: 720,
            resizable: true,
            decorations: true,
            renderer: RendererConfig::default(),
        }
    }
}

#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
struct App<H: AppHandler> {
    config: WindowConfig,
    handler: H,
    window: Option<Arc<Window>>,
    /// Boxed so the address stays stable: hosts such as the .NET binding keep
    /// the pointer handed to `on_init` for the whole lifetime of the window.
    renderer: Option<Box<Renderer>>,
    last_frame: Instant,
    cursor: (f32, f32),
    modifiers: u32,
    error: Option<Error>,
}

#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
impl<H: AppHandler> ApplicationHandler for App<H> {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.window.is_some() {
            return;
        }
        let attributes = Window::default_attributes()
            .with_title(self.config.title.clone())
            .with_resizable(self.config.resizable)
            .with_decorations(self.config.decorations)
            .with_inner_size(winit::dpi::LogicalSize::new(
                self.config.width,
                self.config.height,
            ));
        let window = match event_loop.create_window(attributes) {
            Ok(window) => Arc::new(window),
            Err(error) => {
                self.error = Some(Error::Surface(error.to_string()));
                event_loop.exit();
                return;
            }
        };

        let size = window.inner_size();
        let mut renderer_config = self.config.renderer;
        renderer_config.width = size.width.max(1);
        renderer_config.height = size.height.max(1);

        match Renderer::new_with_surface(window.clone().into(), renderer_config) {
            Ok(renderer) => {
                // Store the renderer before handing it to the handler: moving it
                // afterwards would invalidate any pointer the host kept.
                self.renderer = Some(Box::new(renderer));
                self.window = Some(window);
                self.last_frame = Instant::now();
                let renderer = self.renderer.as_mut().expect("just stored");
                self.handler.on_init(renderer);
            }
            Err(error) => {
                self.error = Some(error);
                event_loop.exit();
            }
        }
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, _id: WindowId, event: WindowEvent) {
        let (Some(renderer), Some(window)) = (self.renderer.as_mut(), self.window.as_ref()) else {
            return;
        };

        match event {
            WindowEvent::CloseRequested => {
                let mut input = InputEvent::new(EventKind::CloseRequested);
                input.modifiers = self.modifiers;
                self.handler.on_event(renderer, &input);
                if self.handler.on_close() {
                    event_loop.exit();
                }
            }
            WindowEvent::Resized(size) => {
                renderer.resize(size.width, size.height);
                let mut input = InputEvent::new(EventKind::Resized);
                input.x = size.width as f32;
                input.y = size.height as f32;
                self.handler.on_event(renderer, &input);
            }
            WindowEvent::ScaleFactorChanged { scale_factor, .. } => {
                let mut input = InputEvent::new(EventKind::ScaleFactorChanged);
                input.x = scale_factor as f32;
                self.handler.on_event(renderer, &input);
            }
            WindowEvent::Focused(focused) => {
                let mut input = InputEvent::new(EventKind::Focus);
                input.code = u32::from(focused);
                self.handler.on_event(renderer, &input);
            }
            WindowEvent::ModifiersChanged(modifiers) => {
                let state = modifiers.state();
                self.modifiers = u32::from(state.shift_key())
                    | (u32::from(state.control_key()) << 1)
                    | (u32::from(state.alt_key()) << 2)
                    | (u32::from(state.super_key()) << 3);
            }
            WindowEvent::KeyboardInput { event, .. } => {
                let kind = if event.state == ElementState::Pressed {
                    EventKind::KeyDown
                } else {
                    EventKind::KeyUp
                };
                let mut input = InputEvent::new(kind);
                input.code = Key::from_physical(event.physical_key) as u32;
                input.modifiers = self.modifiers;
                input.repeat = u32::from(event.repeat);
                self.handler.on_event(renderer, &input);
            }
            WindowEvent::MouseInput { state, button, .. } => {
                let kind = if state == ElementState::Pressed {
                    EventKind::MouseDown
                } else {
                    EventKind::MouseUp
                };
                let mut input = InputEvent::new(kind);
                input.code = match button {
                    winit::event::MouseButton::Left => 0,
                    winit::event::MouseButton::Right => 1,
                    winit::event::MouseButton::Middle => 2,
                    winit::event::MouseButton::Back => 3,
                    winit::event::MouseButton::Forward => 4,
                    winit::event::MouseButton::Other(other) => 5 + other as u32,
                };
                input.x = self.cursor.0;
                input.y = self.cursor.1;
                input.modifiers = self.modifiers;
                self.handler.on_event(renderer, &input);
            }
            WindowEvent::CursorMoved { position, .. } => {
                let (x, y) = (position.x as f32, position.y as f32);
                let mut input = InputEvent::new(EventKind::MouseMove);
                input.x = x;
                input.y = y;
                input.delta_x = x - self.cursor.0;
                input.delta_y = y - self.cursor.1;
                input.modifiers = self.modifiers;
                self.cursor = (x, y);
                self.handler.on_event(renderer, &input);
            }
            WindowEvent::MouseWheel { delta, .. } => {
                let mut input = InputEvent::new(EventKind::MouseWheel);
                match delta {
                    MouseScrollDelta::LineDelta(x, y) => {
                        input.delta_x = x;
                        input.delta_y = y;
                    }
                    MouseScrollDelta::PixelDelta(position) => {
                        // Normalise pixels to approximate wheel lines.
                        input.delta_x = position.x as f32 / 120.0;
                        input.delta_y = position.y as f32 / 120.0;
                    }
                }
                input.x = self.cursor.0;
                input.y = self.cursor.1;
                input.modifiers = self.modifiers;
                self.handler.on_event(renderer, &input);
            }
            WindowEvent::Touch(touch) => {
                let kind = match touch.phase {
                    winit::event::TouchPhase::Started => EventKind::TouchBegin,
                    winit::event::TouchPhase::Moved => EventKind::TouchMove,
                    _ => EventKind::TouchEnd,
                };
                let mut input = InputEvent::new(kind);
                input.code = touch.id as u32;
                input.x = touch.location.x as f32;
                input.y = touch.location.y as f32;
                self.handler.on_event(renderer, &input);
            }
            WindowEvent::RedrawRequested => {
                let now = Instant::now();
                let delta = (now - self.last_frame).as_secs_f32();
                self.last_frame = now;
                self.handler.on_frame(renderer, delta);
                window.pre_present_notify();
            }
            _ => {}
        }
    }

    fn about_to_wait(&mut self, _event_loop: &ActiveEventLoop) {
        // Drive a continuous render loop.
        if let Some(window) = &self.window {
            window.request_redraw();
        }
    }
}

/// Native windows are not available on Android: the host app owns the
/// activity and renders through an offscreen renderer or its own surface.
#[cfg(any(target_os = "android", target_os = "emscripten"))]
pub fn run_app<H: AppHandler>(_config: WindowConfig, _handler: H) -> Result<()> {
    Err(Error::Surface("native windows are not supported on this platform; render offscreen or into a host surface".into()))
}

/// Opens a window and runs the render loop until it is closed. Must be called
/// from the main thread on macOS and Windows.
#[cfg(not(any(target_os = "android", target_os = "emscripten")))]
pub fn run_app<H: AppHandler>(config: WindowConfig, handler: H) -> Result<()> {
    let mut builder = EventLoop::builder();
    // A .NET host cannot make its main thread a single threaded apartment, so
    // the managed side runs this loop on a dedicated STA thread instead. winit
    // rejects that by default on Windows.
    #[cfg(target_os = "windows")]
    {
        use winit::platform::windows::EventLoopBuilderExtWindows;
        builder.with_any_thread(true);
    }
    let event_loop = builder.build().map_err(|e| Error::Surface(e.to_string()))?;
    event_loop.set_control_flow(ControlFlow::Poll);
    let mut app = App {
        config,
        handler,
        window: None,
        renderer: None,
        last_frame: Instant::now(),
        cursor: (0.0, 0.0),
        modifiers: 0,
        error: None,
    };
    event_loop
        .run_app(&mut app)
        .map_err(|e| Error::Surface(e.to_string()))?;
    match app.error {
        Some(error) => Err(error),
        None => Ok(()),
    }
}
