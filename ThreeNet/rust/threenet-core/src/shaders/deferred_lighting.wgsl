// Deferred renderer, lighting pass: a fullscreen triangle that rebuilds each
// surface from the G-buffer and lights it with the shared library.

@group(1) @binding(0) var gbuffer_albedo: texture_2d<f32>;
@group(1) @binding(1) var gbuffer_normal: texture_2d<f32>;
@group(1) @binding(2) var gbuffer_emissive: texture_2d<f32>;
@group(1) @binding(3) var gbuffer_specular: texture_2d<f32>;
@group(1) @binding(4) var gbuffer_extra: texture_2d<f32>;
@group(1) @binding(5) var gbuffer_depth: texture_depth_2d;

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

@fragment
fn fs_lighting(input: FullscreenVertex) -> @location(0) vec4<f32> {
    let pixel = vec2<i32>(input.clip_position.xy);
    let extra = textureLoad(gbuffer_extra, pixel, 0);
    if (extra.w < 0.5) {
        // No geometry: keep the background the target was cleared with.
        discard;
    }

    let albedo = textureLoad(gbuffer_albedo, pixel, 0);
    let normal = textureLoad(gbuffer_normal, pixel, 0);
    let emissive = textureLoad(gbuffer_emissive, pixel, 0);
    let specular = textureLoad(gbuffer_specular, pixel, 0);
    let depth = textureLoad(gbuffer_depth, pixel, 0);

    // World position from the hardware depth.
    let ndc = vec4<f32>(input.uv.x * 2.0 - 1.0, 1.0 - input.uv.y * 2.0, depth, 1.0);
    let world = frame.inverse_view_projection * ndc;
    let world_position = world.xyz / world.w;
    let view_depth = -(frame.view * vec4<f32>(world_position, 1.0)).z;

    var surface: Surface;
    surface.albedo = albedo.rgb;
    surface.alpha = 1.0;
    surface.metallic = albedo.a;
    surface.normal = normalize(normal.xyz);
    surface.roughness = normal.w;
    surface.emissive = emissive.rgb;
    surface.occlusion = emissive.a;
    surface.specular = specular.rgb;
    surface.shininess = specular.a;
    surface.reflectance = extra.x;
    surface.shading_model = extra.y;
    surface.receive_shadow = extra.z;

    let color = shade_surface(surface, world_position, view_depth, input.clip_position.xy);
    return vec4<f32>(apply_fog(color, length(world_position - frame.camera_position.xyz)), 1.0);
}
