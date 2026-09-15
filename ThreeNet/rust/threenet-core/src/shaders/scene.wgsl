// Three.Net uber shader: basic (unlit), lambert, blinn-phong and metallic
// roughness PBR share one pipeline and branch on the material shading model.
// Made by Gravicode Studios - led by Kang Fadhil.

const PI: f32 = 3.141592653589793;
const MAX_LIGHTS: u32 = 64u;

const SHADING_BASIC: f32 = 0.0;
const SHADING_LAMBERT: f32 = 1.0;
const SHADING_PHONG: f32 = 2.0;
const SHADING_PBR: f32 = 3.0;

const LIGHT_DIRECTIONAL: f32 = 0.0;
const LIGHT_POINT: f32 = 1.0;
const LIGHT_SPOT: f32 = 2.0;
const LIGHT_AREA: f32 = 3.0;
const LIGHT_AMBIENT: f32 = 4.0;

const ALPHA_MASK: f32 = 1.0;

struct Frame {
    view: mat4x4<f32>,
    projection: mat4x4<f32>,
    view_projection: mat4x4<f32>,
    inverse_view_projection: mat4x4<f32>,
    // xyz = camera position, w = exposure
    camera_position: vec4<f32>,
    // xyz = ambient colour, w = ambient intensity
    ambient: vec4<f32>,
    // xyz = fog colour, w = fog density
    fog_color: vec4<f32>,
    // x = fog start, y = fog end, z = time, w = environment intensity
    fog_params: vec4<f32>,
    // x = light count, y = has environment map, z = near, w = far
    misc: vec4<f32>,
};

struct Light {
    // xyz = position, w = kind
    position: vec4<f32>,
    // xyz = direction, w = range
    direction: vec4<f32>,
    // xyz = colour, w = intensity
    color: vec4<f32>,
    // x = cos(inner), y = cos(outer), z = width, w = height
    params: vec4<f32>,
};

struct LightBuffer {
    lights: array<Light, MAX_LIGHTS>,
};

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
};

struct ObjectData {
    model: mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<uniform> light_buffer: LightBuffer;
@group(0) @binding(2) var environment_texture: texture_2d<f32>;
@group(0) @binding(3) var environment_sampler: sampler;

@group(1) @binding(0) var<uniform> material: MaterialData;
@group(1) @binding(1) var base_color_texture: texture_2d<f32>;
@group(1) @binding(2) var normal_texture: texture_2d<f32>;
@group(1) @binding(3) var metallic_roughness_texture: texture_2d<f32>;
@group(1) @binding(4) var emissive_texture: texture_2d<f32>;
@group(1) @binding(5) var occlusion_texture: texture_2d<f32>;
@group(1) @binding(6) var material_sampler: sampler;

@group(2) @binding(0) var<uniform> object: ObjectData;

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
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var out: VertexOutput;
    let world_position = object.model * vec4<f32>(input.position, 1.0);
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
    return out;
}

fn distribution_ggx(n_dot_h: f32, roughness: f32) -> f32 {
    let a = roughness * roughness;
    let a2 = a * a;
    let denom = n_dot_h * n_dot_h * (a2 - 1.0) + 1.0;
    return a2 / max(PI * denom * denom, 1e-7);
}

fn geometry_smith(n_dot_v: f32, n_dot_l: f32, roughness: f32) -> f32 {
    // Schlick-GGX with the direct lighting remapping of the roughness.
    let r = roughness + 1.0;
    let k = (r * r) / 8.0;
    let gv = n_dot_v / (n_dot_v * (1.0 - k) + k);
    let gl = n_dot_l / (n_dot_l * (1.0 - k) + k);
    return gv * gl;
}

fn fresnel_schlick(cos_theta: f32, f0: vec3<f32>) -> vec3<f32> {
    return f0 + (vec3<f32>(1.0) - f0) * pow(clamp(1.0 - cos_theta, 0.0, 1.0), 5.0);
}

