// Screen space reflections, run on the HDR image before bloom and tone mapping.
//
// The depth prepass leaves view space normals and linear view depth for every
// opaque pixel. That is enough to march a reflection: take the ray bouncing off
// the surface, step along it in view space, project each step back to the
// screen, and compare the depth the ray has reached with the depth the scene
// actually has there. The first step that ends up behind a surface has found
// what the pixel reflects, and the colour is already sitting in the frame.
//
// What this cannot do is reflect anything the camera is not looking at. A ray
// that leaves the frame, or that passes behind something nearer, has no colour
// to return - so reflections fade out towards the edges rather than being cut
// off, which is the artefact every screen space technique trades for its cost.

struct SsrParams {
    projection: mat4x4<f32>,
    inverse_projection: mat4x4<f32>,
    // x = intensity, y = max distance, z = thickness, w = coarse steps
    params: vec4<f32>,
};

@group(0) @binding(0) var color_texture: texture_2d<f32>;
// Normals in xyz, linear view depth in w. Point sampled: interpolating a
// normal or a depth across a silhouette produces a value no surface has.
@group(0) @binding(1) var gbuffer_texture: texture_2d<f32>;
@group(0) @binding(2) var linear_sampler: sampler;
@group(0) @binding(3) var<uniform> params: SsrParams;

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

fn load_gbuffer(uv: vec2<f32>) -> vec4<f32> {
    let size = vec2<f32>(textureDimensions(gbuffer_texture));
    let pixel = clamp(vec2<i32>(uv * size), vec2<i32>(0), vec2<i32>(size) - vec2<i32>(1));
    return textureLoad(gbuffer_texture, pixel, 0);
}

/// The view space position a pixel stands at, from its uv and its linear depth.
/// The inverse projection gives the direction; the depth says how far to go.
fn view_position(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let ndc = vec4<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 1.0, 1.0);
    let unprojected = params.inverse_projection * ndc;
    let direction = unprojected.xyz / unprojected.w;
    // Scale the ray so that it lands exactly on the plane z = -depth, which is
    // what "linear view depth" means.
    return direction * (depth / max(-direction.z, 1.0e-6));
}

/// Where a view space point lands on screen, or uv outside 0..1 if it is behind
/// the camera.
fn project(view: vec3<f32>) -> vec2<f32> {
    let clip = params.projection * vec4<f32>(view, 1.0);
    if (clip.w <= 0.0) {
        return vec2<f32>(-1.0);
    }
    let ndc = clip.xy / clip.w;
    return vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
}

@fragment
fn fs_ssr(input: FullscreenVertex) -> @location(0) vec4<f32> {
    let source = textureSampleLevel(color_texture, linear_sampler, input.uv, 0.0);
    let gbuffer = load_gbuffer(input.uv);
    let depth = gbuffer.w;
    // The prepass clears to zero, so a zero depth is the background: sky and
    // empty space reflect nothing.
    if (depth <= 0.0) {
        return source;
    }

    let normal = normalize(gbuffer.xyz);
    let origin = view_position(input.uv, depth);
    let to_surface = normalize(origin);
    let ray = reflect(to_surface, normal);

    // A ray heading back towards the camera would have to leave through the
    // near plane; there is nothing on screen for it to find.
    if (ray.z > 0.0) {
        return source;
    }

    let max_distance = max(params.params.y, 0.01);
    let thickness = max(params.params.z, 0.001);
    let steps = max(i32(params.params.w), 8);
    let step_size = max_distance / f32(steps);

    var hit_uv = vec2<f32>(0.0);
    var found = false;
    var travelled = 0.0;
    var previous = 0.0;

    for (var i = 1; i <= steps; i = i + 1) {
        let distance = f32(i) * step_size;
        let sample_view = origin + ray * distance;
        let uv = project(sample_view);
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
            break;
        }

        let scene_depth = load_gbuffer(uv).w;
        if (scene_depth <= 0.0) {
            previous = distance;
            continue;
        }

        let ray_depth = -sample_view.z;
        let difference = ray_depth - scene_depth;
        // Behind a surface, but not so far behind that the ray has gone through
        // a thin wall and out the other side into something unrelated.
        if (difference > 0.0 && difference < thickness) {
            // Binary search between the last step in front and this one behind,
            // which buys most of the accuracy of a fine march for four samples.
            var low = previous;
            var high = distance;
            for (var refine = 0; refine < 4; refine = refine + 1) {
                let mid = (low + high) * 0.5;
                let mid_view = origin + ray * mid;
                let mid_uv = project(mid_view);
                let mid_scene = load_gbuffer(mid_uv).w;
                if (mid_scene > 0.0 && -mid_view.z > mid_scene) {
                    high = mid;
                } else {
                    low = mid;
                }
            }
            hit_uv = project(origin + ray * high);
            travelled = high;
            found = true;
            break;
        }
        previous = distance;
    }

    if (!found) {
        return source;
    }

    // Everything that makes the reflection fade rather than stop.
    // At the edge of the frame there is no more scene to reflect.
    let edge = min(
        min(hit_uv.x, 1.0 - hit_uv.x),
        min(hit_uv.y, 1.0 - hit_uv.y),
    );
    let edge_fade = smoothstep(0.0, 0.08, edge);
    // A ray that had to travel a long way is the least trustworthy, because it
    // is the most likely to have passed behind something it never tested.
    let distance_fade = 1.0 - clamp(travelled / max_distance, 0.0, 1.0);
    // A reflection pointing straight back at the camera is the case screen
    // space cannot serve, since what it wants is hidden behind the surface.
    let facing_fade = clamp(-ray.z, 0.0, 1.0);
    // Fresnel: a surface reflects most at a grazing angle. Without the material
    // parameters here this is all the weighting there is, which is why the
    // effect is strongest on floors, exactly where it is wanted.
    let cos_theta = clamp(dot(-to_surface, normal), 0.0, 1.0);
    let fresnel = 0.04 + 0.96 * pow(1.0 - cos_theta, 5.0);

    let weight = clamp(params.params.x, 0.0, 1.0)
        * edge_fade * distance_fade * facing_fade * fresnel;
    let reflected = textureSampleLevel(color_texture, linear_sampler, hit_uv, 0.0);
    return vec4<f32>(source.rgb + reflected.rgb * weight, source.a);
}
