// Depth-only pass that renders shadow casters into one shadow map layer.

struct ShadowPass {
    view_projection: mat4x4<f32>,
};

struct ObjectData {
    model: mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
    flags: vec4<f32>,
};

@group(0) @binding(0) var<uniform> shadow_pass: ShadowPass;
@group(1) @binding(0) var<uniform> object: ObjectData;

@vertex
fn vs_shadow(@location(0) position: vec3<f32>) -> @builtin(position) vec4<f32> {
    return shadow_pass.view_projection * object.model * vec4<f32>(position, 1.0);
}
