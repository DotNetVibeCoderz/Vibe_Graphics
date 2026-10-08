// The sky pass: one fullscreen triangle sitting at the far plane, so it is
// drawn only where no geometry was, without a dome mesh and without the fog or
// depth prepass trouble one brings.
//
// Composed after `common.wgsl`, which already binds the frame uniform and the
// environment map.

struct SkyVertex {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) ndc: vec2<f32>,
};

@vertex
fn vs_sky(@builtin(vertex_index) index: u32) -> SkyVertex {
    let x = f32((index << 1u) & 2u);
    let y = f32(index & 2u);
    var out: SkyVertex;
    out.ndc = vec2<f32>(x * 2.0 - 1.0, y * 2.0 - 1.0);
    // z = 1 is the far plane: the depth test keeps this behind everything.
    out.clip_position = vec4<f32>(out.ndc, 1.0, 1.0);
    return out;
}

/// The view ray through this pixel, in world space.
fn sky_direction(ndc: vec2<f32>) -> vec3<f32> {
    let near = frame.inverse_view_projection * vec4<f32>(ndc.x, ndc.y, 0.0, 1.0);
    let far = frame.inverse_view_projection * vec4<f32>(ndc.x, ndc.y, 1.0, 1.0);
    return normalize((far.xyz / far.w) - (near.xyz / near.w));
}

@fragment
fn fs_sky(input: SkyVertex) -> @location(0) vec4<f32> {
    // `sky_color` lives in common.wgsl, so a material shader can ask for the
    // same sky this pass draws - that is what makes water reflect it.
    return vec4<f32>(sky_color(sky_direction(input.ndc)), 1.0);
}
