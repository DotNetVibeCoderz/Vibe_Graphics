//! Screen space UI overlay (HUD): panels with rounded corners and borders,
//! images, and text, laid out in pixels with anchors and parents, drawn after
//! tone mapping. Elements can be hit tested for simple buttons.
//!
//! Colours are straight alpha sRGB, as UI colours usually are. The default
//! font is Inter Regular (SIL Open Font License); more fonts can be loaded.

use ab_glyph::{Font, FontArc, GlyphId, PxScale, ScaleFont};

use crate::error::{Error, Result};
use crate::scene::{Arena, TextureId};

pub type OverlayId = u32;

static DEFAULT_FONT: &[u8] = include_bytes!("fonts/Inter-Regular.otf");

/// Point of the parent (or viewport) an element is attached to; the same
/// point of the element is placed there, then shifted by `offset`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(u32)]
pub enum Anchor {
    #[default]
    TopLeft = 0,
    Top = 1,
    TopRight = 2,
    Left = 3,
    Center = 4,
    Right = 5,
    BottomLeft = 6,
    Bottom = 7,
    BottomRight = 8,
}

impl Anchor {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => Anchor::Top,
            2 => Anchor::TopRight,
            3 => Anchor::Left,
            4 => Anchor::Center,
            5 => Anchor::Right,
            6 => Anchor::BottomLeft,
            7 => Anchor::Bottom,
            8 => Anchor::BottomRight,
            _ => Anchor::TopLeft,
        }
    }

    /// Fractions (0, 0.5, 1) along x and y.
    fn fractions(self) -> (f32, f32) {
        let index = self as u32;
        ((index % 3) as f32 * 0.5, (index / 3) as f32 * 0.5)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(u32)]
pub enum TextAlign {
    #[default]
    Start = 0,
    Center = 1,
    End = 2,
}

impl TextAlign {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => TextAlign::Center,
            2 => TextAlign::End,
            _ => TextAlign::Start,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub enum OverlayContent {
    /// A filled rectangle (colour may be transparent for a pure border or group).
    Panel,
    /// A scene texture; `uv` is (u0, v0, u1, v1).
    Image { texture: TextureId, uv: [f32; 4] },
    Text {
        text: String,
        font: u32,
        size: f32,
        align: TextAlign,
        vertical_align: TextAlign,
        wrap: bool,
    },
}

#[derive(Debug, Clone, PartialEq)]
pub struct OverlayElement {
    pub content: OverlayContent,
    pub parent: Option<OverlayId>,
    pub anchor: Anchor,
    /// Pixels from the anchor point (x right, y down).
    pub offset: [f32; 2],
    /// Width and height in pixels; 0 for text means "fit the text".
    pub size: [f32; 2],
    /// Fill, tint or text colour (sRGB, straight alpha).
    pub color: [f32; 4],
    pub border_color: [f32; 4],
    pub border_width: f32,
    pub corner_radius: f32,
    /// Higher layers draw on top; ties keep creation order.
    pub layer: i32,
    pub visible: bool,
    /// Only interactive elements are returned by hit tests.
    pub interactive: bool,
}

impl OverlayElement {
    pub fn new(content: OverlayContent) -> Self {
        Self {
            content,
            parent: None,
            anchor: Anchor::TopLeft,
            offset: [0.0, 0.0],
            size: [0.0, 0.0],
            color: [1.0, 1.0, 1.0, 1.0],
            border_color: [0.0, 0.0, 0.0, 0.0],
            border_width: 0.0,
            corner_radius: 0.0,
            layer: 0,
            visible: true,
            interactive: false,
        }
    }
}

/// Pixel rectangle: x, y (top left), width, height.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Rect {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

impl Rect {
    pub fn contains(&self, x: f32, y: f32) -> bool {
        x >= self.x && y >= self.y && x < self.x + self.width && y < self.y + self.height
    }
}

/// One glyph placed by [`Overlay::layout_text`], relative to the text origin.
#[derive(Debug, Clone, Copy)]
pub struct PlacedGlyph {
    pub glyph: GlyphId,
    /// Pen position on the baseline.
    pub x: f32,
    pub baseline: f32,
}

#[derive(Debug, Clone)]
pub struct TextLayout {
    pub glyphs: Vec<PlacedGlyph>,
    pub width: f32,
    pub height: f32,
}

#[derive(Debug)]
pub struct Overlay {
    elements: Arena<OverlayElement>,
    /// Creation order of each element, for stable draw order.
    sequences: std::collections::HashMap<OverlayId, u64>,
    fonts: Vec<FontArc>,
    next_sequence: u64,
    /// Multiplies every size, offset and font size (UI scale / DPI).
    pub scale: f32,
    pub enabled: bool,
    pub(crate) version: u64,
}

impl Default for Overlay {
    fn default() -> Self {
        Self::new()
    }
}

impl Overlay {
    pub fn new() -> Self {
        let font = FontArc::try_from_slice(DEFAULT_FONT).expect("embedded Inter font is valid");
        Self {
            elements: Arena::default(),
            sequences: std::collections::HashMap::new(),
            fonts: vec![font],
            next_sequence: 1,
            scale: 1.0,
            enabled: true,
            version: 1,
        }
    }

