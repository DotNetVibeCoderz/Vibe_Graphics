namespace DemoGraphics.Framework;

/// <summary>
/// The WGSL the scenes plug into the renderer through the <c>user_vertex</c> and
/// <c>user_surface</c> hooks. Everything here rides on the built-in shading, so
/// a wave still casts shadows, a crest still blooms and a swaying tree is still
/// picked up by SSAO.
///
/// Each source documents which material slots carry its parameters, because
/// <c>custom0</c>..<c>custom3</c> are all a hook gets. The sky, the water
/// simulation and the particle sprites moved into the library
/// (<c>ThreeNet.Effects</c>); what is left here is what is specific to these
/// scenes.
/// </summary>
public static class ShaderHooks
{
    /// <summary>
    /// Open water: the vertex hook lifts a sum of four wave trains and the
    /// surface hook rebuilds the matching normal, so the shading follows the
    /// geometry exactly. <c>custom0</c> = amplitude, frequency, speed,
    /// choppiness, <c>custom1</c> = wind xz, foam, clarity.
    /// </summary>
    public const string Water = """
        /// Height and slope of the wave field at a ground position.
        /// Returns (height, dH/dx, dH/dz).
        fn dg_waves(p: vec2<f32>, params: vec4<f32>, wind: vec2<f32>, time: f32) -> vec3<f32> {
            var height = 0.0;
            var slope = vec2<f32>(0.0);
            var amplitude = params.x;
            var frequency = max(params.y, 0.001);
            var speed = params.z;
            let base = normalize(wind + vec2<f32>(0.0001, 0.0));
            for (var i: i32 = 0; i < 4; i = i + 1) {
                // Fan the trains out so the field never looks like one ripple.
                let angle = f32(i) * 0.7 - 1.05;
                let c = cos(angle);
                let s = sin(angle);
                let dir = vec2<f32>(base.x * c - base.y * s, base.x * s + base.y * c);
                let phase = dot(dir, p) * frequency + time * speed;
                height = height + sin(phase) * amplitude;
                let derivative = cos(phase) * amplitude * frequency;
                slope = slope + dir * derivative;
                amplitude = amplitude * 0.52;
                frequency = frequency * 1.94;
                speed = speed * 1.21;
            }
            return vec3<f32>(height, slope.x, slope.y);
        }

        fn user_vertex(context: VertexContext) -> vec3<f32> {
            let wind = context.custom1.xy;
            let wave = dg_waves(context.position.xz, context.custom0, wind, context.time);
            // Choppiness pulls the surface towards the crests.
            let chop = context.custom0.w * 0.35;
            return context.position
                + vec3<f32>(-wave.y * chop, wave.x, -wave.z * chop);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let wind = context.custom1.xy;
            let wave = dg_waves(context.world_position.xz, context.custom0, wind, context.time);
            out.normal = normalize(vec3<f32>(-wave.y, 1.0, -wave.z));

            let clarity = context.custom1.w;
            let deep = vec3<f32>(0.004, 0.035, 0.055);
            let shallow = mix(vec3<f32>(0.02, 0.16, 0.19), vec3<f32>(0.03, 0.30, 0.34), clarity);
            // Crests catch the light, troughs keep the deep colour.
            let lift = clamp(wave.x / max(context.custom0.x * 2.0, 0.001) * 0.5 + 0.5, 0.0, 1.0);
            out.albedo = mix(deep, shallow, lift * lift);

            // Foam where the surface is both high and steep.
            let steepness = clamp(length(vec2<f32>(wave.y, wave.z)) * 1.1, 0.0, 1.0);
            let foam = clamp((lift - 0.82) * 5.0, 0.0, 1.0) * steepness * context.custom1.z;
            out.albedo = mix(out.albedo, vec3<f32>(0.82, 0.86, 0.88), foam);
            out.roughness = mix(0.045, 0.55, foam);
            out.metallic = 0.0;
            out.reflectance = 0.95;
            return out;
        }
        """;

    /// <summary>
    /// Trees and grass. The vertex hook sways with the shared wind, using the
    /// per-tree phase baked into <c>uv.y</c>; the surface hook picks bark or
    /// canopy from <c>uv.x</c>. <c>custom0</c> = bark colour + wind angle,
    /// <c>custom1</c> = canopy colour + wind strength.
    /// </summary>
    public const string Foliage = """
        fn user_vertex(context: VertexContext) -> vec3<f32> {
            let strength = context.custom1.w;
            let angle = context.custom0.w;
            let direction = vec2<f32>(sin(angle), cos(angle));
            // Bending grows with the square of the height, like a real stem.
            let lever = context.position.y * context.position.y * 0.02;
            let phase = context.uv.y * 6.2831853;
            let t = context.time * (1.1 + strength * 2.2) + phase;
            let gust = sin(t) * 0.72 + sin(t * 2.37 + 1.7) * 0.28;
            let bend = gust * lever * (0.15 + strength);
            return context.position + vec3<f32>(direction.x * bend, -abs(bend) * 0.12, direction.y * bend);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let canopy = step(0.5, context.uv.x);
            out.albedo = mix(context.custom0.xyz, context.custom1.xyz, canopy);
            // Break the canopy up so a low poly tree does not read as plastic.
            let variation = fract(sin(dot(floor(context.world_position.xz * 3.0), vec2<f32>(12.99, 78.23))) * 43758.55);
            out.albedo = out.albedo * (0.82 + 0.34 * variation);
            out.roughness = mix(0.9, 0.62, canopy);
            out.metallic = 0.0;
            return out;
        }
        """;