fn fresnel_schlick_roughness(cos_theta: f32, f0: vec3<f32>, roughness: f32) -> vec3<f32> {
    let inv_rough = vec3<f32>(1.0 - roughness);
    return f0 + (max(inv_rough, f0) - f0) * pow(clamp(1.0 - cos_theta, 0.0, 1.0), 5.0);
}

// Windowed inverse square falloff (Karis, "Real Shading in Unreal Engine 4").
fn distance_attenuation(distance: f32, range: f32) -> f32 {
    let inv_square = 1.0 / max(distance * distance, 1e-4);
    if (range <= 0.0) {
        return inv_square;
    }
    let ratio = distance / range;
    let window = clamp(1.0 - ratio * ratio * ratio * ratio, 0.0, 1.0);
    return inv_square * window * window;
}

fn sample_environment(direction: vec3<f32>, lod: f32) -> vec3<f32> {
    // Equirectangular lookup.
    let uv = vec2<f32>(
        atan2(direction.z, direction.x) / (2.0 * PI) + 0.5,
        acos(clamp(direction.y, -1.0, 1.0)) / PI,
    );
    return textureSampleLevel(environment_texture, environment_sampler, uv, lod).rgb;
}

fn shading_normal(input: VertexOutput, front_facing: bool) -> vec3<f32> {
    var normal = normalize(input.world_normal);
    if (!front_facing) {
        normal = -normal;
    }
    if (material.texture_flags.y > 0.5) {
        let tangent_sample = textureSample(normal_texture, material_sampler, input.uv).xyz;
        var tangent_normal = tangent_sample * 2.0 - 1.0;
        tangent_normal = vec3<f32>(
            tangent_normal.xy * material.params1.x,
            tangent_normal.z,
        );
        let t = normalize(input.world_tangent.xyz - normal * dot(normal, input.world_tangent.xyz));
        if (length(input.world_tangent.xyz) > 1e-5) {
            let b = cross(normal, t) * input.world_tangent.w;
            normal = normalize(mat3x3<f32>(t, b, normal) * tangent_normal);
        }
    }
    return normal;
}

struct Surface {
    albedo: vec3<f32>,
    alpha: f32,
    normal: vec3<f32>,
    view: vec3<f32>,
    metallic: f32,
    roughness: f32,
    occlusion: f32,
    f0: vec3<f32>,
};

fn evaluate_light(
    light: Light,
    surface: Surface,
    world_position: vec3<f32>,
    shading_model: f32,
) -> vec3<f32> {
    let kind = light.position.w;
    var to_light = -normalize(light.direction.xyz);
    var attenuation = 1.0;

    if (kind == LIGHT_POINT || kind == LIGHT_SPOT || kind == LIGHT_AREA) {
        let delta = light.position.xyz - world_position;
        let distance = length(delta);
        if (distance < 1e-5) {
            return vec3<f32>(0.0);
        }
        to_light = delta / distance;
        attenuation = distance_attenuation(distance, light.direction.w);
        if (kind == LIGHT_SPOT) {
            let cos_angle = dot(normalize(light.direction.xyz), -to_light);
            let spot = clamp(
                (cos_angle - light.params.y) / max(light.params.x - light.params.y, 1e-4),
                0.0,
                1.0,
            );
            attenuation = attenuation * spot * spot;
        } else if (kind == LIGHT_AREA) {
            // Diffuse disc approximation: fade by the angle to the light plane.
            let facing = clamp(dot(normalize(light.direction.xyz), -to_light), 0.0, 1.0);
            attenuation = attenuation * facing * light.params.z * light.params.w;
        }
    }

    let n_dot_l = dot(surface.normal, to_light);
    if (n_dot_l <= 0.0 || attenuation <= 0.0) {
        return vec3<f32>(0.0);
    }

    let radiance = light.color.rgb * light.color.w * attenuation;

    if (shading_model == SHADING_LAMBERT) {
        return surface.albedo * radiance * n_dot_l;
    }

    if (shading_model == SHADING_PHONG) {
        let half_vector = normalize(to_light + surface.view);
        let specular_term = pow(
            max(dot(surface.normal, half_vector), 0.0),
            material.params0.w,
        );
        let specular = material.specular.rgb * specular_term;
        return (surface.albedo + specular) * radiance * n_dot_l;
    }

    // Cook-Torrance microfacet BRDF.
    let half_vector = normalize(to_light + surface.view);
    let n_dot_v = max(dot(surface.normal, surface.view), 1e-4);
    let n_dot_h = max(dot(surface.normal, half_vector), 0.0);
    let v_dot_h = max(dot(surface.view, half_vector), 0.0);

    let d = distribution_ggx(n_dot_h, surface.roughness);
    let g = geometry_smith(n_dot_v, n_dot_l, surface.roughness);
    let f = fresnel_schlick(v_dot_h, surface.f0);

    let specular = (d * g * f) / max(4.0 * n_dot_v * n_dot_l, 1e-4);
    let kd = (vec3<f32>(1.0) - f) * (1.0 - surface.metallic);
    let diffuse = kd * surface.albedo / PI;
    return (diffuse + specular) * radiance * n_dot_l;
}

