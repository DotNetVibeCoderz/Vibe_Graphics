//! Modular shaders and custom shader injection.
//!
//! The renderer's shaders are assembled from WGSL modules: `common.wgsl`
//! (lighting library), `material.wgsl` (material evaluation and the vertex
//! stage) and a pass specific entry module (forward, deferred G-buffer). Custom
//! shaders plug into two hooks:
//!
//! ```wgsl
//! fn user_vertex(context: VertexContext) -> vec3<f32>               // object space position
//! fn user_surface(context: SurfaceContext, surface: Surface) -> Surface
//! ```
//!
//! Hooks may be written in WGSL or GLSL (translated to WGSL with naga). Either
//! hook can be omitted. Every custom shader is validated on the CPU before it
//! reaches the GPU, so a typo returns an error message instead of a device
//! error.

use crate::error::{Error, Result};

pub const COMMON: &str = include_str!("shaders/common.wgsl");
pub const MATERIAL: &str = include_str!("shaders/material.wgsl");
pub const FORWARD: &str = include_str!("shaders/forward.wgsl");
pub const DEFERRED_GBUFFER: &str = include_str!("shaders/deferred_gbuffer.wgsl");

const DEFAULT_VERTEX_HOOK: &str = "
fn user_vertex(context: VertexContext) -> vec3<f32> {
    return context.position;
}
";

const DEFAULT_SURFACE_HOOK: &str = "
fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
    return surface;
}
";

/// GLSL declarations mirroring the WGSL hook structs.
const GLSL_PRELUDE: &str = "#version 450
struct Surface {
    vec3 albedo;
    float alpha;
    vec3 normal;
    float metallic;
    vec3 emissive;
    float roughness;
    vec3 specular;
    float occlusion;
    float shininess;
    float reflectance;
    float shading_model;
    float receive_shadow;
};
struct SurfaceContext {
    vec3 world_position;
    vec3 world_normal;
    vec3 view_direction;
    vec2 uv;
    vec2 screen_uv;
    float time;
    vec4 custom0;
    vec4 custom1;
};
struct VertexContext {
    vec3 position;
    vec3 normal;
    vec2 uv;
    float time;
    vec4 custom0;
    vec4 custom1;
};
";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum ShaderLanguage {
    Wgsl = 0,
    Glsl = 1,
}

impl ShaderLanguage {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => ShaderLanguage::Glsl,
            _ => ShaderLanguage::Wgsl,
        }
    }
}

/// Which pipeline a shader is assembled for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ShaderPass {
    Forward,
    DeferredGBuffer,
}

/// A validated custom shader: the original source and the WGSL hooks the
/// renderer splices into its pipelines.
#[derive(Debug, Clone)]
pub struct CustomShader {
    pub name: String,
    pub language: ShaderLanguage,
    pub source: String,
    /// WGSL hook functions (translated when the source is GLSL).
    pub hooks: String,
    pub(crate) version: u32,
}

impl CustomShader {
    /// Translates (if needed) and validates the hooks against both passes.
    pub fn new(name: impl Into<String>, language: ShaderLanguage, source: impl Into<String>) -> Result<Self> {
        let source = source.into();
        let hooks = match language {
            ShaderLanguage::Wgsl => source.clone(),
            ShaderLanguage::Glsl => translate_glsl(&source)?,
        };
        if !hooks.contains("fn user_vertex") && !hooks.contains("fn user_surface") {
            return Err(Error::InvalidArgument(
                "a custom shader must define user_vertex and/or user_surface".into(),
            ));
        }
        for pass in [ShaderPass::Forward, ShaderPass::DeferredGBuffer] {
            validate(&compose(pass, Some(&hooks)))?;
        }
        Ok(Self {
            name: name.into(),
            language,
            source,
            hooks,
            version: 1,
        })
    }

    #[inline]
    pub fn version(&self) -> u32 {
        self.version
    }
}

/// Assembles the full WGSL source of a pass, with custom hooks when given.
pub fn compose(pass: ShaderPass, hooks: Option<&str>) -> String {
    let hooks = hooks.unwrap_or("");
    let mut source = String::with_capacity(COMMON.len() + MATERIAL.len() + hooks.len() + 4096);
    source.push_str(COMMON);
    source.push('\n');
    source.push_str(MATERIAL);
    source.push('\n');
    source.push_str(hooks);
    source.push('\n');
    if !hooks.contains("fn user_vertex") {
        source.push_str(DEFAULT_VERTEX_HOOK);
    }
    if !hooks.contains("fn user_surface") {
        source.push_str(DEFAULT_SURFACE_HOOK);
    }
    source.push_str(match pass {
        ShaderPass::Forward => FORWARD,
        ShaderPass::DeferredGBuffer => DEFERRED_GBUFFER,
    });
    source
}

/// Parses and validates WGSL, returning a readable error on failure.
pub fn validate(source: &str) -> Result<()> {
    let module = naga::front::wgsl::parse_str(source)
        .map_err(|error| Error::InvalidArgument(format!("WGSL error: {}", error.emit_to_string(source))))?;
    naga::valid::Validator::new(naga::valid::ValidationFlags::all(), naga::valid::Capabilities::all())
        .validate(&module)
        .map_err(|error| Error::InvalidArgument(format!("shader validation error: {}", error.emit_to_string(source))))?;
    Ok(())
}

