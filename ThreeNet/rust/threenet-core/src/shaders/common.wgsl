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
    // x = debug view (0 = off), yzw reserved
    debug: vec4<f32>,
    // xyz = direction towards the sun, w = sky mode
    sky_sun: vec4<f32>,
    // x = intensity, y = haze, z = cloud cover, w = rotation
    sky_params: vec4<f32>,
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

// ---------------------------------------------------------------------- sky

fn sky_hash(p: vec2<f32>) -> f32 {
    return fract(sin(dot(p, vec2<f32>(127.1, 311.7))) * 43758.5453);
}

fn sky_value(p: vec2<f32>) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let w = f * f * (3.0 - 2.0 * f);
    let a = mix(sky_hash(i), sky_hash(i + vec2<f32>(1.0, 0.0)), w.x);
    let b = mix(sky_hash(i + vec2<f32>(0.0, 1.0)), sky_hash(i + vec2<f32>(1.0, 1.0)), w.x);
    return mix(a, b, w.y);
}

fn sky_fbm(p: vec2<f32>) -> f32 {
    var sum = 0.0;
    var amplitude = 0.5;
    var point = p;
    for (var i: i32 = 0; i < 5; i = i + 1) {
        sum = sum + sky_value(point) * amplitude;
        amplitude = amplitude * 0.5;
        point = point * 2.02;
    }
    return sum;
}

/// A sky from the sun direction alone: height gradient, horizon haze, forward
/// scattering around the sun, a sun disc bright enough for bloom, and an
/// optional cloud sheet.
fn procedural_sky(direction: vec3<f32>, sun: vec3<f32>, haze: f32, clouds: f32, time: f32) -> vec3<f32> {
    let up = clamp(direction.y, -1.0, 1.0);
    // Night is simply the sun being below the horizon.
    let night = 1.0 - clamp((sun.y + 0.12) / 0.32, 0.0, 1.0);
    let daylight = 1.0 - night;

    let zenith = mix(vec3<f32>(0.035, 0.115, 0.40), vec3<f32>(0.004, 0.009, 0.035), night);
    let horizon = mix(vec3<f32>(0.42, 0.56, 0.80), vec3<f32>(0.022, 0.035, 0.085), night);
    let blend = pow(clamp(1.0 - max(up, 0.0), 0.0, 1.0), max(4.5 - haze * 3.0 - clouds * 2.5, 0.6));
    var color = mix(zenith, horizon, blend);

    // Below the horizon the sky carries on as haze, so whatever the scene puts
    // there - ground, water, nothing - meets it without a seam.
    let ground = mix(horizon * 0.72, vec3<f32>(0.012, 0.016, 0.03), night);
    color = mix(ground, color, smoothstep(-0.12, 0.0, up));

    let sun_dot = max(dot(direction, sun), 0.0);
    let warm = mix(vec3<f32>(1.0, 0.42, 0.13), vec3<f32>(1.0, 0.88, 0.68), clamp(sun.y * 2.5, 0.0, 1.0));
    color = color + warm * pow(sun_dot, 5.0) * 0.6 * daylight;
    color = color + warm * pow(sun_dot, 64.0) * 1.4 * daylight;
    color = color + vec3<f32>(1.0, 0.95, 0.86) * smoothstep(0.99955, 0.99975, sun_dot) * 26.0 * daylight;

    let moon_dot = max(dot(direction, -sun), 0.0);
    color = color + vec3<f32>(0.78, 0.84, 1.0) * smoothstep(0.9992, 0.9996, moon_dot) * 7.0 * night;

    if (night > 0.02 && up > -0.02) {
        let cell = floor(direction.xz * 260.0 / max(abs(direction.y) + 0.35, 0.35));
        let twinkle = sky_hash(cell);
        let spark = step(0.9975, twinkle) * (0.6 + 0.4 * sin(time * 2.0 + twinkle * 40.0));
        color = color + vec3<f32>(0.9, 0.93, 1.0) * spark * night * 3.0;
    }

    if (clouds > 0.01 && up > 0.005) {
        // A plane at altitude, projected onto the view ray and drifting.
        let plane = direction.xz / max(direction.y, 0.06) * 0.35 + vec2<f32>(time * 0.004, time * 0.002);
        let density = sky_fbm(plane);
        let cover = smoothstep(1.0 - clouds * 1.45, 1.0 - clouds * 0.55, density);
        let top = mix(vec3<f32>(0.30, 0.31, 0.35), vec3<f32>(1.10, 1.06, 1.00), clamp(sun.y * 1.6, 0.0, 1.0));
        let base = mix(vec3<f32>(0.10, 0.11, 0.14), vec3<f32>(0.48, 0.47, 0.50), clamp(sun.y * 1.6, 0.0, 1.0));
        let lit = mix(base, top, clamp(density * 1.6, 0.0, 1.0)) * mix(1.0, 0.18, night);
        color = mix(color, lit, cover * smoothstep(0.005, 0.20, up));
    }

    return color;
}