    pub fn add(&mut self, element: OverlayElement) -> Result<OverlayId> {
        self.validate(&element, None)?;
        let id = self.elements.insert(element);
        self.sequences.insert(id, self.next_sequence);
        self.next_sequence += 1;
        self.version += 1;
        Ok(id)
    }

    fn validate(&self, element: &OverlayElement, id: Option<OverlayId>) -> Result<()> {
        if let Some(parent) = element.parent {
            if !self.elements.contains(parent) {
                return Err(Error::InvalidHandle("parent overlay element"));
            }
            // Walk up to make sure the element is not its own ancestor.
            let mut current = Some(parent);
            while let Some(ancestor) = current {
                if Some(ancestor) == id {
                    return Err(Error::InvalidArgument("overlay parent cycle".into()));
                }
                current = self.elements.get(ancestor).and_then(|e| e.parent);
            }
        }
        if let OverlayContent::Text { font, .. } = &element.content
            && *font as usize >= self.fonts.len()
        {
            return Err(Error::InvalidHandle("font"));
        }
        Ok(())
    }

    /// Replaces an element, keeping its draw order.
    pub fn update(&mut self, id: OverlayId, element: OverlayElement) -> Result<()> {
        self.validate(&element, Some(id))?;
        let existing = self.elements.get_mut(id).ok_or(Error::InvalidHandle("overlay element"))?;
        *existing = element;
        self.version += 1;
        Ok(())
    }

    pub fn get(&self, id: OverlayId) -> Option<&OverlayElement> {
        self.elements.get(id)
    }

    /// Removes an element and all of its children.
    pub fn remove(&mut self, id: OverlayId) -> bool {
        if !self.elements.contains(id) {
            return false;
        }
        let mut pending = vec![id];
        while let Some(current) = pending.pop() {
            self.elements.remove(current);
            self.sequences.remove(&current);
            let children: Vec<OverlayId> = self
                .elements
                .iter()
                .filter(|(_, e)| e.parent == Some(current))
                .map(|(child, _)| child)
                .collect();
            pending.extend(children);
        }
        self.version += 1;
        true
    }

    pub fn clear(&mut self) {
        self.elements = Arena::default();
        self.sequences.clear();
        self.version += 1;
    }

    pub fn len(&self) -> usize {
        self.elements.len()
    }

    pub fn is_empty(&self) -> bool {
        self.elements.is_empty()
    }

    /// Loads a TTF / OTF font and returns its index.
    pub fn add_font(&mut self, bytes: Vec<u8>) -> Result<u32> {
        let font = FontArc::try_from_vec(bytes).map_err(|e| Error::Asset(format!("invalid font: {e}")))?;
        self.fonts.push(font);
        Ok(self.fonts.len() as u32 - 1)
    }

    pub fn font(&self, index: u32) -> Option<&FontArc> {
        self.fonts.get(index as usize)
    }

    /// Textures referenced by image elements.
    pub fn texture_ids(&self) -> Vec<TextureId> {
        self.elements
            .iter()
            .filter_map(|(_, e)| match e.content {
                OverlayContent::Image { texture, .. } => Some(texture),
                _ => None,
            })
            .collect()
    }

