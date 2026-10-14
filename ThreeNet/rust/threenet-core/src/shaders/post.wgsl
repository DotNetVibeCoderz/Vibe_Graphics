// Post-processing chain: bright pass, separable gaussian blur and the final
// tone mapping composite. All passes render a single full screen triangle.

struct PostParams {
    // x = exposure, y = tone mapping operator, z = bloom intensity, w = bloom threshold
    params: vec4<f32>,
    // xy = source texel size, zw = blur direction
    texel: vec4<f32>,
    // x = vignette strength, y = vignette softness, z = film grain, w = seconds
    grade: vec4<f32>,
    // x = saturation, y = contrast, z = brightness, w reserved
    grade2: vec4<f32>,
};

@group(0) @binding(0) var source_texture: texture_2d<f32>;
@group(0) @binding(1) var source_sampler: sampler;
@group(0) @binding(2) var<uniform> post: PostParams;
@group(0) @binding(3) var bloom_texture: texture_2d<f32>;

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_fullscreen(@builtin(vertex_index) index: u32) -> VertexOutput {
    // Oversized triangle covering the viewport without a vertex buffer.
    let x = f32((index << 1u) & 2u);
    let y = f32(index & 2u);
    var out: VertexOutput;
    out.uv = vec2<f32>(x, 1.0 - y);
    out.clip_position = vec4<f32>(x * 2.0 - 1.0, y * 2.0 - 1.0, 0.0, 1.0);
    return out;
}

@fragment
fn fs_threshold(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = textureSample(source_texture, source_sampler, input.uv).rgb;
    let luminance = dot(color, vec3<f32>(0.2126, 0.7152, 0.0722));
    let threshold = post.params.w;
    // Soft knee so the bloom ramps in instead of popping.
    let knee = max(threshold * 0.5, 1e-4);
    let soft = clamp((luminance - threshold + knee) / (2.0 * knee), 0.0, 1.0);
    let contribution = max(soft * soft * knee, max(luminance - threshold, 0.0));
    return vec4<f32>(color * (contribution / max(luminance, 1e-4)), 1.0);
}

@fragment
fn fs_blur(input: VertexOutput) -> @location(0) vec4<f32> {
    // 9 tap gaussian, normalised weights.
    let weights = array<f32, 5>(0.227027, 0.194594, 0.121621, 0.054054, 0.016216);
    let step = post.texel.xy * post.texel.zw;
    var result = textureSample(source_texture, source_sampler, input.uv).rgb * weights[0];
    for (var i: i32 = 1; i < 5; i = i + 1) {
        let offset = step * f32(i);
        result = result + textureSample(source_texture, source_sampler, input.uv + offset).rgb * weights[i];
        result = result + textureSample(source_texture, source_sampler, input.uv - offset).rgb * weights[i];
    }
    return vec4<f32>(result, 1.0);
}

// Plain copy, used to build mip chains by downsampling level by level.
@fragment
fn fs_copy(input: VertexOutput) -> @location(0) vec4<f32> {
    return textureSample(source_texture, source_sampler, input.uv);
}

fn tonemap_reinhard(color: vec3<f32>) -> vec3<f32> {
    return color / (color + vec3<f32>(1.0));
}

// Narkowicz ACES approximation.
fn tonemap_aces(color: vec3<f32>) -> vec3<f32> {
    let a = 2.51;
    let b = 0.03;
    let c = 2.43;
    let d = 0.59;
    let e = 0.14;
    return clamp((color * (a * color + b)) / (color * (c * color + d) + e), vec3<f32>(0.0), vec3<f32>(1.0));
}

// Uncharted 2 filmic curve.
fn tonemap_filmic(color: vec3<f32>) -> vec3<f32> {
    let a = 0.15;
    let b = 0.50;
    let c = 0.10;
    let d = 0.20;
    let e = 0.02;
    let f = 0.30;
    let w = 11.2;
    let curve = ((color * (a * color + c * b) + d * e) / (color * (a * color + b) + d * f)) - e / f;
    let white = ((vec3<f32>(w) * (a * vec3<f32>(w) + c * b) + d * e) / (vec3<f32>(w) * (a * vec3<f32>(w) + b) + d * f)) - e / f;
    return clamp(curve / white, vec3<f32>(0.0), vec3<f32>(1.0));
}

/// A hash with no pattern the eye can latch onto, for the grain.
fn post_hash(p: vec2<f32>) -> f32 {
    return fract(sin(dot(p, vec2<f32>(12.9898, 78.233))) * 43758.5453);
}

/// Saturation, contrast and brightness, applied after tone mapping where the
/// values are the ones that reach the screen.
fn grade_color(color: vec3<f32>, saturation: f32, contrast: f32, brightness: f32) -> vec3<f32> {
    // Rec. 709 luma, so desaturating keeps the apparent brightness.
    let luma = dot(color, vec3<f32>(0.2126, 0.7152, 0.0722));
    var out = mix(vec3<f32>(luma), color, saturation);
    // Contrast pivots on middle grey, not on black, or raising it would just
    // brighten everything.
    out = (out - vec3<f32>(0.5)) * contrast + vec3<f32>(0.5);
    return out + vec3<f32>(brightness);
}

@fragment
fn fs_composite(input: VertexOutput) -> @location(0) vec4<f32> {
    let scene = textureSample(source_texture, source_sampler, input.uv);
    var color = scene.rgb;
    let bloom_intensity = post.params.z;
    if (bloom_intensity > 0.0) {
        color = color + textureSample(bloom_texture, source_sampler, input.uv).rgb * bloom_intensity;
    }
    color = color * post.params.x;

    let tone_op = post.params.y;
    if (tone_op == 1.0) {
        color = tonemap_reinhard(color);
    } else if (tone_op == 2.0) {
        color = tonemap_aces(color);
    } else if (tone_op == 3.0) {
        color = tonemap_filmic(color);
    } else {
        color = clamp(color, vec3<f32>(0.0), vec3<f32>(1.0));
    }

    // Grading, the vignette and the grain all come after tone mapping, because
    // they are things a lens and a film stock do, not things the scene does.
    color = grade_color(color, post.grade2.x, post.grade2.y, post.grade2.z);

    let vignette = post.grade.x;
    if (vignette > 0.0) {
        // Distance from the centre, squared off so the corners darken first.
        let offset = (input.uv - vec2<f32>(0.5)) * 2.0;
        let radius = length(offset) * 0.7071;
        let softness = clamp(post.grade.y, 0.01, 1.0);
        let shade = 1.0 - smoothstep(1.0 - softness, 1.0, radius) * vignette;
        color = color * clamp(shade, 0.0, 1.0);
    }

    let grain = post.grade.z;
    if (grain > 0.0) {
        // Moved by time, or it reads as dirt on the lens rather than grain.
        let noise = post_hash(input.uv * vec2<f32>(1024.0, 1024.0) + vec2<f32>(post.grade.w * 37.0)) - 0.5;
        color = color + vec3<f32>(noise * grain);
    }

    return vec4<f32>(clamp(color, vec3<f32>(0.0), vec3<f32>(1.0)), scene.a);
}

