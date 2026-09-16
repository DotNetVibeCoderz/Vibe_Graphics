// HDR camera effects that run before bloom and tone mapping:
// bokeh depth of field and camera motion blur.

struct EffectsParams {
    inverse_projection: mat4x4<f32>,
    inverse_view_projection: mat4x4<f32>,
    previous_view_projection: mat4x4<f32>,
    // x = focus distance, y = focus range, z = max blur (pixels), w = unused
    dof: vec4<f32>,
    // x = strength, y = samples, z = max length (uv), w = unused
    motion: vec4<f32>,
    // xy = texel size, zw = target size
    texel: vec4<f32>,
};

@group(0) @binding(0) var color_texture: texture_2d<f32>;
@group(0) @binding(1) var depth_texture: texture_depth_2d;
@group(0) @binding(2) var linear_sampler: sampler;
@group(0) @binding(3) var<uniform> params: EffectsParams;

struct FullscreenVertex {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_fullscreen(@builtin(vertex_index) index: u32) -> FullscreenVertex {
    let x = f32((index << 1u) & 2u);
    let y = f32(index & 2u);
    var out: FullscreenVertex;
    out.uv = vec2<f32>(x, 1.0 - y);
    out.clip_position = vec4<f32>(x * 2.0 - 1.0, y * 2.0 - 1.0, 0.0, 1.0);
    return out;
}

fn hardware_depth(uv: vec2<f32>) -> f32 {
    let size = vec2<f32>(textureDimensions(depth_texture));
    let pixel = clamp(vec2<i32>(uv * size), vec2<i32>(0), vec2<i32>(size) - vec2<i32>(1));
    return textureLoad(depth_texture, pixel, 0);
}

/// Distance along the view axis for a hardware depth sample.
fn linear_depth(uv: vec2<f32>) -> f32 {
    let ndc = vec4<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, hardware_depth(uv), 1.0);
    let view = params.inverse_projection * ndc;
    return -view.z / view.w;
}

/// Circle of confusion radius in pixels.
fn circle_of_confusion(depth: f32) -> f32 {
    let distance = abs(depth - params.dof.x) - params.dof.y * 0.5;
    return clamp(distance / max(params.dof.y, 1e-3), 0.0, 1.0) * params.dof.z;
}

const GOLDEN_ANGLE: f32 = 2.39996323;
const RADIUS_SCALE: f32 = 1.0;

// Single pass "scatter as gather" bokeh (after Dennis Gustafsson): samples on a
// golden angle spiral, each accepted when its own blur reaches the centre.
@fragment
fn fs_dof(input: FullscreenVertex) -> @location(0) vec4<f32> {
    let centre_depth = linear_depth(input.uv);
    let centre_size = circle_of_confusion(centre_depth);
    var color = textureSampleLevel(color_texture, linear_sampler, input.uv, 0.0).rgb;
    var total = 1.0;
    var radius = RADIUS_SCALE;
    var angle = 0.0;

    for (var i: i32 = 0; i < 96; i = i + 1) {
        if (radius >= params.dof.z) {
            break;
        }
        let offset = vec2<f32>(cos(angle), sin(angle)) * params.texel.xy * radius;
        let uv = input.uv + offset;
        let sample_color = textureSampleLevel(color_texture, linear_sampler, uv, 0.0).rgb;
        let sample_depth = linear_depth(uv);
        var sample_size = circle_of_confusion(sample_depth);
        if (sample_depth > centre_depth) {
            // Background behind an in-focus centre must not bleed over it.
            sample_size = clamp(sample_size, 0.0, centre_size * 2.0);
        }
        let weight = smoothstep(radius - 0.5, radius + 0.5, sample_size);
        color = color + mix(color / total, sample_color, weight);
        total = total + 1.0;
        radius = radius + RADIUS_SCALE / radius;
        angle = angle + GOLDEN_ANGLE;
    }

    return vec4<f32>(color / total, 1.0);
}

// Camera motion blur: reproject each pixel into the previous frame and smear
// along the screen space motion.
@fragment
fn fs_motion(input: FullscreenVertex) -> @location(0) vec4<f32> {
    let depth = hardware_depth(input.uv);
    let ndc = vec4<f32>(input.uv.x * 2.0 - 1.0, 1.0 - input.uv.y * 2.0, depth, 1.0);
    var world = params.inverse_view_projection * ndc;
    world = world / world.w;
    let previous = params.previous_view_projection * world;
    let previous_uv = vec2<f32>(previous.x / previous.w * 0.5 + 0.5, 0.5 - previous.y / previous.w * 0.5);

    var velocity = (input.uv - previous_uv) * params.motion.x;
    let length_uv = length(velocity);
    if (length_uv > params.motion.z) {
        velocity = velocity * (params.motion.z / length_uv);
    }

    let samples = max(i32(params.motion.y), 2);
    var color = vec3<f32>(0.0);
    for (var i: i32 = 0; i < samples; i = i + 1) {
        let t = f32(i) / f32(samples - 1) - 0.5;
        color = color + textureSampleLevel(color_texture, linear_sampler, input.uv - velocity * t, 0.0).rgb;
    }
    return vec4<f32>(color / f32(samples), 1.0);
}