/// Translates GLSL hook functions to WGSL: the hooks are wrapped in a GLSL 450
/// fragment module, parsed by naga, written back as WGSL, and the helper
/// structs and dummy entry point are stripped again.
pub fn translate_glsl(source: &str) -> Result<String> {
    let wrapped = format!("{GLSL_PRELUDE}\n{source}\nvoid main() {{}}\n");
    let mut frontend = naga::front::glsl::Frontend::default();
    let options = naga::front::glsl::Options::from(naga::ShaderStage::Fragment);
    let module = frontend
        .parse(&options, &wrapped)
        .map_err(|error| Error::InvalidArgument(format!("GLSL error: {}", error.emit_to_string(&wrapped))))?;
    let info = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), naga::valid::Capabilities::all())
        .validate(&module)
        .map_err(|error| Error::InvalidArgument(format!("GLSL validation error: {}", error.emit_to_string(&wrapped))))?;
    let wgsl = naga::back::wgsl::write_string(&module, &info, naga::back::wgsl::WriterFlags::empty())
        .map_err(|error| Error::InvalidArgument(format!("GLSL to WGSL translation failed: {error}")))?;

    let mut hooks = wgsl;
    for (name, fields) in [
        ("Surface", SURFACE_FIELDS.as_slice()),
        ("SurfaceContext", SURFACE_CONTEXT_FIELDS.as_slice()),
        ("VertexContext", VERTEX_CONTEXT_FIELDS.as_slice()),
    ] {
        // naga may rename members (e.g. `custom0` becomes `custom0_`); map the
        // emitted names back to the WGSL struct the hooks will be linked with.
        let header = format!("struct {name} {{");
        if let Some(emitted) = block_body(&hooks, &header) {
            let emitted_names: Vec<String> = emitted
                .split(',')
                .filter_map(|member| member.split(':').next())
                .map(|member| member.trim().to_string())
                .filter(|member| !member.is_empty())
                .collect();
            for (emitted_name, original) in emitted_names.iter().zip(fields.iter()) {
                if emitted_name != original {
                    hooks = hooks.replace(&format!(".{emitted_name}"), &format!(".{original}"));
                }
            }
        }
        hooks = remove_block(&hooks, &header);
    }
    hooks = remove_block(&hooks, "fn main(");
    Ok(hooks.replace("@fragment", ""))
}

const SURFACE_FIELDS: [&str; 12] = [
    "albedo", "alpha", "normal", "metallic", "emissive", "roughness", "specular", "occlusion",
    "shininess", "reflectance", "shading_model", "receive_shadow",
];
const SURFACE_CONTEXT_FIELDS: [&str; 8] = [
    "world_position", "world_normal", "view_direction", "uv", "screen_uv", "time", "custom0", "custom1",
];
const VERTEX_CONTEXT_FIELDS: [&str; 6] = ["position", "normal", "uv", "time", "custom0", "custom1"];

/// Text between the braces of the first block starting with `prefix`.
fn block_body<'a>(source: &'a str, prefix: &str) -> Option<&'a str> {
    let start = source.find(prefix)?;
    let open = start + source[start..].find('{')?;
    let close = open + source[open..].find('}')?;
    Some(&source[open + 1..close])
}

/// Removes the first `{ ... }` block that starts with `prefix`.
fn remove_block(source: &str, prefix: &str) -> String {
    let Some(start) = source.find(prefix) else {
        return source.to_string();
    };
    let Some(open) = source[start..].find('{').map(|i| start + i) else {
        return source.to_string();
    };
    let mut depth = 0;
    for (offset, character) in source[open..].char_indices() {
        match character {
            '{' => depth += 1,
            '}' => {
                depth -= 1;
                if depth == 0 {
                    let end = open + offset + 1;
                    let mut result = String::with_capacity(source.len());
                    result.push_str(&source[..start]);
                    result.push_str(&source[end..]);
                    return result;
                }
            }
            _ => {}
        }
    }
    source.to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn built_in_passes_validate() {
        validate(&compose(ShaderPass::Forward, None)).unwrap();
        validate(&compose(ShaderPass::DeferredGBuffer, None)).unwrap();
    }

    #[test]
    fn wgsl_hooks_are_accepted() {
        let shader = CustomShader::new(
            "stripes",
            ShaderLanguage::Wgsl,
            "fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
                var s = surface;
                s.albedo = vec3<f32>(step(0.5, fract(context.uv.x * context.custom0.x)));
                return s;
            }",
        )
        .unwrap();
        assert!(shader.hooks.contains("user_surface"));
    }

    #[test]
    fn glsl_hooks_are_translated() {
        let shader = CustomShader::new(
            "wave",
            ShaderLanguage::Glsl,
            "vec3 user_vertex(VertexContext context) {
                 return context.position + context.normal * sin(context.time + context.position.x) * context.custom0.x;
             }
             Surface user_surface(SurfaceContext context, Surface surface) {
                 surface.albedo = vec3(1.0, 0.0, 0.0);
                 surface.emissive = context.custom1.rgb;
                 return surface;
             }",
        )
        .unwrap();
        assert!(shader.hooks.contains("fn user_vertex"));
        assert!(shader.hooks.contains("fn user_surface"));
        assert!(!shader.hooks.contains("struct Surface"));
    }

    #[test]
    fn broken_shaders_report_errors() {
        let error = CustomShader::new("broken", ShaderLanguage::Wgsl, "fn user_surface(context: SurfaceContext, surface: Surface) -> Surface { return 1.0; }")
            .unwrap_err()
            .to_string();
        assert!(error.contains("error"), "{error}");
        assert!(CustomShader::new("empty", ShaderLanguage::Wgsl, "fn nothing() {}").is_err());
    }
}
