// SSAO prepass: view space normal (rgb) and linear view depth (a).

struct Frame {
    view: mat4x4<f32>,
    projection: mat4x4<f32>,
    view_projection: mat4x4<f32>,
    inverse_view_projection: mat4x4<f32>,
    camera_position: vec4<f32>,
    ambient: vec4<f32>,
    fog_color: vec4<f32>,
    fog_params: vec4<f32>,
    misc: vec4<f32>,
    screen: vec4<f32>,
};

struct ObjectData {
    model: mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
    flags: vec4<f32>,
};

@group(0) @binding(0) var<uniform> frame: Frame;
@group(1) @binding(0) var<uniform> object: ObjectData;

struct GBufferVertex {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) view_normal: vec3<f32>,
    @location(1) view_depth: f32,
};

@vertex
fn vs_gbuffer(@location(0) position: vec3<f32>, @location(1) normal: vec3<f32>) -> GBufferVertex {
    var out: GBufferVertex;
    let world = object.model * vec4<f32>(position, 1.0);
    let view_position = frame.view * world;
    out.clip_position = frame.projection * view_position;
    let normal_matrix = mat3x3<f32>(
        object.normal_matrix[0].xyz,
        object.normal_matrix[1].xyz,
        object.normal_matrix[2].xyz,
    );
    out.view_normal = (frame.view * vec4<f32>(normal_matrix * normal, 0.0)).xyz;
    out.view_depth = -view_position.z;
    return out;
}

@fragment
fn fs_gbuffer(input: GBufferVertex, @builtin(front_facing) front_facing: bool) -> @location(0) vec4<f32> {
    var normal = normalize(input.view_normal);
    if (!front_facing) {
        normal = -normal;
    }
    return vec4<f32>(normal, input.view_depth);
}
