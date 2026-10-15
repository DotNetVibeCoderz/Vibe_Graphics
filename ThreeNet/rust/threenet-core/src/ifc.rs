//! IFC (`.ifc`) import: the tessellated geometry out of a STEP physical file.
//!
//! An IFC file is ISO 10303-21 - numbered entity lines, each naming a type and
//! a list of arguments, with `#n` references between them:
//!
//! ```text
//! #12=IFCCARTESIANPOINT((0.,0.,0.));
//! #34=IFCPOLYLOOP((#12,#13,#14));
//! ```
//!
//! What this reads is the geometry that is already triangles or polygons:
//!
//! - `IFCTRIANGULATEDFACESET` with an `IFCCARTESIANPOINTLIST3D`, which is how
//!   IFC4 exporters write meshed geometry and by far the most common case.
//! - `IFCPOLYLOOP` over `IFCCARTESIANPOINT`, which is the older faceted B-rep.
//!
//! **What it does not read is the parametric half of IFC**, and that half is
//! large: swept solids, extrusions along a directrix, constructive solid
//! geometry with booleans, and the placement tree that positions all of it.
//! Evaluating those is a geometry kernel, not a loader. A file written entirely
//! in parametric form reports that rather than importing empty, so the
//! difference is visible instead of looking like a broken model.

use std::collections::HashMap;
use std::path::Path;

use crate::collada::{place, ColladaMesh};
use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};

/// One parsed entity line: its type and its raw argument text.
struct Entity {
    kind: String,
    args: String,
}

/// Splits the data section into entities by id.
///
/// STEP allows a statement to run over many lines, so this accumulates until
/// the terminating semicolon rather than reading line by line.
fn parse_entities(text: &str) -> HashMap<u32, Entity> {
    let mut entities = HashMap::new();
    let body = match (text.find("DATA;"), text.find("ENDSEC;")) {
        (Some(start), Some(end)) if end > start => &text[start + 5..end],
        _ => text,
    };

    let mut statement = String::new();
    for character in body.chars() {
        if character == ';' {
            let trimmed = statement.trim();
            if let Some(rest) = trimmed.strip_prefix('#')
                && let Some((id, definition)) = rest.split_once('=')
                && let Ok(id) = id.trim().parse::<u32>()
            {
                let definition = definition.trim();
                if let Some(open) = definition.find('(') {
                    let kind = definition[..open].trim().to_ascii_uppercase();
                    // Exactly one closing bracket, not every one: a point list
                    // ends in ")))" and stripping them all would cut the last
                    // coordinate in half.
                    let inner = &definition[open + 1..];
                    let args = inner.strip_suffix(')').unwrap_or(inner).to_string();
                    entities.insert(id, Entity { kind, args });
                }
            }
            statement.clear();
            continue;
        }
        statement.push(character);
    }
    entities
}

/// Splits an argument list on commas that are not inside brackets or quotes.
fn split_args(args: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut current = String::new();
    let mut depth = 0i32;
    let mut quoted = false;

    for character in args.chars() {
        match character {
            '\'' => {
                quoted = !quoted;
                current.push(character);
            }
            '(' if !quoted => {
                depth += 1;
                current.push(character);
            }
            ')' if !quoted => {
                depth -= 1;
                current.push(character);
            }
            ',' if depth == 0 && !quoted => parts.push(std::mem::take(&mut current)),
            c => current.push(c),
        }
    }
    if !current.trim().is_empty() {
        parts.push(current);
    }
    parts
}

/// Reads `#123` out of a reference argument.
fn reference(text: &str) -> Option<u32> {
    text.trim().strip_prefix('#')?.trim().parse().ok()
}

/// Reads the numbers out of a nested list like `((0.,0.,0.),(1.,0.,0.))`.
fn numbers(text: &str) -> Vec<f32> {
    text.split(|c: char| c == ',' || c == '(' || c == ')' || c.is_whitespace())
        .filter(|part| !part.is_empty())
        .filter_map(|part| part.parse::<f32>().ok())
        .collect()
}

/// Reads the groups of a nested list, one `Vec` per inner bracket.
fn groups(text: &str) -> Vec<Vec<f32>> {
    let mut out = Vec::new();
    let mut depth = 0i32;
    let mut current = String::new();
    for character in text.chars() {
        match character {
            '(' => {
                depth += 1;
                if depth == 2 {
                    current.clear();
                }
            }
            ')' => {
                if depth == 2 {
                    out.push(numbers(&current));
                }
                depth -= 1;
            }
            c if depth >= 2 => current.push(c),
            _ => {}
        }
    }
    out
}