    /// Lays out text at `size` pixels, wrapping at `max_width` when given.
    pub fn layout_text(&self, font: u32, size: f32, text: &str, max_width: Option<f32>) -> TextLayout {
        let Some(font) = self.fonts.get(font as usize) else {
            return TextLayout { glyphs: Vec::new(), width: 0.0, height: 0.0 };
        };
        let scaled = font.as_scaled(PxScale::from(size.max(1.0)));
        let line_height = scaled.ascent() - scaled.descent() + scaled.line_gap();
        let space = scaled.h_advance(scaled.glyph_id(' '));

        let mut glyphs = Vec::new();
        let mut width = 0f32;
        let mut line = 0usize;
        for paragraph in text.split('\n') {
            let mut pen = 0f32;
            let mut first_word = true;
            for word in paragraph.split(' ') {
                let word_width = word_advance(&scaled, word);
                let start = if first_word { 0.0 } else { pen + space };
                if let Some(limit) = max_width
                    && !first_word
                    && start + word_width > limit
                {
                    width = width.max(pen);
                    line += 1;
                    pen = 0.0;
                } else if !first_word {
                    pen += space;
                }
                first_word = false;

                let baseline = scaled.ascent() + line as f32 * line_height;
                let mut previous: Option<GlyphId> = None;
                for character in word.chars() {
                    let id = scaled.glyph_id(character);
                    if let Some(previous) = previous {
                        pen += scaled.kern(previous, id);
                    }
                    glyphs.push(PlacedGlyph { glyph: id, x: pen, baseline });
                    pen += scaled.h_advance(id);
                    previous = Some(id);
                }
            }
            width = width.max(pen);
            line += 1;
        }
        TextLayout {
            glyphs,
            width,
            height: line.max(1) as f32 * line_height - scaled.line_gap(),
        }
    }

    /// Measures text in unscaled pixels.
    pub fn measure_text(&self, font: u32, size: f32, text: &str, max_width: Option<f32>) -> (f32, f32) {
        let layout = self.layout_text(font, size, text, max_width);
        (layout.width, layout.height)
    }

    /// Screen rectangles of every visible element in draw order (back to front).
    pub fn layout(&self, viewport_width: f32, viewport_height: f32) -> Vec<(OverlayId, Rect)> {
        let viewport = Rect { x: 0.0, y: 0.0, width: viewport_width, height: viewport_height };
        let mut rects: std::collections::HashMap<OverlayId, Option<(Rect, i32)>> = std::collections::HashMap::new();
        let ids: Vec<OverlayId> = self.elements.iter().map(|(id, _)| id).collect();
        for id in &ids {
            self.resolve(*id, viewport, &mut rects);
        }
        let mut visible: Vec<(OverlayId, Rect, i32, u64)> = ids
            .iter()
            .filter_map(|id| {
                let (rect, layer) = rects.get(id).copied().flatten()?;
                Some((*id, rect, layer, *self.sequences.get(id)?))
            })
            .collect();
        // Children draw above their parent: effective layer is inherited (max).
        visible.sort_by(|a, b| a.2.cmp(&b.2).then(a.3.cmp(&b.3)));
        visible.into_iter().map(|(id, rect, _, _)| (id, rect)).collect()
    }

    fn resolve(
        &self,
        id: OverlayId,
        viewport: Rect,
        cache: &mut std::collections::HashMap<OverlayId, Option<(Rect, i32)>>,
    ) -> Option<(Rect, i32)> {
        if let Some(cached) = cache.get(&id) {
            return *cached;
        }
        let element = self.elements.get(id)?;
        let result = if !element.visible {
            None
        } else {
            let parent = match element.parent {
                Some(parent) => self.resolve(parent, viewport, cache),
                None => Some((viewport, i32::MIN)),
            };
            parent.map(|(container, parent_layer)| {
                let scale = self.scale.max(0.01);
                let mut width = element.size[0] * scale;
                let mut height = element.size[1] * scale;
                if let OverlayContent::Text { text, font, size, wrap, .. } = &element.content
                    && (width <= 0.0 || height <= 0.0)
                {
                    let limit = (*wrap && width > 0.0).then_some(width);
                    let (w, h) = self.measure_text(*font, size * scale, text, limit);
                    if width <= 0.0 {
                        width = w.ceil();
                    }
                    if height <= 0.0 {
                        height = h.ceil();
                    }
                }
                let (fx, fy) = element.anchor.fractions();
                let rect = Rect {
                    x: container.x + container.width * fx - width * fx + element.offset[0] * scale,
                    y: container.y + container.height * fy - height * fy + element.offset[1] * scale,
                    width,
                    height,
                };
                (rect, element.layer.max(parent_layer))
            })
        };
        cache.insert(id, result);
        result
    }

