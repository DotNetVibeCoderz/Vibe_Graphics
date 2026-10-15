// Post-processing chain: bright pass, separable gaussian blur and the final
// tone mapping composite. All passes render a single full screen triangle.

struct PostParams {
    // x = exposure, y = tone mapping operator, z = bloom intensity, w = bloom threshold
    params: vec4<f32>,
    // xy = source texel size, zw = blur direction
    texel: vec4<f32>,
    // x = vignette strength, y = vignette softness, z = film grain, w = seconds
    grade: vec4<f32>,
    // x = saturation, y = contrast, z = brightness, w = colour LUT strength
    grade2: vec4<f32>,
};

@group(0) @binding(0) var source_texture: texture_2d<f32>;
@group(0) @binding(1) var source_sampler: sampler;
@group(0) @binding(2) var<uniform> post: PostParams;
@group(0) @binding(3) var bloom_texture: texture_2d<f32>;
// A colour lookup table laid out as a strip: `size` slices of `size` by `size`
// side by side, so a 32 step table is a 1024 by 32 image. Only read when the
// strength above is positive; otherwise this binding holds whatever was
// convenient and the shader never touches it.
@group(0) @binding(4) var lut_texture: texture_2d<f32>;

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

/// Looks a graded colour up in a strip LUT. The blue channel picks the slice
/// and is interpolated between the two nearest by hand, because a 2D sampler
/// would blend across the seam into the neighbouring slice.
fn apply_lut(color: vec3<f32>, strength: f32) -> vec3<f32> {
    let dimensions = vec2<f32>(textureDimensions(lut_texture));
    let size = dimensions.y;
    if (size < 2.0) {
        return color;
    }
    let c = clamp(color, vec3<f32>(0.0), vec3<f32>(1.0));
    let last = size - 1.0;

    // Half a texel in from each end of a slice, so the ends are not blurred
    // into whatever is next to them.
    let u = (c.r * last + 0.5) / dimensions.x;
    let v = (c.g * last + 0.5) / size;
    let slice = c.b * last;
    let low = floor(slice);
    let high = min(low + 1.0, last);

    let a = textureSampleLevel(lut_texture, source_sampler, vec2<f32>(u + low / size, v), 0.0).rgb;
    let b = textureSampleLevel(lut_texture, source_sampler, vec2<f32>(u + high / size, v), 0.0).rgb;
    return mix(color, mix(a, b, slice - low), clamp(strength, 0.0, 1.0));
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

    // The lookup table is the last thing the colour passes through, so it sees
    // exactly what the saturation and contrast left - which is what a table
    // authored against a screenshot was authored against.
    let lut_strength = post.grade2.w;
    if (lut_strength > 0.0) {
        color = apply_lut(color, lut_strength);
    }

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


/// Perceptual weight of a colour, for the edge test.
fn fxaa_luma(color: vec3<f32>) -> f32 {
    return dot(color, vec3<f32>(0.299, 0.587, 0.114));
}

/// FXAA 3.11, the quick version: find an edge from the luma of the four
/// diagonal neighbours, work out which way it runs, and blur along it.
///
/// This is the only antialiasing the deferred path can have, because MSAA needs
/// a multisampled target and the G-buffer is resolved before the lighting ever
/// sees it. Forward rendering keeps MSAA; this is for when that is not there or
/// not enough.
@fragment
fn fs_fxaa(input: VertexOutput) -> @location(0) vec4<f32> {
    let texel = post.texel.xy;
    let center = textureSample(source_texture, source_sampler, input.uv);

    let luma_m = fxaa_luma(center.rgb);
    let luma_nw = fxaa_luma(textureSample(source_texture, source_sampler, input.uv + vec2<f32>(-texel.x, -texel.y)).rgb);
    let luma_ne = fxaa_luma(textureSample(source_texture, source_sampler, input.uv + vec2<f32>(texel.x, -texel.y)).rgb);
    let luma_sw = fxaa_luma(textureSample(source_texture, source_sampler, input.uv + vec2<f32>(-texel.x, texel.y)).rgb);
    let luma_se = fxaa_luma(textureSample(source_texture, source_sampler, input.uv + vec2<f32>(texel.x, texel.y)).rgb);

    let luma_min = min(luma_m, min(min(luma_nw, luma_ne), min(luma_sw, luma_se)));
    let luma_max = max(luma_m, max(max(luma_nw, luma_ne), max(luma_sw, luma_se)));

    // Flat enough to leave alone. Without the absolute floor, dark areas where
    // the relative test is meaningless get smeared.
    let range = luma_max - luma_min;
    if (range < max(0.0312, luma_max * 0.125)) {
        return center;
    }

    // The edge runs perpendicular to the luma gradient across the diagonals.
    var direction = vec2<f32>(
        -((luma_nw + luma_ne) - (luma_sw + luma_se)),
        ((luma_nw + luma_sw) - (luma_ne + luma_se)),
    );
    // Keep a nearly axis aligned edge from producing a huge step.
    let reduce = max((luma_nw + luma_ne + luma_sw + luma_se) * 0.03125, 0.0078125);
    let scale = 1.0 / (min(abs(direction.x), abs(direction.y)) + reduce);
    direction = clamp(direction * scale, vec2<f32>(-8.0), vec2<f32>(8.0)) * texel;

    // Two taps inside the span, then two at its ends.
    let inner = 0.5 * (
        textureSample(source_texture, source_sampler, input.uv + direction * (1.0 / 3.0 - 0.5)).rgb
        + textureSample(source_texture, source_sampler, input.uv + direction * (2.0 / 3.0 - 0.5)).rgb
    );
    let outer = inner * 0.5 + 0.25 * (
        textureSample(source_texture, source_sampler, input.uv - direction * 0.5).rgb
        + textureSample(source_texture, source_sampler, input.uv + direction * 0.5).rgb
    );

    // The wider blur is better unless it wandered off the edge entirely.
    let luma_outer = fxaa_luma(outer);
    if (luma_outer < luma_min || luma_outer > luma_max) {
        return vec4<f32>(inner, center.a);
    }
    return vec4<f32>(outer, center.a);
}

// Subpixel morphological antialiasing, in three passes.
//
// 1. Find the edges, by luma, with local contrast adaptation so that an edge
//    sitting next to a much stronger one is not treated as one.
// 2. For every edge pixel, walk along the edge to both ends of its run, work
//    out how the run terminates, and from that reconstruct where the real
//    geometric line crossed the pixel. The fraction of the pixel on each side
//    of that line is the blend weight.
// 3. Blend each pixel with the neighbour its weight points at.
//
// Pass two is where this differs from the reference SMAA, and deliberately.
// The reference looks the coverage area up in a precomputed table (AreaTex),
// and accelerates the run search with a second table (SearchTex). Both tables
// are tabulations of arithmetic that a modern GPU can simply do: the area of a
// pixel under a straight line. Computing it inline costs a few instructions,
// removes the quantisation the table introduces, and - the reason that matters
// here - leaves nothing that can be subtly wrong in a way no test would catch.
//
// Not included: diagonal pattern detection and corner rounding. Both are
// optional in the reference too (SMAA_DISABLE_DIAG_DETECTION,
// SMAA_DISABLE_CORNER_DETECTION), and both want their own tables.

const SMAA_THRESHOLD: f32 = 0.05;
const SMAA_SEARCH_STEPS: i32 = 16;
// Below this an "edge" is contrast inside a texture rather than a silhouette.
const SMAA_CONTRAST_FACTOR: f32 = 2.0;

fn smaa_luma(colour: vec3<f32>) -> f32 {
    return dot(colour, vec3<f32>(0.2126, 0.7152, 0.0722));
}

fn smaa_sample_luma(uv: vec2<f32>, offset: vec2<f32>) -> f32 {
    return smaa_luma(textureSampleLevel(source_texture, source_sampler, uv + offset, 0.0).rgb);
}

// ------------------------------------------------------------ pass 1: edges

@fragment
fn fs_smaa_edges(input: VertexOutput) -> @location(0) vec4<f32> {
    let texel = post.texel.xy;
    let uv = input.uv;

    let here = smaa_sample_luma(uv, vec2<f32>(0.0));
    let left = smaa_sample_luma(uv, vec2<f32>(-texel.x, 0.0));
    let top = smaa_sample_luma(uv, vec2<f32>(0.0, -texel.y));

    let delta = abs(vec2<f32>(here - left, here - top));
    var edges = step(vec2<f32>(SMAA_THRESHOLD), delta);
    if (edges.x + edges.y == 0.0) {
        return vec4<f32>(0.0, 0.0, 0.0, 1.0);
    }

    // What the neighbourhood's strongest edge is. A step that is weak next to
    // the one beside it is detail, not a silhouette, and rounding it off is how
    // morphological antialiasing smears texture.
    let right = smaa_sample_luma(uv, vec2<f32>(texel.x, 0.0));
    let bottom = smaa_sample_luma(uv, vec2<f32>(0.0, texel.y));
    let far_left = smaa_sample_luma(uv, vec2<f32>(-2.0 * texel.x, 0.0));
    let far_top = smaa_sample_luma(uv, vec2<f32>(0.0, -2.0 * texel.y));

    let others = max(
        abs(vec2<f32>(here - right, here - bottom)),
        abs(vec2<f32>(left - far_left, top - far_top)),
    );
    let strongest = max(max(delta.x, delta.y), max(others.x, others.y));
    edges = edges * step(vec2<f32>(strongest / SMAA_CONTRAST_FACTOR), delta);

    return vec4<f32>(edges, 0.0, 1.0);
}

// ---------------------------------------------------------- pass 2: weights

fn smaa_edge(uv: vec2<f32>) -> vec2<f32> {
    return textureSampleLevel(source_texture, source_sampler, uv, 0.0).rg;
}

/// How far a horizontal run continues in `direction`, in pixels.
fn smaa_search_horizontal(uv: vec2<f32>, texel: vec2<f32>, direction: f32) -> f32 {
    var distance = 0.0;
    for (var i = 1; i <= SMAA_SEARCH_STEPS; i = i + 1) {
        let at = uv + vec2<f32>(direction * f32(i) * texel.x, 0.0);
        if (at.x < 0.0 || at.x > 1.0) {
            break;
        }
        if (smaa_edge(at).g < 0.5) {
            break;
        }
        distance = f32(i);
    }
    return distance;
}

/// The same along a column.
fn smaa_search_vertical(uv: vec2<f32>, texel: vec2<f32>, direction: f32) -> f32 {
    var distance = 0.0;
    for (var i = 1; i <= SMAA_SEARCH_STEPS; i = i + 1) {
        let at = uv + vec2<f32>(0.0, direction * f32(i) * texel.y);
        if (at.y < 0.0 || at.y > 1.0) {
            break;
        }
        if (smaa_edge(at).r < 0.5) {
            break;
        }
        distance = f32(i);
    }
    return distance;
}

/// The area of one pixel lying under the reconstructed line.
///
/// The run is `length` pixels long and the pixel sits `position` along it. The
/// line falls from half a pixel of coverage at the end that steps to nothing at
/// the far end, so the coverage of one pixel is the line's average height
/// across it. This is exactly what the reference tabulates in AreaTex.
fn smaa_area(position: f32, length: f32) -> f32 {
    if (length <= 0.0) {
        return 0.0;
    }
    return max(0.5 * (1.0 - (position + 0.5) / length), 0.0);
}

@fragment
fn fs_smaa_weights(input: VertexOutput) -> @location(0) vec4<f32> {
    let texel = post.texel.xy;
    let uv = input.uv;
    let edges = smaa_edge(uv);

    // r = blend with the pixel above, g = below, b = left, a = right.
    var weights = vec4<f32>(0.0);

    if (edges.g > 0.5) {
        // A horizontal edge: walk the run and see how it ends.
        let left = smaa_search_horizontal(uv, texel, -1.0);
        let right = smaa_search_horizontal(uv, texel, 1.0);
        let length = left + right + 1.0;

        // A crossing edge at an end is the step the silhouette takes there.
        // Which side of the row it sits on says which way the line goes.
        let left_end = uv + vec2<f32>(-(left + 1.0) * texel.x, 0.0);
        let right_end = uv + vec2<f32>((right + 1.0) * texel.x, 0.0);
        let steps_up = smaa_edge(left_end + vec2<f32>(0.0, -texel.y)).r
            + smaa_edge(right_end + vec2<f32>(0.0, -texel.y)).r;
        let steps_down = smaa_edge(left_end).r + smaa_edge(right_end).r;

        // The pixel is nearer one end than the other; the ramp that reaches it
        // is the one from the nearer end.
        let coverage = max(smaa_area(left, length), smaa_area(right, length));
        if (steps_up >= steps_down) {
            weights.r = coverage;
        } else {
            weights.g = coverage;
        }
    }

    if (edges.r > 0.5) {
        // A vertical edge: the same walk, turned ninety degrees.
        let up = smaa_search_vertical(uv, texel, -1.0);
        let down = smaa_search_vertical(uv, texel, 1.0);
        let length = up + down + 1.0;

        let top_end = uv + vec2<f32>(0.0, -(up + 1.0) * texel.y);
        let bottom_end = uv + vec2<f32>(0.0, (down + 1.0) * texel.y);
        let steps_left = smaa_edge(top_end + vec2<f32>(-texel.x, 0.0)).g
            + smaa_edge(bottom_end + vec2<f32>(-texel.x, 0.0)).g;
        let steps_right = smaa_edge(top_end).g + smaa_edge(bottom_end).g;

        let coverage = max(smaa_area(up, length), smaa_area(down, length));
        if (steps_left >= steps_right) {
            weights.b = coverage;
        } else {
            weights.a = coverage;
        }
    }

    return weights;
}

// ------------------------------------------------------------ pass 3: blend

@fragment
fn fs_smaa_blend(input: VertexOutput) -> @location(0) vec4<f32> {
    let texel = post.texel.xy;
    let uv = input.uv;
    // bloom_texture carries the weights here; the composite layout is reused
    // rather than given a fourth shape for one pass.
    let weights = textureSampleLevel(bloom_texture, source_sampler, uv, 0.0);
    let total = weights.r + weights.g + weights.b + weights.a;

    let here = textureSampleLevel(source_texture, source_sampler, uv, 0.0);
    if (total <= 0.0) {
        return here;
    }

    let above = textureSampleLevel(source_texture, source_sampler, uv + vec2<f32>(0.0, -texel.y), 0.0);
    let below = textureSampleLevel(source_texture, source_sampler, uv + vec2<f32>(0.0, texel.y), 0.0);
    let left = textureSampleLevel(source_texture, source_sampler, uv + vec2<f32>(-texel.x, 0.0), 0.0);
    let right = textureSampleLevel(source_texture, source_sampler, uv + vec2<f32>(texel.x, 0.0), 0.0);

    // The weights say how much of each neighbour replaces this pixel, so what
    // is left over is how much of the pixel survives.
    let blended = here.rgb * (1.0 - min(total, 1.0))
        + above.rgb * weights.r
        + below.rgb * weights.g
        + left.rgb * weights.b
        + right.rgb * weights.a;
    return vec4<f32>(blended, here.a);
}