/// Parses an IFC file into meshes.
pub fn parse_ifc(text: &str) -> Result<Vec<ColladaMesh>> {
    let entities = parse_entities(text);
    let mut meshes: Vec<ColladaMesh> = Vec::new();
    let mut parametric = 0usize;

    for (id, entity) in &entities {
        match entity.kind.as_str() {
            "IFCTRIANGULATEDFACESET" => {
                let args = split_args(&entity.args);
                // Coordinates, Normals, Closed, CoordIndex, PnIndex.
                let Some(list) = args.first().and_then(|a| reference(a)) else {
                    continue;
                };
                let Some(points) = entities.get(&list) else {
                    continue;
                };
                let positions: Vec<Vec3> = groups(&points.args)
                    .into_iter()
                    .filter(|triple| triple.len() >= 3)
                    .map(|triple| Vec3::new(triple[0], triple[1], triple[2]))
                    .collect();
                let faces = args.get(3).map(|a| groups(a)).unwrap_or_default();
                if positions.is_empty() || faces.is_empty() {
                    continue;
                }

                let limit = positions.len() as u32;
                let mut indices = Vec::new();
                for face in faces {
                    if face.len() < 3 {
                        continue;
                    }
                    // IFC indices are one based.
                    let corners: Vec<u32> = face.iter().map(|v| *v as u32).collect();
                    if corners.iter().any(|index| *index == 0 || *index > limit) {
                        continue;
                    }
                    for i in 1..corners.len() - 1 {
                        indices.extend_from_slice(&[
                            corners[0] - 1,
                            corners[i] - 1,
                            corners[i + 1] - 1,
                        ]);
                    }
                }
                if let Some(geometry) = finish(positions, indices) {
                    meshes.push(ColladaMesh {
                        name: format!("faceset{id}"),
                        geometry,
                    });
                }
            }
            "IFCFACETEDBREP" | "IFCCLOSEDSHELL" | "IFCOPENSHELL" => {}
            "IFCEXTRUDEDAREASOLID" | "IFCSWEPTDISKSOLID" | "IFCBOOLEANRESULT"
            | "IFCBOOLEANCLIPPINGRESULT" | "IFCREVOLVEDAREASOLID" => parametric += 1,
            _ => {}
        }
    }

    // The older faceted form: every polyloop is a face in its own right, and
    // they are gathered into one mesh because the shell that grouped them is
    // not followed.
    if meshes.is_empty() {
        let mut positions: Vec<Vec3> = Vec::new();
        let mut indices: Vec<u32> = Vec::new();
        let mut ids: Vec<u32> = entities
            .iter()
            .filter(|(_, entity)| entity.kind == "IFCPOLYLOOP")
            .map(|(id, _)| *id)
            .collect();
        // Sorted so a file reads the same way twice; a HashMap does not promise
        // an order and a mesh that changed between loads would be maddening.
        ids.sort_unstable();

        for id in ids {
            let Some(loop_entity) = entities.get(&id) else {
                continue;
            };
            let corners: Vec<Vec3> = split_args(loop_entity.args.trim_matches(['(', ')']))
                .iter()
                .filter_map(|argument| reference(argument))
                .filter_map(|point| entities.get(&point))
                .filter(|point| point.kind == "IFCCARTESIANPOINT")
                .map(|point| {
                    let values = numbers(&point.args);
                    Vec3::new(
                        values.first().copied().unwrap_or(0.0),
                        values.get(1).copied().unwrap_or(0.0),
                        values.get(2).copied().unwrap_or(0.0),
                    )
                })
                .collect();
            if corners.len() < 3 {
                continue;
            }
            let base = positions.len() as u32;
            positions.extend_from_slice(&corners);
            for i in 1..corners.len() - 1 {
                indices.extend_from_slice(&[base, base + i as u32, base + i as u32 + 1]);
            }
        }
        if let Some(geometry) = finish(positions, indices) {
            meshes.push(ColladaMesh {
                name: "brep".to_string(),
                geometry,
            });
        }
    }

    if meshes.is_empty() {
        return Err(Error::Asset(if parametric > 0 {
            format!(
                "this IFC holds {parametric} parametric solid(s) - extrusions, sweeps or booleans - \
                 and no tessellated geometry. Evaluating those needs a geometry kernel; export the \
                 model with tessellation turned on and it will load"
            )
        } else {
            "the IFC file holds no geometry this loader can read".into()
        }));
    }
    Ok(meshes)
}