    /// Topmost visible interactive element under the point.
    pub fn hit_test(&self, x: f32, y: f32, viewport_width: f32, viewport_height: f32) -> Option<OverlayId> {
        self.layout(viewport_width, viewport_height)
            .into_iter()
            .rev()
            .find(|(id, rect)| rect.contains(x, y) && self.elements.get(*id).is_some_and(|e| e.interactive))
            .map(|(id, _)| id)
    }
}

fn word_advance<F: Font, S: ScaleFont<F>>(scaled: &S, word: &str) -> f32 {
    let mut pen = 0.0;
    let mut previous: Option<GlyphId> = None;
    for character in word.chars() {
        let id = scaled.glyph_id(character);
        if let Some(previous) = previous {
            pen += scaled.kern(previous, id);
        }
        pen += scaled.h_advance(id);
        previous = Some(id);
    }
    pen
}

#[cfg(test)]
mod tests {
    use super::*;

    fn panel(anchor: Anchor, offset: [f32; 2], size: [f32; 2]) -> OverlayElement {
        OverlayElement { anchor, offset, size, ..OverlayElement::new(OverlayContent::Panel) }
    }

    #[test]
    fn anchors_parents_and_hit_testing() {
        let mut overlay = Overlay::new();
        let bar = overlay.add(panel(Anchor::Bottom, [0.0, -10.0], [200.0, 40.0])).unwrap();
        let button = overlay
            .add(OverlayElement {
                parent: Some(bar),
                interactive: true,
                ..panel(Anchor::Right, [-5.0, 0.0], [30.0, 20.0])
            })
            .unwrap();
        let layout: std::collections::HashMap<_, _> = overlay.layout(800.0, 600.0).into_iter().collect();
        assert_eq!(layout[&bar], Rect { x: 300.0, y: 550.0, width: 200.0, height: 40.0 });
        assert_eq!(layout[&button], Rect { x: 465.0, y: 560.0, width: 30.0, height: 20.0 });

        assert_eq!(overlay.hit_test(470.0, 565.0, 800.0, 600.0), Some(button));
        assert_eq!(overlay.hit_test(310.0, 565.0, 800.0, 600.0), None, "the bar is not interactive");

        overlay.remove(bar);
        assert!(overlay.is_empty(), "children go with their parent");
    }

    #[test]
    fn text_measures_and_wraps() {
        let overlay = Overlay::new();
        let (w, h) = overlay.measure_text(0, 20.0, "Hello world", None);
        assert!(w > 80.0 && w < 140.0, "width {w}");
        assert!(h > 18.0 && h < 30.0, "height {h}");
        let (wrapped_w, wrapped_h) = overlay.measure_text(0, 20.0, "Hello world", Some(70.0));
        assert!(wrapped_w < w && wrapped_h > h * 1.8, "{wrapped_w} x {wrapped_h}");
        let (_, two_lines) = overlay.measure_text(0, 20.0, "a\nb", None);
        assert!((two_lines - wrapped_h).abs() < 0.01);
    }

    #[test]
    fn invalid_parents_and_cycles_are_rejected() {
        let mut overlay = Overlay::new();
        assert!(overlay.add(OverlayElement { parent: Some(42), ..OverlayElement::new(OverlayContent::Panel) }).is_err());
        let a = overlay.add(OverlayElement::new(OverlayContent::Panel)).unwrap();
        let b = overlay.add(OverlayElement { parent: Some(a), ..OverlayElement::new(OverlayContent::Panel) }).unwrap();
        let mut looped = overlay.get(a).unwrap().clone();
        looped.parent = Some(b);
        assert!(overlay.update(a, looped).is_err());
    }
}
