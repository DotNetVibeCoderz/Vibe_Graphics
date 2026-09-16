// Forward pass: evaluate the material and light it in one go.

@fragment
fn fs_main(input: VertexOutput, @builtin(front_facing) front_facing: bool) -> @location(0) vec4<f32> {
    let surface = material_surface(input, front_facing);
    let color = shade_surface(surface, input.world_position, input.view_depth, input.clip_position.xy);
    return vec4<f32>(apply_fog(color, input.view_distance), surface.alpha);
}