@fragment
fn fs_main(input: VertexOutput, @builtin(front_facing) front_facing: bool) -> @location(0) vec4<f32> {
    var base_color = material.base_color;
    if (material.texture_flags.x > 0.5) {
        base_color = base_color * textureSample(base_color_texture, material_sampler, input.uv);
    }

    if (material.specular.w == ALPHA_MASK && base_color.a < material.params1.z) {
        discard;
    }

    let shading_model = material.params1.w;
    var color = base_color.rgb;

    if (shading_model != SHADING_BASIC) {
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

        var surface: Surface;
        surface.albedo = base_color.rgb;
        surface.alpha = base_color.a;
        surface.normal = shading_normal(input, front_facing);
        surface.view = normalize(frame.camera_position.xyz - input.world_position);
        surface.metallic = clamp(metallic, 0.0, 1.0);
        surface.roughness = clamp(roughness, 0.015, 1.0);
        surface.occlusion = occlusion;
        let dielectric = vec3<f32>(0.16 * material.params0.z * material.params0.z);
        surface.f0 = mix(dielectric, base_color.rgb, surface.metallic);

        var lit = vec3<f32>(0.0);
        var ambient_light = frame.ambient.rgb * frame.ambient.w;

        let count = u32(frame.misc.x);
        for (var i: u32 = 0u; i < count; i = i + 1u) {
            let light = light_buffer.lights[i];
            if (light.position.w == LIGHT_AMBIENT) {
                ambient_light = ambient_light + light.color.rgb * light.color.w;
            } else {
                lit = lit + evaluate_light(light, surface, input.world_position, shading_model);
            }
        }

        var ambient = ambient_light * surface.albedo * surface.occlusion;

        // Image based lighting from the equirectangular environment map.
        if (frame.misc.y > 0.5 && shading_model == SHADING_PBR) {
            let n_dot_v = max(dot(surface.normal, surface.view), 1e-4);
            let reflected = reflect(-surface.view, surface.normal);
            let irradiance = sample_environment(surface.normal, 6.0);
            let prefiltered = sample_environment(reflected, surface.roughness * 6.0);
            let f = fresnel_schlick_roughness(n_dot_v, surface.f0, surface.roughness);
            let kd = (vec3<f32>(1.0) - f) * (1.0 - surface.metallic);
            let intensity = frame.fog_params.w;
            ambient = ambient
                + (kd * irradiance * surface.albedo + prefiltered * f)
                * surface.occlusion
                * intensity;
        }

        color = lit + ambient;
    }

    var emissive = material.emissive.rgb * material.emissive.w;
    if (material.texture_flags.w > 0.5) {
        emissive = emissive * textureSample(emissive_texture, material_sampler, input.uv).rgb;
    }
    color = color + emissive;

    // Exponential squared fog.
    let density = frame.fog_color.w;
    if (density > 0.0) {
        let d = max(input.view_distance - frame.fog_params.x, 0.0) * density;
        let fog_factor = 1.0 - exp(-d * d);
        color = mix(color, frame.fog_color.rgb, clamp(fog_factor, 0.0, 1.0));
    }

    return vec4<f32>(color, base_color.a);
}
