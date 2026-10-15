//! SVG (`.svg`) import: 2D outlines turned into 3D by extruding them.
//!
//! An SVG is a drawing, not a model, so the question is what a loader should
//! produce. Extruding each outline to a given depth is the answer that makes a
//! logo into a solid, and it is what the extrude generator from the geometry
//! group was built for.
//!
//! Paths are flattened to line segments first: curves become a fixed number of
//! steps, which is what the triangulator needs since it works on polygons.
//! Supported path commands are the ones that describe an outline - move, line,
//! horizontal and vertical line, cubic and quadratic bezier, and close - in
//! both their absolute and relative spellings. Arcs are not supported, and a
//! path using one is skipped rather than silently closed through the middle.
//!
//! SVG's y axis points down; the outlines are flipped on the way in so a logo
//! is not extruded upside down.

use std::path::Path;

use quick_xml::events::Event;
use quick_xml::Reader;

use crate::error::{Error, Result};
use crate::geometry::{primitives, Geometry};
use crate::loaders::ImportResult;
use crate::material::Material;
use crate::math::Vec2;
use crate::scene::{NodeId, Scene};

/// How many segments a curve is flattened into. Enough that a logo's corners
/// read as curves and few enough that the triangulator stays quick.
const CURVE_STEPS: usize = 12;

/// One extruded outline.
#[derive(Debug)]
pub struct SvgShape {
    pub name: String,
    pub geometry: Geometry,
}

fn attribute(event: &quick_xml::events::BytesStart<'_>, name: &str) -> Option<String> {
    event.attributes().flatten().find_map(|attribute| {
        if attribute.key.as_ref() == name.as_bytes() {
            Some(String::from_utf8_lossy(&attribute.value).into_owned())
        } else {
            None
        }
    })
}

/// Splits a path's `d` attribute into commands and numbers.
///
/// SVG allows numbers to run together where the sign or decimal point makes the
/// break unambiguous - `10-5` is two numbers, and so is `.5.5` - so this cannot
/// simply split on whitespace.
fn tokenise_path(d: &str) -> Vec<String> {
    let mut tokens: Vec<String> = Vec::new();
    let mut number = String::new();

    let flush = |number: &mut String, tokens: &mut Vec<String>| {
        if !number.is_empty() {
            tokens.push(std::mem::take(number));
        }
    };

    for character in d.chars() {
        match character {
            c if c.is_ascii_alphabetic() => {
                flush(&mut number, &mut tokens);
                tokens.push(c.to_string());
            }
            '-' | '+' => {
                // A sign starts a new number unless it follows an exponent.
                if !number.is_empty() && !number.ends_with(['e', 'E']) {
                    flush(&mut number, &mut tokens);
                }
                number.push(character);
            }
            '.' => {
                // A second dot in one number means a new number began.
                if number.contains('.') {
                    flush(&mut number, &mut tokens);
                }
                number.push('.');
            }
            c if c.is_ascii_digit() || c == 'e' || c == 'E' => number.push(c),
            _ => flush(&mut number, &mut tokens),
        }
    }
    flush(&mut number, &mut tokens);
    tokens
}

