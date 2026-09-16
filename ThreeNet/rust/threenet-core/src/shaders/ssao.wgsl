// SSAO occlusion pass: hemisphere sampling around each pixel.

const MAX_SSAO_SAMPLES: u32 = 32u;

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

struct SsaoParams {
    projection: mat4x4<f32>,
    kernel: array<vec4<f32>, MAX_SSAO_SAMPLES>,
    // x = radius, y = bias, z = intensity, w = sample count
    params: vec4<f32>,
    // xy = noise uv scale, zw = AO texel size
    noise: vec4<f32>,
};

@group(0) @binding(0) var gbuffer_texture: texture_2d<f32>;
@group(0) @binding(1) var point_sampler: sampler;
@group(0) @binding(2) var noise_texture: texture_2d<f32>;
@group(0) @binding(3) var repeat_sampler: sampler;
@group(0) @binding(4) var<uniform> ssao: SsaoParams;

// Rebuilds the view space position from a uv and a linear depth, for both
// perspective and orthographic projections.
fn view_position_from_depth(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let ndc = vec2<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    let p = ssao.projection;
    if (p[3][3] > 0.5) {
        return vec3<f32>((ndc.x - p[3][0]) / p[0][0], (ndc.y - p[3][1]) / p[1][1], -depth);
    }
    return vec3<f32>(ndc.x * depth / p[0][0], ndc.y * depth / p[1][1], -depth);
}

@fragment
fn fs_ssao(input: FullscreenVertex) -> @location(0) vec4<f32> {
    let g = textureSampleLevel(gbuffer_texture, point_sampler, input.uv, 0.0);
    let depth = g.a;
    if (depth <= 0.0) {
        // Background: nothing to occlude.
        return vec4<f32>(1.0);
    }

    let position = view_position_from_depth(input.uv, depth);
    let normal = normalize(g.xyz);
    let random = normalize(textureSampleLevel(noise_texture, repeat_sampler, input.uv * ssao.noise.xy, 0.0).xyz * 2.0 - 1.0);

    // Gram-Schmidt: a tangent frame randomly rotated around the normal.
    let tangent = normalize(random - normal * dot(random, normal));
    let bitangent = cross(normal, tangent);
    let tbn = mat3x3<f32>(tangent, bitangent, normal);

    let radius = ssao.params.x;
    let bias = ssao.params.y;
    let count = min(u32(ssao.params.w), MAX_SSAO_SAMPLES);
    var occlusion = 0.0;

    for (var i: u32 = 0u; i < count; i = i + 1u) {
        let sample_position = position + (tbn * ssao.kernel[i].xyz) * radius;
        let clip = ssao.projection * vec4<f32>(sample_position, 1.0);
        let ndc = clip.xy / clip.w;
        let sample_uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        if (any(sample_uv < vec2<f32>(0.0)) || any(sample_uv > vec2<f32>(1.0))) {
            continue;
        }

        let scene_depth = textureSampleLevel(gbuffer_texture, point_sampler, sample_uv, 0.0).a;
        if (scene_depth <= 0.0) {
            continue;
        }

        let scene_z = -scene_depth;
        // Ignore occluders far outside the hemisphere (range check).
        let range = smoothstep(0.0, 1.0, radius / max(abs(position.z - scene_z), 1e-4));
        if (scene_z >= sample_position.z + bias) {
            occlusion = occlusion + range;
        }
    }

    let ao = pow(clamp(1.0 - occlusion / f32(max(count, 1u)), 0.0, 1.0), ssao.params.z);
    return vec4<f32>(ao, ao, ao, 1.0);
}