/// The sky in a direction, as the sky pass draws it. Material shaders call this
/// for reflections, so what a lake mirrors is the sky actually overhead.
fn sky_color(direction: vec3<f32>) -> vec3<f32> {
    let mode = u32(frame.sky_sun.w);
    var color: vec3<f32>;
    if (mode == 1u) {
        // The environment map, spun around the vertical axis if asked.
        let rotation = frame.sky_params.w;
        let c = cos(rotation);
        let s = sin(rotation);
        let spun = vec3<f32>(
            direction.x * c - direction.z * s,
            direction.y,
            direction.x * s + direction.z * c,
        );
        color = sample_environment(spun, 0.0) * frame.fog_params.w;
    } else {
        color = procedural_sky(
            direction,
            normalize(frame.sky_sun.xyz),
            frame.sky_params.y,
            frame.sky_params.z,
            frame.fog_params.z,
        );
    }
    return color * frame.sky_params.x;
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

// -------------------------------------------------------------- debug views

const DEBUG_OFF: u32 = 0u;
const DEBUG_BASE_COLOR: u32 = 1u;
const DEBUG_WORLD_NORMAL: u32 = 2u;
const DEBUG_ROUGHNESS: u32 = 3u;
const DEBUG_METALLIC: u32 = 4u;
const DEBUG_OCCLUSION: u32 = 5u;
const DEBUG_EMISSIVE: u32 = 6u;
const DEBUG_DEPTH: u32 = 7u;
const DEBUG_LIGHTING: u32 = 8u;
const DEBUG_SHADOW: u32 = 9u;
const DEBUG_UV: u32 = 10u;
/// Channels a pass cannot answer are drawn magenta rather than black, so a
/// missing channel never looks like a black surface.
const DEBUG_UNAVAILABLE: u32 = 255u;

fn debug_view() -> u32 {
    return u32(frame.debug.x);
}

/// Smallest shadow visibility over every shadow casting light at one point.
fn shadow_mask(surface: Surface, world_position: vec3<f32>, view_depth: f32) -> f32 {
    var mask = 1.0;
    let count = u32(frame.misc.x);
    for (var i: u32 = 0u; i < count; i = i + 1u) {
        let light = light_buffer.lights[i];
        if (light.shadow.x < 0.0 || light.position.w == LIGHT_AMBIENT) {
            continue;
        }
        let visibility = shadow_visibility(
            light,
            world_position,
            surface.normal,
            view_depth,
            surface.receive_shadow,
        );
        mask = min(mask, visibility);
    }
    return mask;
}

/// Returns one channel of a shaded point instead of its lit colour. Tone
/// mapping, exposure, bloom and the camera effects are switched off while a
/// debug view is on, so what reaches the screen is the value itself.
fn debug_channel(
    mode: u32,
    surface_in: Surface,
    world_position: vec3<f32>,
    view_depth: f32,
    pixel: vec2<f32>,
    uv: vec2<f32>,
) -> vec4<f32> {
    var surface = surface_in;
    var color = vec3<f32>(1.0, 0.0, 1.0);
    switch (mode) {
        case 1u: {
            color = surface.albedo;
        }
        case 2u: {
            color = surface.normal * 0.5 + vec3<f32>(0.5);
        }
        case 3u: {
            color = vec3<f32>(surface.roughness);
        }
        case 4u: {
            color = vec3<f32>(surface.metallic);
        }
        case 5u: {
            var ao = surface.occlusion;
            if (frame.screen.z > 0.5) {
                ao = ao * textureSampleLevel(ao_texture, ao_sampler, pixel / frame.screen.xy, 0.0).r;
            }
            color = vec3<f32>(ao);
        }
        case 6u: {
            color = surface.emissive;
        }
        case 7u: {
            // Square root keeps the near range, where the detail is, readable.
            let far = max(frame.misc.w, frame.misc.z + 1e-3);
            color = vec3<f32>(sqrt(clamp(view_depth / far, 0.0, 1.0)));
        }
        case 8u: {
            // Lighting with the albedo taken out of the picture.
            surface.albedo = vec3<f32>(1.0);
            surface.metallic = 0.0;
            surface.emissive = vec3<f32>(0.0);
            color = shade_surface(surface, world_position, view_depth, pixel);
        }
        case 9u: {
            color = vec3<f32>(shadow_mask(surface, world_position, view_depth));
        }
        case 10u: {
            color = vec3<f32>(fract(uv), 0.0);
        }
        default: {
            color = vec3<f32>(1.0, 0.0, 1.0);
        }
    }
    return vec4<f32>(color, 1.0);
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