/// Flattens a path into closed outlines of straight segments.
///
/// Returns `None` when the path uses something that cannot be flattened, so the
/// caller can skip it rather than draw a wrong shape.
pub fn flatten_path(d: &str) -> Option<Vec<Vec<Vec2>>> {
    let tokens = tokenise_path(d);
    let mut outlines: Vec<Vec<Vec2>> = Vec::new();
    let mut current: Vec<Vec2> = Vec::new();
    let mut cursor = Vec2::ZERO;
    let mut start = Vec2::ZERO;
    let mut index = 0usize;
    let mut command = ' ';

    let number = |tokens: &[String], index: &mut usize| -> Option<f32> {
        let value = tokens.get(*index)?.parse().ok()?;
        *index += 1;
        Some(value)
    };

    while index < tokens.len() {
        let token = tokens[index].clone();
        if token.chars().next()?.is_ascii_alphabetic() {
            command = token.chars().next()?;
            index += 1;
        }
        // A repeated set of numbers continues the previous command, which is
        // how "L 1 1 2 2 3 3" draws three lines.
        let relative = command.is_lowercase();
        match command.to_ascii_uppercase() {
            'M' => {
                let x = number(&tokens, &mut index)?;
                let y = number(&tokens, &mut index)?;
                if current.len() > 2 {
                    outlines.push(std::mem::take(&mut current));
                } else {
                    current.clear();
                }
                cursor = if relative { cursor + Vec2::new(x, y) } else { Vec2::new(x, y) };
                start = cursor;
                current.push(cursor);
                // A second pair after a move is a line, per the specification.
                command = if relative { 'l' } else { 'L' };
            }
            'L' => {
                let x = number(&tokens, &mut index)?;
                let y = number(&tokens, &mut index)?;
                cursor = if relative { cursor + Vec2::new(x, y) } else { Vec2::new(x, y) };
                current.push(cursor);
            }
            'H' => {
                let x = number(&tokens, &mut index)?;
                cursor.x = if relative { cursor.x + x } else { x };
                current.push(cursor);
            }
            'V' => {
                let y = number(&tokens, &mut index)?;
                cursor.y = if relative { cursor.y + y } else { y };
                current.push(cursor);
            }
            'C' => {
                let p1 = read_point(&tokens, &mut index, cursor, relative)?;
                let p2 = read_point(&tokens, &mut index, cursor, relative)?;
                let end = read_point(&tokens, &mut index, cursor, relative)?;
                for step in 1..=CURVE_STEPS {
                    let t = step as f32 / CURVE_STEPS as f32;
                    current.push(cubic(cursor, p1, p2, end, t));
                }
                cursor = end;
            }
            'Q' => {
                let control = read_point(&tokens, &mut index, cursor, relative)?;
                let end = read_point(&tokens, &mut index, cursor, relative)?;
                for step in 1..=CURVE_STEPS {
                    let t = step as f32 / CURVE_STEPS as f32;
                    current.push(quadratic(cursor, control, end, t));
                }
                cursor = end;
            }
            'Z' => {
                if current.len() > 2 {
                    outlines.push(std::mem::take(&mut current));
                } else {
                    current.clear();
                }
                cursor = start;
            }
            // Arcs would need their own flattening, and guessing at one would
            // put a straight line where a curve belongs.
            'A' => return None,
            _ => return None,
        }
    }

    if current.len() > 2 {
        outlines.push(current);
    }
    if outlines.is_empty() {
        return None;
    }
    Some(outlines)
}

fn read_point(tokens: &[String], index: &mut usize, cursor: Vec2, relative: bool) -> Option<Vec2> {
    let x: f32 = tokens.get(*index)?.parse().ok()?;
    let y: f32 = tokens.get(*index + 1)?.parse().ok()?;
    *index += 2;
    Some(if relative { cursor + Vec2::new(x, y) } else { Vec2::new(x, y) })
}

fn cubic(a: Vec2, b: Vec2, c: Vec2, d: Vec2, t: f32) -> Vec2 {
    let u = 1.0 - t;
    a * (u * u * u) + b * (3.0 * u * u * t) + c * (3.0 * u * t * t) + d * (t * t * t)
}

fn quadratic(a: Vec2, b: Vec2, c: Vec2, t: f32) -> Vec2 {
    let u = 1.0 - t;
    a * (u * u) + b * (2.0 * u * t) + c * (t * t)
}

/// Parses an SVG document and extrudes every outline it can flatten.
pub fn parse_svg(text: &str, depth: f32) -> Result<Vec<SvgShape>> {
    let mut reader = Reader::from_str(text);
    reader.config_mut().trim_text(true);

    let mut shapes = Vec::new();
    let mut skipped = 0usize;

    loop {
        match reader.read_event() {
            Err(error) => return Err(Error::Asset(format!("malformed SVG: {error}"))),
            Ok(Event::Eof) => break,
            Ok(Event::Start(event)) | Ok(Event::Empty(event)) => {
                let outlines = match event.name().as_ref() {
                    b"path" => attribute(&event, "d").and_then(|d| flatten_path(&d)),
                    b"polygon" | b"polyline" => attribute(&event, "points").map(|points| {
                        let numbers: Vec<f32> = points
                            .split(|c: char| c == ',' || c.is_whitespace())
                            .filter(|part| !part.is_empty())
                            .filter_map(|part| part.parse().ok())
                            .collect();
                        vec![numbers
                            .chunks_exact(2)
                            .map(|pair| Vec2::new(pair[0], pair[1]))
                            .collect()]
                    }),
                    b"rect" => {
                        let read = |name: &str| {
                            attribute(&event, name).and_then(|v| v.parse::<f32>().ok()).unwrap_or(0.0)
                        };
                        let (x, y, w, h) = (read("x"), read("y"), read("width"), read("height"));
                        if w > 0.0 && h > 0.0 {
                            Some(vec![vec![
                                Vec2::new(x, y),
                                Vec2::new(x + w, y),
                                Vec2::new(x + w, y + h),
                                Vec2::new(x, y + h),
                            ]])
                        } else {
                            None
                        }
                    }
                    _ => None,
                };

                if event.name().as_ref() == b"path" && outlines.is_none() {
                    // Almost always an arc, which is not flattened here.
                    skipped += 1;
                }

                for outline in outlines.into_iter().flatten() {
                    if outline.len() < 3 {
                        continue;
                    }
                    // SVG's y runs down the page; flip it so the extrusion is
                    // the right way up.
                    let flipped: Vec<Vec2> =
                        outline.iter().map(|p| Vec2::new(p.x, -p.y)).collect();
                    let geometry = primitives::extrude(&flipped, depth);
                    if geometry.vertices.is_empty() {
                        continue;
                    }
                    shapes.push(SvgShape {
                        name: format!("shape{}", shapes.len()),
                        geometry,
                    });
                }
            }
            _ => {}
        }
    }

    if shapes.is_empty() {
        return Err(Error::Asset(format!(
            "the SVG holds no outline this loader can extrude ({skipped} path(s) skipped, \
             usually because they use arcs)"
        )));
    }
    Ok(shapes)
}

