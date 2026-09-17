// Screen space overlay: rounded panels with borders, images and glyphs.

struct OverlayUniform {
    // xy = target size in pixels, z = 1 when the target is sRGB encoded
    screen: vec4<f32>,
};

@group(0) @binding(0) var<uniform> overlay: OverlayUniform;
@group(1) @binding(0) var overlay_texture: texture_2d<f32>;
@group(1) @binding(1) var overlay_sampler: sampler;

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) uv: vec2<f32>,
    @location(2) color: vec4<f32>,
    // xy = pixel offset from the rect centre, zw = half size
    @location(3) local: vec4<f32>,
    @location(4) border_color: vec4<f32>,
    // x = corner radius, y = border width, z = mode (0 panel, 1 image, 2 glyph)
    @location(5) params: vec4<f32>,
};

struct VertexOutput {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) local: vec4<f32>,
    @location(3) border_color: vec4<f32>,
    @location(4) params: vec4<f32>,
};

fn srgb_to_linear(c: vec3<f32>) -> vec3<f32> {
    let low = c / 12.92;
    let high = pow((c + 0.055) / 1.055, vec3<f32>(2.4));
    return select(high, low, c <= vec3<f32>(0.04045));
}

fn linear_to_srgb(c: vec3<f32>) -> vec3<f32> {
    let low = c * 12.92;
    let high = 1.055 * pow(c, vec3<f32>(1.0 / 2.4)) - 0.055;
    return select(high, low, c <= vec3<f32>(0.0031308));
}

@vertex
fn vs_overlay(input: VertexInput) -> VertexOutput {
    var out: VertexOutput;
    let ndc = input.position / overlay.screen.xy * 2.0 - 1.0;
    out.clip = vec4<f32>(ndc.x, -ndc.y, 0.0, 1.0);
    out.uv = input.uv;
    // Colours arrive as sRGB; blend in linear light.
    out.color = vec4<f32>(srgb_to_linear(input.color.rgb), input.color.a);
    out.border_color = vec4<f32>(srgb_to_linear(input.border_color.rgb), input.border_color.a);
    out.local = input.local;
    out.params = input.params;
    return out;
}

// Signed distance to a rounded box of half size `half` and radius `radius`.
fn rounded_box(p: vec2<f32>, half: vec2<f32>, radius: f32) -> f32 {
    let r = min(radius, min(half.x, half.y));
    let q = abs(p) - half + vec2<f32>(r);
    return length(max(q, vec2<f32>(0.0))) + min(max(q.x, q.y), 0.0) - r;
}

@fragment
fn fs_overlay(input: VertexOutput) -> @location(0) vec4<f32> {
    let mode = input.params.z;
    var color = input.color;
    var coverage = 1.0;

    if (mode > 1.5) {
        // Glyph: the atlas holds coverage in the red channel.
        coverage = textureSample(overlay_texture, overlay_sampler, input.uv).r;
    } else {
        let d = rounded_box(input.local.xy, input.local.zw, input.params.x);
        coverage = clamp(0.5 - d, 0.0, 1.0);
        if (mode > 0.5) {
            let texel = textureSample(overlay_texture, overlay_sampler, input.uv);
            color = color * texel;
        }
        let border = input.params.y;
        if (border > 0.0) {
            // Inside the inner edge keeps the fill, the ring gets the border colour.
            let inner = clamp(0.5 - (d + border), 0.0, 1.0);
            color = mix(input.border_color, color, inner);
        }
    }

    var rgb = color.rgb;
    if (overlay.screen.z < 0.5) {
        rgb = linear_to_srgb(rgb);
    }
    let alpha = color.a * coverage;
    return vec4<f32>(rgb * alpha, alpha);
}