    /// <summary>
    /// Terrain splatting by height and slope, with snow on top.
    /// <c>custom0</c> = sand line, grass line, rock slope, snow amount,
    /// <c>custom1</c> = wetness.
    /// </summary>
    public const string Terrain = """
        fn dg_hash2(p: vec2<f32>) -> f32 {
            return fract(sin(dot(p, vec2<f32>(127.1, 311.7))) * 43758.5453);
        }

        fn dg_noise2(p: vec2<f32>) -> f32 {
            let i = floor(p);
            let f = fract(p);
            let w = f * f * (3.0 - 2.0 * f);
            let a = mix(dg_hash2(i), dg_hash2(i + vec2<f32>(1.0, 0.0)), w.x);
            let b = mix(dg_hash2(i + vec2<f32>(0.0, 1.0)), dg_hash2(i + vec2<f32>(1.0, 1.0)), w.x);
            return mix(a, b, w.y);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let height = context.world_position.y;
            let slope = 1.0 - clamp(context.world_normal.y, 0.0, 1.0);
            let sand_line = context.custom0.x;
            let grass_line = context.custom0.y;
            let rock_slope = context.custom0.z;
            let snow = context.custom0.w;
            let wetness = context.custom1.x;

            let sand = vec3<f32>(0.52, 0.45, 0.33);
            let grass = vec3<f32>(0.115, 0.185, 0.085);
            let rock = vec3<f32>(0.21, 0.205, 0.195);
            let crest = vec3<f32>(0.88, 0.91, 0.97);

            var albedo = mix(sand, grass, smoothstep(sand_line, grass_line, height));
            albedo = mix(albedo, rock, smoothstep(rock_slope, rock_slope + 0.22, slope));
            let cover = snow * smoothstep(grass_line, grass_line + 6.0, height) * (1.0 - clamp(slope * 1.8, 0.0, 1.0));
            albedo = mix(albedo, crest, cover);

            // Two scales of noise: broad patches, then grain. (`patch` itself is
            // a reserved word in WGSL, hence the name.)
            let patches = dg_noise2(context.world_position.xz * 0.06);
            let grain = dg_noise2(context.world_position.xz * 0.9);
            out.albedo = albedo * (0.78 + 0.34 * patches) * (0.92 + 0.16 * grain);
            // Wet ground is smoother and darker, which is what makes it look wet.
            out.albedo = out.albedo * mix(1.0, 0.68, wetness);
            out.roughness = clamp(mix(0.95, 0.18, wetness) - 0.12 * grain + cover * 0.2, 0.04, 1.0);
            out.metallic = 0.0;
            return out;
        }
        """;

    /// <summary>
    /// Signage and light tubes: emission that flickers per fixture, seeded from
    /// its world position. <c>custom0</c> = colour + flicker rate.
    /// </summary>
    public const string Neon = """
        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let seed = fract(sin(dot(floor(context.world_position.xz), vec2<f32>(41.7, 89.3))) * 1237.13);
            let rate = context.custom0.w;
            let pulse = 0.72 + 0.28 * sin(context.time * rate + seed * 24.0);
            // Every so often a tube stutters, which is what sells a night street.
            let stutter = step(0.986, fract(sin(floor(context.time * 8.0 + seed * 70.0)) * 5137.7));
            out.emissive = context.custom0.xyz * pulse * (1.0 - stutter * 0.75);
            out.albedo = context.custom0.xyz * 0.04;
            return out;
        }
        """;

    /// <summary>
    /// Asphalt that gets wet: puddles form in the noise lows, and a wet road is
    /// smooth, so the reflections arrive on their own.
    /// <c>custom0.x</c> = wetness, <c>custom0.y</c> = puddle scale.
    /// </summary>
    public const string WetGround = """
        fn dg_road_hash(p: vec2<f32>) -> f32 {
            return fract(sin(dot(p, vec2<f32>(127.1, 311.7))) * 43758.5453);
        }

        fn dg_road_noise(p: vec2<f32>) -> f32 {
            let i = floor(p);
            let f = fract(p);
            let w = f * f * (3.0 - 2.0 * f);
            let a = mix(dg_road_hash(i), dg_road_hash(i + vec2<f32>(1.0, 0.0)), w.x);
            let b = mix(dg_road_hash(i + vec2<f32>(0.0, 1.0)), dg_road_hash(i + vec2<f32>(1.0, 1.0)), w.x);
            return mix(a, b, w.y);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let wetness = context.custom0.x;
            let scale = max(context.custom0.y, 0.02);
            let coarse = dg_road_noise(context.world_position.xz * scale);
            let fine = dg_road_noise(context.world_position.xz * scale * 6.0);
            // Water pools in the lows, so the puddle mask follows the noise
            // (inverted by hand: smoothstep needs its edges in order).
            let puddle = (1.0 - smoothstep(0.34, 0.58, coarse)) * wetness;
            out.albedo = surface.albedo * (0.82 + 0.3 * fine) * mix(1.0, 0.45, puddle);
            out.roughness = clamp(mix(surface.roughness, 0.045, puddle) - 0.1 * wetness, 0.03, 1.0);
            out.reflectance = mix(surface.reflectance, 0.9, puddle);
            return out;
        }
        """;
}
