// Three.Net shading library shared by the forward pass, the deferred G-buffer
// and the deferred lighting pass: frame and light data, BRDFs, shadows, image
// based lighting and fog.
// Made by Gravicode Studios - led by Kang Fadhil.

const PI: f32 = 3.141592653589793;
const MAX_LIGHTS: u32 = 128u;
const MAX_SHADOW_LAYERS: u32 = 16u;

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
    // xy = target size, z = SSAO enabled, w = SSAO strength on direct light
    screen: vec4<f32>,
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
    // x = first shadow layer (-1 = none), y = cascades (or 6 cube faces), z = depth bias, w = normal bias
    shadow: vec4<f32>,
    // x = shadow strength
    shadow_extra: vec4<f32>,
};

struct LightBuffer {
    lights: array<Light, MAX_LIGHTS>,
};

struct ShadowData {
    matrices: array<mat4x4<f32>, MAX_SHADOW_LAYERS>,
    cascade_splits: vec4<f32>,
    // x = texel size, y = PCF radius, z = cascade blend, w = enabled
    params: vec4<f32>,
    // World space size of one shadow texel, per layer (MAX_SHADOW_LAYERS values).
    texel_world: array<vec4<f32>, 4>,
};

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<uniform> light_buffer: LightBuffer;
@group(0) @binding(2) var environment_texture: texture_2d<f32>;
@group(0) @binding(3) var environment_sampler: sampler;
@group(0) @binding(4) var shadow_map: texture_depth_2d_array;
@group(0) @binding(5) var shadow_sampler: sampler_comparison;
@group(0) @binding(6) var<uniform> shadows: ShadowData;
@group(0) @binding(7) var ao_texture: texture_2d<f32>;
@group(0) @binding(8) var ao_sampler: sampler;

/// Everything the lighting needs about one shaded point. Custom surface
/// shaders receive and return this struct.
struct Surface {
    albedo: vec3<f32>,
    alpha: f32,
    normal: vec3<f32>,
    metallic: f32,
    emissive: vec3<f32>,
    roughness: f32,
    specular: vec3<f32>,
    occlusion: f32,
    shininess: f32,
    reflectance: f32,
    shading_model: f32,
    receive_shadow: f32,
};

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

// ------------------------------------------------------------------ shadows

fn sample_shadow_layer(layer: i32, world_position: vec3<f32>, bias: f32) -> f32 {
    let clip = shadows.matrices[layer] * vec4<f32>(world_position, 1.0);
    let ndc = clip.xyz / clip.w;
    let uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    if (any(uv < vec2<f32>(0.0)) || any(uv > vec2<f32>(1.0)) || ndc.z < 0.0 || ndc.z > 1.0) {
        return 1.0;
    }

    // Percentage closer filtering over a (2r+1)^2 texel footprint.
    let reference = ndc.z - bias;
    let radius = i32(shadows.params.y);
    let texel = shadows.params.x;
    var lit = 0.0;
    var taps = 0.0;
    for (var y: i32 = -radius; y <= radius; y = y + 1) {
        for (var x: i32 = -radius; x <= radius; x = x + 1) {
            let offset = vec2<f32>(f32(x), f32(y)) * texel;
            lit = lit + textureSampleCompareLevel(shadow_map, shadow_sampler, uv + offset, layer, reference);
            taps = taps + 1.0;
        }
    }
    return lit / max(taps, 1.0);
}

fn shadow_visibility(light: Light, world_position: vec3<f32>, normal: vec3<f32>, view_depth: f32, receive: f32) -> f32 {
    if (shadows.params.w < 0.5 || light.shadow.x < 0.0 || receive < 0.5) {
        return 1.0;
    }

    let kind = light.position.w;
    var to_light = -normalize(light.direction.xyz);
    if (kind != LIGHT_DIRECTIONAL) {
        to_light = normalize(light.position.xyz - world_position);
    }
    let n_dot_l = clamp(dot(normal, to_light), 0.0, 1.0);

    var layer = i32(light.shadow.x);
    let cascades = i32(light.shadow.y);
    var fade = 0.0;

    if (kind == LIGHT_POINT) {
        // Cube shadow: six layers, picked by the major axis of the light to
        // fragment vector, in the order the planner writes them.
        let offset_to_fragment = world_position - light.position.xyz;
        let magnitude = abs(offset_to_fragment);
        var face = 0;
        if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z) {
            face = select(1, 0, offset_to_fragment.x > 0.0);
        } else if (magnitude.y >= magnitude.z) {
            face = select(3, 2, offset_to_fragment.y > 0.0);
        } else {
            face = select(5, 4, offset_to_fragment.z > 0.0);
        }
        layer = layer + face;
    }

    if (kind == LIGHT_DIRECTIONAL) {
        let last_split = shadows.cascade_splits[max(cascades - 1, 0)];
        if (view_depth > last_split) {
            return 1.0;
        }
        var cascade = 0;
        for (var i: i32 = 0; i < cascades - 1; i = i + 1) {
            if (view_depth > shadows.cascade_splits[i]) {
                cascade = i + 1;
            }
        }
        layer = layer + cascade;
        // Fade the far edge of the last cascade instead of cutting it off.
        fade = smoothstep(last_split * (1.0 - shadows.params.z), last_split, view_depth);
    }

    // Normal offset, measured in shadow texels of the chosen layer and grown at
    // grazing angles where acne shows first.
    let texel_world = shadows.texel_world[layer / 4][layer % 4];
    let offset = normal * light.shadow.w * texel_world * (1.5 - n_dot_l);
    let visibility = sample_shadow_layer(layer, world_position + offset, light.shadow.z);
    return mix(1.0, mix(visibility, 1.0, fade), light.shadow_extra.x);
}

