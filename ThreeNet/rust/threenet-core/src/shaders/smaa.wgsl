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
