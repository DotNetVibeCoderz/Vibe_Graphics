// SSAO blur: depth aware separable blur that keeps edges crisp.

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

struct BlurParams {
    // xy = texel step, z = depth sharpness
    params: vec4<f32>,
};

@group(0) @binding(0) var ao_texture: texture_2d<f32>;
@group(0) @binding(1) var linear_sampler: sampler;
@group(0) @binding(2) var depth_texture: texture_2d<f32>;
@group(0) @binding(3) var<uniform> blur: BlurParams;

@fragment
fn fs_blur(input: FullscreenVertex) -> @location(0) vec4<f32> {
    let center_depth = textureSampleLevel(depth_texture, linear_sampler, input.uv, 0.0).a;
    var total = 0.0;
    var weight_sum = 0.0;
    for (var i: i32 = -3; i <= 3; i = i + 1) {
        let uv = input.uv + blur.params.xy * f32(i);
        let depth = textureSampleLevel(depth_texture, linear_sampler, uv, 0.0).a;
        let spatial = exp(-f32(i * i) / 8.0);
        // Samples across a depth discontinuity barely contribute, so AO does not bleed over edges.
        let range = exp(-abs(depth - center_depth) * blur.params.z / max(center_depth, 1e-3));
        let weight = spatial * range;
        total = total + textureSampleLevel(ao_texture, linear_sampler, uv, 0.0).r * weight;
        weight_sum = weight_sum + weight;
    }
    let ao = total / max(weight_sum, 1e-4);
    return vec4<f32>(ao, ao, ao, 1.0);
}
