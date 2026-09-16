// Material and object data, the vertex stage and the surface evaluation that
// turns material parameters and textures into a `Surface`. Custom shaders plug
// in through `user_vertex` and `user_surface`.

struct MaterialData {
    base_color: vec4<f32>,
    // xyz = emissive, w = emissive intensity
    emissive: vec4<f32>,
    // x = metallic, y = roughness, z = reflectance, w = shininess
    params0: vec4<f32>,
    // x = normal scale, y = occlusion strength, z = alpha cutoff, w = shading model
    params1: vec4<f32>,
    // xyz = specular colour, w = alpha mode
    specular: vec4<f32>,
    // xy = uv scale, zw = uv offset
    uv_transform: vec4<f32>,
    // base colour, normal, metallic-roughness, emissive
    texture_flags: vec4<f32>,
    // x = occlusion
    texture_flags2: vec4<f32>,
    // Free parameters for custom shaders.
    custom0: vec4<f32>,
    custom1: vec4<f32>,
};

struct ObjectData {
    model: mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
    // x = receives shadows
    flags: vec4<f32>,
};

@group(1) @binding(0) var<uniform> material: MaterialData;
@group(1) @binding(1) var base_color_texture: texture_2d<f32>;
@group(1) @binding(2) var normal_texture: texture_2d<f32>;
@group(1) @binding(3) var metallic_roughness_texture: texture_2d<f32>;
@group(1) @binding(4) var emissive_texture: texture_2d<f32>;
@group(1) @binding(5) var occlusion_texture: texture_2d<f32>;
@group(1) @binding(6) var material_sampler: sampler;

@group(2) @binding(0) var<uniform> object: ObjectData;

/// Input of the `user_vertex` hook, in object space.
struct VertexContext {
    position: vec3<f32>,
    normal: vec3<f32>,
    uv: vec2<f32>,
    time: f32,
    custom0: vec4<f32>,
    custom1: vec4<f32>,
};

/// Input of the `user_surface` hook, in world space.
struct SurfaceContext {
    world_position: vec3<f32>,
    world_normal: vec3<f32>,
    view_direction: vec3<f32>,
    uv: vec2<f32>,
    screen_uv: vec2<f32>,
    time: f32,
    custom0: vec4<f32>,
    custom1: vec4<f32>,
};

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) tangent: vec4<f32>,
};

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) world_position: vec3<f32>,
    @location(1) world_normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) world_tangent: vec4<f32>,
    @location(4) view_distance: f32,
    // Linear depth along the camera axis, used to pick a shadow cascade.
    @location(5) view_depth: f32,
    @location(6) receive_shadow: f32,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var context: VertexContext;
    context.position = input.position;
    context.normal = input.normal;
    context.uv = input.uv;
    context.time = frame.fog_params.z;
    context.custom0 = material.custom0;
    context.custom1 = material.custom1;
    let local_position = user_vertex(context);

    var out: VertexOutput;
    let world_position = object.model * vec4<f32>(local_position, 1.0);
    out.world_position = world_position.xyz;
    out.clip_position = frame.view_projection * world_position;
    let normal_matrix = mat3x3<f32>(
        object.normal_matrix[0].xyz,
        object.normal_matrix[1].xyz,
        object.normal_matrix[2].xyz,
    );
    out.world_normal = normalize(normal_matrix * input.normal);
    let tangent = normal_matrix * input.tangent.xyz;
    out.world_tangent = vec4<f32>(tangent, input.tangent.w);
    out.uv = input.uv * material.uv_transform.xy + material.uv_transform.zw;
    out.view_distance = length(world_position.xyz - frame.camera_position.xyz);
    out.view_depth = -(frame.view * world_position).z;
    out.receive_shadow = object.flags.x;
    return out;
}

fn shading_normal(input: VertexOutput, front_facing: bool) -> vec3<f32> {
    var normal = normalize(input.world_normal);
    if (!front_facing) {
        normal = -normal;
    }
    if (material.texture_flags.y > 0.5) {
        let tangent_sample = textureSample(normal_texture, material_sampler, input.uv).xyz;
        var tangent_normal = tangent_sample * 2.0 - 1.0;
        tangent_normal = vec3<f32>(tangent_normal.xy * material.params1.x, tangent_normal.z);
        let t = normalize(input.world_tangent.xyz - normal * dot(normal, input.world_tangent.xyz));
        if (length(input.world_tangent.xyz) > 1e-5) {
            let b = cross(normal, t) * input.world_tangent.w;
            normal = normalize(mat3x3<f32>(t, b, normal) * tangent_normal);
        }
    }
    return normal;
}

/// Evaluates the material (and the custom surface hook) for one fragment.
/// Discards alpha masked fragments.
fn material_surface(input: VertexOutput, front_facing: bool) -> Surface {
    var base_color = material.base_color;
    if (material.texture_flags.x > 0.5) {
        base_color = base_color * textureSample(base_color_texture, material_sampler, input.uv);
    }

    var metallic = material.params0.x;
    var roughness = material.params0.y;
    if (material.texture_flags.z > 0.5) {
        let mr = textureSample(metallic_roughness_texture, material_sampler, input.uv);
        roughness = roughness * mr.g;
        metallic = metallic * mr.b;
    }

    var occlusion = 1.0;
    if (material.texture_flags2.x > 0.5) {
        let ao = textureSample(occlusion_texture, material_sampler, input.uv).r;
        occlusion = mix(1.0, ao, material.params1.y);
    }

    var emissive = material.emissive.rgb * material.emissive.w;
    if (material.texture_flags.w > 0.5) {
        emissive = emissive * textureSample(emissive_texture, material_sampler, input.uv).rgb;
    }

    var surface: Surface;
    surface.albedo = base_color.rgb;
    surface.alpha = base_color.a;
    surface.normal = shading_normal(input, front_facing);
    surface.metallic = clamp(metallic, 0.0, 1.0);
    surface.roughness = roughness;
    surface.occlusion = occlusion;
    surface.emissive = emissive;
    surface.specular = material.specular.rgb;
    surface.shininess = material.params0.w;
    surface.reflectance = material.params0.z;
    surface.shading_model = material.params1.w;
    surface.receive_shadow = input.receive_shadow;

    var context: SurfaceContext;
    context.world_position = input.world_position;
    context.world_normal = surface.normal;
    context.view_direction = normalize(frame.camera_position.xyz - input.world_position);
    context.uv = input.uv;
    context.screen_uv = input.clip_position.xy / frame.screen.xy;
    context.time = frame.fog_params.z;
    context.custom0 = material.custom0;
    context.custom1 = material.custom1;
    surface = user_surface(context, surface);
    surface.roughness = clamp(surface.roughness, 0.015, 1.0);
    surface.metallic = clamp(surface.metallic, 0.0, 1.0);

    if (material.specular.w == ALPHA_MASK && surface.alpha < material.params1.z) {
        discard;
    }
    return surface;
}