// ------------------------------------------------------------------ lights

fn surface_f0(surface: Surface) -> vec3<f32> {
    let dielectric = vec3<f32>(0.16 * surface.reflectance * surface.reflectance);
    return mix(dielectric, surface.albedo, surface.metallic);
}

fn evaluate_light(light: Light, surface: Surface, view: vec3<f32>, world_position: vec3<f32>, visibility: f32) -> vec3<f32> {
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
    if (n_dot_l <= 0.0 || attenuation <= 0.0 || visibility <= 0.0) {
        return vec3<f32>(0.0);
    }

    let radiance = light.color.rgb * light.color.w * attenuation * visibility;

    if (surface.shading_model == SHADING_LAMBERT) {
        return surface.albedo * radiance * n_dot_l;
    }

    if (surface.shading_model == SHADING_PHONG) {
        let half_vector = normalize(to_light + view);
        let specular_term = pow(max(dot(surface.normal, half_vector), 0.0), surface.shininess);
        return (surface.albedo + surface.specular * specular_term) * radiance * n_dot_l;
    }

    // Cook-Torrance microfacet BRDF.
    let f0 = surface_f0(surface);
    let half_vector = normalize(to_light + view);
    let n_dot_v = max(dot(surface.normal, view), 1e-4);
    let n_dot_h = max(dot(surface.normal, half_vector), 0.0);
    let v_dot_h = max(dot(view, half_vector), 0.0);

    let d = distribution_ggx(n_dot_h, surface.roughness);
    let g = geometry_smith(n_dot_v, n_dot_l, surface.roughness);
    let f = fresnel_schlick(v_dot_h, f0);

    let specular = (d * g * f) / max(4.0 * n_dot_v * n_dot_l, 1e-4);
    let kd = (vec3<f32>(1.0) - f) * (1.0 - surface.metallic);
    let diffuse = kd * surface.albedo / PI;
    return (diffuse + specular) * radiance * n_dot_l;
}

/// Full lighting of a surface: every light with its shadow, SSAO, ambient,
/// image based lighting and emission. Returns linear HDR radiance.
fn shade_surface(surface_in: Surface, world_position: vec3<f32>, view_depth: f32, pixel: vec2<f32>) -> vec3<f32> {
    var surface = surface_in;
    if (surface.shading_model == SHADING_BASIC) {
        return surface.albedo + surface.emissive;
    }

    let view = normalize(frame.camera_position.xyz - world_position);
    var lit = vec3<f32>(0.0);
    var ambient_light = frame.ambient.rgb * frame.ambient.w;

    let count = u32(frame.misc.x);
    for (var i: u32 = 0u; i < count; i = i + 1u) {
        let light = light_buffer.lights[i];
        if (light.position.w == LIGHT_AMBIENT) {
            ambient_light = ambient_light + light.color.rgb * light.color.w;
        } else {
            let visibility = shadow_visibility(light, world_position, surface.normal, view_depth, surface.receive_shadow);
            lit = lit + evaluate_light(light, surface, view, world_position, visibility);
        }
    }

    // Screen space ambient occlusion from the SSAO pass (1.0 when disabled).
    var ao = 1.0;
    if (frame.screen.z > 0.5) {
        ao = textureSampleLevel(ao_texture, ao_sampler, pixel / frame.screen.xy, 0.0).r;
        surface.occlusion = surface.occlusion * ao;
    }

    var ambient = ambient_light * surface.albedo * surface.occlusion;

    // Image based lighting from the equirectangular environment map.
    if (frame.misc.y > 0.5 && surface.shading_model == SHADING_PBR) {
        let f0 = surface_f0(surface);
        let n_dot_v = max(dot(surface.normal, view), 1e-4);
        let reflected = reflect(-view, surface.normal);
        let irradiance = sample_environment(surface.normal, 6.0);
        let prefiltered = sample_environment(reflected, surface.roughness * 6.0);
        let f = fresnel_schlick_roughness(n_dot_v, f0, surface.roughness);
        let kd = (vec3<f32>(1.0) - f) * (1.0 - surface.metallic);
        ambient = ambient + (kd * irradiance * surface.albedo + prefiltered * f) * surface.occlusion * frame.fog_params.w;
    }

    return lit * mix(1.0, ao, frame.screen.w) + ambient + surface.emissive;
}

/// Exponential squared distance fog.
fn apply_fog(color: vec3<f32>, view_distance: f32) -> vec3<f32> {
    let density = frame.fog_color.w;
    if (density <= 0.0) {
        return color;
    }
    let d = max(view_distance - frame.fog_params.x, 0.0) * density;
    let fog_factor = 1.0 - exp(-d * d);
    return mix(color, frame.fog_color.rgb, clamp(fog_factor, 0.0, 1.0));
}
