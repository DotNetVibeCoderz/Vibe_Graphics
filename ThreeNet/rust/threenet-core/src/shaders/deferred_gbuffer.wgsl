// Deferred renderer, geometry pass: writes the evaluated surface into five
// G-buffer targets. Lighting happens later in `deferred_lighting.wgsl`.

struct GBufferOutput {
    // rgb = albedo, a = metallic
    @location(0) albedo: vec4<f32>,
    // xyz = world normal, w = roughness
    @location(1) normal: vec4<f32>,
    // rgb = emissive, a = occlusion
    @location(2) emissive: vec4<f32>,
    // rgb = Blinn-Phong specular, a = shininess
    @location(3) specular: vec4<f32>,
    // x = reflectance, y = shading model, z = receives shadows, w = coverage
    @location(4) extra: vec4<f32>,
};

@fragment
fn fs_gbuffer(input: VertexOutput, @builtin(front_facing) front_facing: bool) -> GBufferOutput {
    let surface = material_surface(input, front_facing);
    var out: GBufferOutput;
    out.albedo = vec4<f32>(surface.albedo, surface.metallic);
    out.normal = vec4<f32>(surface.normal, surface.roughness);
    out.emissive = vec4<f32>(surface.emissive, surface.occlusion);
    out.specular = vec4<f32>(surface.specular, surface.shininess);
    out.extra = vec4<f32>(surface.reflectance, surface.shading_model, surface.receive_shadow, 1.0);
    return out;
}