/// Imports an `.svg` file into `scene` under `parent`, extruded by `depth`.
pub fn load_svg(
    scene: &mut Scene,
    path: &str,
    parent: Option<NodeId>,
    depth: f32,
) -> Result<ImportResult> {
    let text = std::fs::read_to_string(path)?;
    let shapes = parse_svg(&text, depth)?;
    let material = scene.add_material(Material::default());

    let root = scene.create_node(parent)?;
    if let Some(node) = scene.node_mut(root) {
        node.name = Path::new(path)
            .file_stem()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_default();
    }

    let mut nodes = vec![root];
    let mut geometries = Vec::new();
    for shape in shapes {
        let geometry_id = scene.add_geometry(shape.geometry);
        let node = scene.add_mesh(Some(root), geometry_id, material)?;
        if let Some(target) = scene.node_mut(node) {
            target.name = shape.name;
        }
        nodes.push(node);
        geometries.push(geometry_id);
    }

    Ok(ImportResult {
        root,
        nodes,
        geometries,
        materials: vec![material],
        textures: Vec::new(),
        animations: Vec::new(),
        skins: Vec::new(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_rectangle_extrudes_into_a_box() {
        let svg = r##"<svg><rect x="0" y="0" width="10" height="5"/></svg>"##;
        let shapes = parse_svg(svg, 2.0).unwrap();
        assert_eq!(shapes.len(), 1);
        // Two caps of two triangles plus four walls of two each.
        assert!(shapes[0].geometry.indices.len() >= 24);
    }

    #[test]
    fn a_closed_path_extrudes() {
        let svg = r##"<svg><path d="M 0 0 L 10 0 L 10 10 Z"/></svg>"##;
        let shapes = parse_svg(svg, 1.0).unwrap();
        assert_eq!(shapes.len(), 1);
    }

    #[test]
    fn numbers_that_run_together_still_parse() {
        // "10-5" is two numbers, not one. Splitting on whitespace would read it
        // as a single malformed token and lose half the outline.
        let tokens = tokenise_path("M10-5L20-5Z");
        assert_eq!(tokens, vec!["M", "10", "-5", "L", "20", "-5", "Z"]);
    }

    #[test]
    fn a_curve_is_flattened_rather_than_cut_straight() {
        // Three corners, because two points is a line and not an outline -
        // flatten_path rightly refuses that.
        let straight = flatten_path("M 0 0 L 10 0 L 10 10 Z").unwrap();
        let curved = flatten_path("M 0 0 C 0 10 10 10 10 10 Z").unwrap();
        // The curve has to contribute many points, or it was read as a line.
        assert!(curved[0].len() > straight[0].len() + 5);
    }

    #[test]
    fn the_y_axis_is_flipped() {
        // SVG measures y downwards. A shape low on the page must not come out
        // high in the world.
        let svg = r##"<svg><rect x="0" y="0" width="4" height="4"/></svg>"##;
        let shapes = parse_svg(svg, 1.0).unwrap();
        let highest = shapes[0]
            .geometry
            .vertices
            .iter()
            .map(|v| v.position[1])
            .fold(f32::MIN, f32::max);
        // y = 0 at the top of the rect becomes the highest point, so the top of
        // the extrusion is 0 and everything else is below it.
        assert!(highest <= 0.001, "the shape was not flipped: highest y {highest}");
    }

    #[test]
    fn an_arc_is_skipped_rather_than_guessed_at() {
        // Drawing a straight line where an arc belongs would be worse than
        // saying so.
        assert!(flatten_path("M 0 0 A 5 5 0 0 1 10 10 Z").is_none());
        let svg = r##"<svg><path d="M 0 0 A 5 5 0 0 1 10 10 Z"/></svg>"##;
        let error = parse_svg(svg, 1.0).unwrap_err();
        assert!(format!("{error}").contains("arcs"));
    }

    #[test]
    fn a_polygon_element_works_too() {
        let svg = r##"<svg><polygon points="0,0 10,0 10,10"/></svg>"##;
        assert_eq!(parse_svg(svg, 1.0).unwrap().len(), 1);
    }
}