fn finish(positions: Vec<Vec3>, indices: Vec<u32>) -> Option<Geometry> {
    if positions.is_empty() || indices.is_empty() {
        return None;
    }
    let vertices: Vec<Vertex> = positions
        .into_iter()
        .map(|position| Vertex::new(position, Vec3::Y, Vec2::ZERO))
        .collect();
    let mut geometry = Geometry::new(vertices, indices);
    geometry.compute_normals();
    geometry.compute_tangents();
    Some(geometry)
}

/// Imports an `.ifc` file into `scene` under `parent`.
pub fn load_ifc(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let text = std::fs::read_to_string(path)?;
    let meshes = parse_ifc(&text)?;
    let name = Path::new(path)
        .file_stem()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();
    place(scene, parent, &name, meshes)
}

#[cfg(test)]
mod tests {
    use super::*;

    const TESSELLATED: &str = "ISO-10303-21;\n\
        HEADER;\n\
        ENDSEC;\n\
        DATA;\n\
        #1=IFCCARTESIANPOINTLIST3D(((0.,0.,0.),(1.,0.,0.),(0.,1.,0.)));\n\
        #2=IFCTRIANGULATEDFACESET(#1,$,.T.,((1,2,3)),$);\n\
        ENDSEC;\n\
        END-ISO-10303-21;\n";

    #[test]
    fn reads_a_triangulated_face_set() {
        let meshes = parse_ifc(TESSELLATED).unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(meshes[0].geometry.vertices.len(), 3);
        assert_eq!(meshes[0].geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn indices_are_one_based() {
        // IFC counts from one. Reading them as zero based would point the first
        // corner at the second vertex and silently skew every face.
        let meshes = parse_ifc(TESSELLATED).unwrap();
        assert_eq!(meshes[0].geometry.vertices[0].position, [0.0, 0.0, 0.0]);
        assert_eq!(meshes[0].geometry.indices[0], 0);
    }

    #[test]
    fn a_statement_split_over_lines_still_parses() {
        // STEP lets a statement run across lines, so reading line by line would
        // lose any entity an exporter chose to wrap.
        let wrapped = TESSELLATED.replace(
            "#1=IFCCARTESIANPOINTLIST3D(((0.,0.,0.),(1.,0.,0.),(0.,1.,0.)));",
            "#1=IFCCARTESIANPOINTLIST3D(((0.,0.,0.),\n  (1.,0.,0.),\n  (0.,1.,0.)));",
        );
        assert_eq!(parse_ifc(&wrapped).unwrap()[0].geometry.vertices.len(), 3);
    }

    #[test]
    fn reads_the_older_faceted_form() {
        let brep = "ISO-10303-21;\nDATA;\n\
            #1=IFCCARTESIANPOINT((0.,0.,0.));\n\
            #2=IFCCARTESIANPOINT((1.,0.,0.));\n\
            #3=IFCCARTESIANPOINT((0.,1.,0.));\n\
            #4=IFCPOLYLOOP((#1,#2,#3));\n\
            ENDSEC;\n";
        let meshes = parse_ifc(brep).unwrap();
        assert_eq!(meshes[0].geometry.indices.len(), 3);
    }

    #[test]
    fn a_parametric_only_file_says_so() {
        // The failure that matters: a model written as extrusions imports as
        // nothing, and the reason has to be visible rather than looking like a
        // broken file.
        let parametric = "ISO-10303-21;\nDATA;\n\
            #1=IFCEXTRUDEDAREASOLID(#2,#3,#4,1000.);\n\
            ENDSEC;\n";
        let error = parse_ifc(parametric).unwrap_err();
        let message = format!("{error}");
        assert!(message.contains("parametric"), "unhelpful message: {message}");
        assert!(message.contains("tessellation"), "no way forward offered: {message}");
    }

    #[test]
    fn a_face_pointing_past_the_points_is_dropped() {
        let broken = TESSELLATED.replace("((1,2,3))", "((1,2,9))");
        assert!(parse_ifc(&broken).is_err());
    }

    #[test]
    fn something_that_is_not_ifc_is_an_error() {
        assert!(parse_ifc("hello").is_err());
    }
}
