namespace ThreeNet.Effects;

/// <summary>
/// WGSL for the built-in effects, written against the <c>user_vertex</c> and
/// <c>user_surface</c> hooks. Everything here rides on the normal shading, so an
/// effect still takes light, still casts shadows and still reaches the post
/// chain.
/// </summary>
/// <remarks>
/// The sources are public because an effect is a starting point, not a black
/// box: copy one, change the part you want and hand it to
/// <see cref="Scene.CreateShader"/>. Each one documents the material slots it
/// reads, since <c>custom0</c>..<c>custom3</c> and <c>custom_texture</c> are all
/// a hook gets.
/// </remarks>
public static class EffectShaders
{
    /// <summary>
    /// Reads the wave height field out of <c>custom_texture</c>. Shared by the
    /// water surface and by the caustics the floor draws, so both see exactly
    /// the same water.
    /// </summary>
    /// <remarks>
    /// The field is RGBA32F - height, velocity, normal x, normal z - which no
    /// backend promises to filter in hardware, so it is fetched by texel and
    /// interpolated here.
    /// </remarks>
    public const string HeightField = """
        /// Bilinear fetch of the wave field. uv (0,0)..(1,1) spans the field.
        fn tn_water_fetch(uv: vec2<f32>) -> vec4<f32> {
            let size = vec2<f32>(textureDimensions(custom_texture, 0));
            let point = clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0)) * (size - vec2<f32>(1.0));
            let base = floor(point);
            let f = point - base;
            let x0 = i32(base.x);
            let y0 = i32(base.y);
            let x1 = min(x0 + 1, i32(size.x) - 1);
            let y1 = min(y0 + 1, i32(size.y) - 1);
            let a = textureLoad(custom_texture, vec2<i32>(x0, y0), 0);
            let b = textureLoad(custom_texture, vec2<i32>(x1, y0), 0);
            let c = textureLoad(custom_texture, vec2<i32>(x0, y1), 0);
            let d = textureLoad(custom_texture, vec2<i32>(x1, y1), 0);
            return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
        }

        /// World xz to field uv. `field` is (centre x, centre z, size, _).
        fn tn_water_uv(position: vec2<f32>, field: vec4<f32>) -> vec2<f32> {
            return (position - field.xy) / max(field.z, 0.001) + vec2<f32>(0.5);
        }

        /// The surface normal stored in the field: only x and z are kept.
        fn tn_water_normal(sample: vec4<f32>) -> vec3<f32> {
            let flat_part = clamp(1.0 - sample.b * sample.b - sample.a * sample.a, 0.0, 1.0);
            return normalize(vec3<f32>(sample.b, sqrt(flat_part), sample.a));
        }

        fn tn_hash21(p: vec2<f32>) -> f32 {
            return fract(sin(dot(p, vec2<f32>(127.1, 311.7))) * 43758.5453);
        }

        fn tn_value2(p: vec2<f32>) -> f32 {
            let i = floor(p);
            let f = fract(p);
            let w = f * f * (3.0 - 2.0 * f);
            let a = mix(tn_hash21(i), tn_hash21(i + vec2<f32>(1.0, 0.0)), w.x);
            let b = mix(tn_hash21(i + vec2<f32>(0.0, 1.0)), tn_hash21(i + vec2<f32>(1.0, 1.0)), w.x);
            return mix(a, b, w.y);
        }
        """;

    /// <summary>
    /// The water surface: the mesh is lifted by the height field, the normal
    /// comes from the same field, the sky is reflected by Fresnel and what is
    /// under the water is absorbed with depth.
    /// </summary>
    /// <remarks>
    /// <para>Material slots:</para>
    /// <list type="bullet">
    /// <item><c>custom0</c> = field centre xz, field size, displacement scale</item>
    /// <item><c>custom1</c> = deep water colour (rgb), absorption per metre</item>
    /// <item><c>custom2</c> = shallow water colour (rgb), floor height</item>
    /// <item><c>custom3</c> = foam, detail ripples, detail speed, water level</item>
    /// <item><c>custom_texture</c> = the wave field</item>
    /// </list>
    /// <para>
    /// The node holding the surface must sit at the centre of the field with no
    /// rotation, because the vertex hook works in object space;
    /// <see cref="WaterSurface"/> sets that up for you.
    /// </para>
    /// </remarks>
    public const string Water = HeightField + """

        fn user_vertex(context: VertexContext) -> vec3<f32> {
            // Object space, and the node sits at the centre of the field, so the
            // uv is the position over the field size.
            let uv = context.position.xz / max(context.custom0.z, 0.001) + vec2<f32>(0.5);
            let wave = tn_water_fetch(uv);
            return context.position + vec3<f32>(0.0, wave.r * context.custom0.w, 0.0);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let field = context.custom0;
            let uv = tn_water_uv(context.world_position.xz, field);
            let wave = tn_water_fetch(uv);

            // The simulated normal carries the big ripples; two scrolling noise
            // fields add the small chop the grid is too coarse to hold.
            var normal = tn_water_normal(wave);
            let detail_strength = context.custom3.y;
            if (detail_strength > 0.001) {
                let speed = context.custom3.z;
                let p = context.world_position.xz;
                let t = context.time * speed;
                let e = 0.35;
                let h = tn_value2(p * 1.7 + vec2<f32>(t, t * 0.6))
                    + tn_value2(p * 3.9 - vec2<f32>(t * 0.8, t * 1.3)) * 0.5;
                let hx = tn_value2((p + vec2<f32>(e, 0.0)) * 1.7 + vec2<f32>(t, t * 0.6))
                    + tn_value2((p + vec2<f32>(e, 0.0)) * 3.9 - vec2<f32>(t * 0.8, t * 1.3)) * 0.5;
                let hz = tn_value2((p + vec2<f32>(0.0, e)) * 1.7 + vec2<f32>(t, t * 0.6))
                    + tn_value2((p + vec2<f32>(0.0, e)) * 3.9 - vec2<f32>(t * 0.8, t * 1.3)) * 0.5;
                normal = normalize(normal + vec3<f32>(-(hx - h), 0.0, -(hz - h)) * detail_strength * 14.0);
            }
            out.normal = normal;

            let view = normalize(context.view_direction);
            let facing = clamp(dot(view, normal), 0.0, 1.0);
            // Schlick against water's own reflectance: glass flat on, mirror at
            // a glancing angle. This single number is what makes water look wet.
            let fresnel = 0.02 + 0.98 * pow(1.0 - facing, 5.0);

            // How much water the view ray has to cross before it reaches the
            // bottom, and what survives it (Beer-Lambert).
            let level = context.custom3.w + wave.r * field.w;
            let depth = max(level - context.custom2.w, 0.0);
            let path = depth / max(facing, 0.12);
            let absorption = context.custom1.w;
            let transmission = exp(-path * absorption);

            // Shallow water keeps its own colour, deep water loses everything
            // but the blue; the sky arrives on top through the Fresnel term.
            let body = mix(context.custom1.xyz, context.custom2.xyz, transmission);
            out.albedo = body;
            out.emissive = sky_color(reflect(-view, normal)) * fresnel * 0.9;

            // Foam where the water is steep, and along the shore where the
            // bottom comes up to meet it.
            let steepness = clamp(length(vec2<f32>(wave.b, wave.a)) * 2.2, 0.0, 1.0);
            let shore = 1.0 - smoothstep(0.0, 0.14, depth);
            let foam = clamp((steepness * 0.8 + shore) * context.custom3.x, 0.0, 1.0);
            out.albedo = mix(out.albedo, vec3<f32>(0.86, 0.90, 0.92), foam);

            out.roughness = mix(0.035, 0.55, foam);
            out.metallic = 0.0;
            out.reflectance = 0.95;
            // Opaque where the water is deep or seen flat on, clear at the edge.
            out.alpha = clamp(max(1.0 - transmission, fresnel) + foam, 0.0, 1.0) * surface.alpha;
            return out;
        }
        """;

    /// <summary>
    /// The pool floor seen through the water: caustics, and the blue the depth
    /// of the water adds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Caustics are computed the way the reference does, by differential area:
    /// refract the sunlight through the wave surface, then ask how much the
    /// patch of floor one pixel covers has shrunk or spread. Where the waves act
    /// as a lens the patch shrinks and the same light lands in a smaller place,
    /// which is the bright web on the bottom of a pool.
    /// </para>
    /// <para>Material slots:</para>
    /// <list type="bullet">
    /// <item><c>custom0</c> = field centre xz, field size, water level</item>
    /// <item><c>custom1</c> = direction towards the sun (xyz), caustic strength</item>
    /// <item><c>custom2</c> = underwater tint (rgb), absorption per metre</item>
    /// <item><c>custom_texture</c> = the wave field</item>
    /// </list>
    /// </remarks>
    public const string WaterFloor = HeightField + """

        /// Where a ray from `start` along `direction` crosses height `plane`.
        fn tn_plane_hit(start: vec3<f32>, direction: vec3<f32>, plane: f32) -> vec3<f32> {
            let denominator = select(direction.y, 0.0001, abs(direction.y) < 0.0001);
            return start + direction * ((plane - start.y) / denominator);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let field = context.custom0;
            let level = field.w;
            let depth = level - context.world_position.y;
            if (depth <= 0.0) {
                // Above the water line: dry stone, nothing to do.
                return out;
            }

            let sun = normalize(context.custom1.xyz);
            // Walk back up the light ray to where it entered the water...
            let entry = tn_plane_hit(context.world_position, sun, level);
            let wave = tn_water_fetch(tn_water_uv(entry.xz, field));
            let normal = tn_water_normal(wave);
            // ...bend it there, and follow it back down to the floor.
            let bent = refract(-sun, normal, 1.0 / 1.333);
            let landing = tn_plane_hit(entry, bent, context.world_position.y);

            // One pixel of floor covers this much of the undisturbed grid, and
            // this much once the waves have moved it. The ratio is the focus.
            let flat_area = length(dpdx(entry)) * length(dpdy(entry));
            let bent_area = length(dpdx(landing)) * length(dpdy(landing));
            let focus = clamp(flat_area / max(bent_area, 1.0e-6), 0.0, 6.0);
            // 1.0 is "no focusing at all", so the caustic is what sits above it.
            let caustic = max(focus - 1.0, 0.0) * context.custom1.w;

            // What is left of the daylight after crossing the water twice.
            let absorption = context.custom2.w;
            let through = exp(-depth * (1.0 + 1.0 / max(sun.y, 0.25)) * absorption);
            out.albedo = out.albedo * mix(context.custom2.xyz, vec3<f32>(1.0), through);
            out.emissive = out.emissive + out.albedo * caustic * through;
            return out;
        }
        """;

    /// <summary>
    /// Unpacks what <see cref="ParticleEffect"/> hides in a sprite's uv: the
    /// quad corner, the age from 0 at birth to 1 at death, and a per particle
    /// random.
    /// </summary>
    public const string Sprite = """
        struct TnSprite {
            corner: vec2<f32>,
            radius: f32,
            age: f32,
            seed: f32,
        };

        /// The corner lives in the fraction 0.25..0.75 of the uv and the two
        /// bytes are added on as whole numbers, which float32 holds exactly.
        fn tn_sprite(uv: vec2<f32>) -> TnSprite {
            var out: TnSprite;
            out.corner = (fract(uv) - vec2<f32>(0.25)) * 2.0;
            out.radius = clamp(length(out.corner - vec2<f32>(0.5)) * 2.0, 0.0, 1.0);
            out.age = floor(uv.x) / 255.0;
            out.seed = floor(uv.y) / 255.0;
            return out;
        }

        fn tn_sprite_hash(p: vec2<f32>) -> f32 {
            return fract(sin(dot(p, vec2<f32>(127.1, 311.7))) * 43758.5453);
        }
        """;

    /// <summary>
    /// Volumetric fire: the fragment ray marches a box and accumulates a flame
    /// that turbulent 3D noise shapes, after
    /// <see href="https://github.com/typeWolffo/THREE.Fire">THREE.Fire</see> and
    /// the Alfred et al. paper behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference looks the flame colour up in a texture. This builds the
    /// same ramp in code - a cone shaped envelope and a temperature that falls
    /// as the flame rises and spreads - so a fire needs no asset at all.
    /// </para>
    /// <para>Material slots:</para>
    /// <list type="bullet">
    /// <item><c>custom0</c> = fire centre xyz, brightness</item>
    /// <item><c>custom1</c> = box size xyz, rise speed</item>
    /// <item><c>custom2</c> = tint (rgb), magnitude (how far the noise bends the flame)</item>
    /// <item><c>custom3</c> = noise scale across, noise scale up, lacunarity, gain</item>
    /// </list>
    /// <para>
    /// Use it with <see cref="AlphaMode.Additive"/>, <see cref="CullMode.None"/>
    /// and no depth writing; <see cref="FireEffect"/> sets all of that.
    /// </para>
    /// </remarks>
    public const string Fire = """
        // Ashima's simplex noise, as the reference uses: its ridges are what
        // make a flame look torn rather than merely bumpy.
        fn tn_mod289_3(x: vec3<f32>) -> vec3<f32> { return x - floor(x * (1.0 / 289.0)) * 289.0; }
        fn tn_mod289_4(x: vec4<f32>) -> vec4<f32> { return x - floor(x * (1.0 / 289.0)) * 289.0; }
        fn tn_permute(x: vec4<f32>) -> vec4<f32> { return tn_mod289_4(((x * 34.0) + 1.0) * x); }
        fn tn_taylor_inv_sqrt(r: vec4<f32>) -> vec4<f32> { return 1.79284291400159 - 0.85373472095314 * r; }

        fn tn_snoise(v: vec3<f32>) -> f32 {
            let C = vec2<f32>(1.0 / 6.0, 1.0 / 3.0);
            let D = vec4<f32>(0.0, 0.5, 1.0, 2.0);

            var i = floor(v + dot(v, C.yyy));
            let x0 = v - i + dot(i, C.xxx);

            let g = step(x0.yzx, x0.xyz);
            let l = 1.0 - g;
            let i1 = min(g.xyz, l.zxy);
            let i2 = max(g.xyz, l.zxy);

            let x1 = x0 - i1 + C.xxx;
            let x2 = x0 - i2 + C.yyy;
            let x3 = x0 - D.yyy;

            i = tn_mod289_3(i);
            let p = tn_permute(tn_permute(tn_permute(
                i.z + vec4<f32>(0.0, i1.z, i2.z, 1.0))
                + i.y + vec4<f32>(0.0, i1.y, i2.y, 1.0))
                + i.x + vec4<f32>(0.0, i1.x, i2.x, 1.0));

            let n_ = 0.142857142857;
            let ns = n_ * D.wyz - D.xzx;

            let j = p - 49.0 * floor(p * ns.z * ns.z);
            let x_ = floor(j * ns.z);
            let y_ = floor(j - 7.0 * x_);

            let x = x_ * ns.x + ns.yyyy;
            let y = y_ * ns.x + ns.yyyy;
            let h = 1.0 - abs(x) - abs(y);

            let b0 = vec4<f32>(x.xy, y.xy);
            let b1 = vec4<f32>(x.zw, y.zw);

            let s0 = floor(b0) * 2.0 + 1.0;
            let s1 = floor(b1) * 2.0 + 1.0;
            let sh = -step(h, vec4<f32>(0.0));

            let a0 = b0.xzyw + s0.xzyw * sh.xxyy;
            let a1 = b1.xzyw + s1.xzyw * sh.zzww;

            var p0 = vec3<f32>(a0.xy, h.x);
            var p1 = vec3<f32>(a0.zw, h.y);
            var p2 = vec3<f32>(a1.xy, h.z);
            var p3 = vec3<f32>(a1.zw, h.w);

            let norm = tn_taylor_inv_sqrt(vec4<f32>(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
            p0 = p0 * norm.x;
            p1 = p1 * norm.y;
            p2 = p2 * norm.z;
            p3 = p3 * norm.w;

            var m = max(0.6 - vec4<f32>(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), vec4<f32>(0.0));
            m = m * m;
            return 42.0 * dot(m * m, vec4<f32>(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
        }

        /// Turbulence: octaves of |noise|, whose creases read as flame edges.
        fn tn_turbulence(p: vec3<f32>, lacunarity: f32, gain: f32) -> f32 {
            var sum = 0.0;
            var frequency = 1.0;
            var amplitude = 1.0;
            for (var i: i32 = 0; i < 3; i = i + 1) {
                sum = sum + abs(tn_snoise(p * frequency)) * amplitude;
                frequency = frequency * lacunarity;
                amplitude = amplitude * gain;
            }
            return sum;
        }

        /// The flame at one point of the normalised volume: xz in -1..1 around
        /// the axis, y in 0..1 from the base. Returns colour and density.
        fn tn_flame(
            point: vec3<f32>,
            tint: vec3<f32>,
            magnitude: f32,
            noise_scale: vec3<f32>,
            lacunarity: f32,
            gain: f32,
            rise: f32,
            seed: f32,
            time: f32,
        ) -> vec4<f32> {
            var st = vec2<f32>(length(point.xz), point.y);
            if (st.x >= 1.0 || st.y <= 0.0 || st.y >= 1.0) {
                return vec4<f32>(0.0);
            }

            // Turbulence only ever pushes the sample *up* the flame, and the
            // envelope only narrows with height, so a sample already outside the
            // envelope at this height can never be inside it afterwards. Testing
            // that first is exact, and it is what keeps the cost off the empty
            // corners of the box - the flame is a thin cone inside it.
            var taper = pow(1.0 - st.y, 0.55);
            if (st.x >= taper) {
                return vec4<f32>(0.0);
            }

            // The noise field falls as the fire rises, so the flame climbs.
            var sample_point = point;
            sample_point.y = sample_point.y - (seed + time) * rise;
            sample_point = sample_point * noise_scale;
            st.y = st.y + sqrt(st.y) * magnitude * tn_turbulence(sample_point, lacunarity, gain);
            if (st.y <= 0.0 || st.y >= 1.0) {
                return vec4<f32>(0.0);
            }

            // Envelope: broad at the base, drawn to a point at the top.
            taper = pow(1.0 - st.y, 0.55);
            let body = 1.0 - smoothstep(taper * 0.25, taper, st.x);
            if (body <= 0.001) {
                return vec4<f32>(0.0);
            }

            // Temperature: hottest in the core and low down, cooling as it
            // spreads and climbs. The ramp is a flame's own: white, yellow,
            // orange, then the red that is nearly smoke.
            let heat = clamp(body * (1.15 - st.y), 0.0, 1.0);
            var color = mix(vec3<f32>(0.62, 0.055, 0.004), vec3<f32>(1.0, 0.33, 0.03), smoothstep(0.0, 0.42, heat));
            color = mix(color, vec3<f32>(1.0, 0.72, 0.21), smoothstep(0.36, 0.72, heat));
            color = mix(color, vec3<f32>(1.0, 0.95, 0.80), smoothstep(0.70, 1.0, heat));
            return vec4<f32>(color * tint, body * body * 0.42);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let center = context.custom0.xyz;
            let size = max(context.custom1.xyz, vec3<f32>(0.001));
            let steps = 20;
            // Two fires in different places burn out of step on their own.
            let seed = fract(sin(dot(center.xz, vec2<f32>(12.9898, 78.233))) * 43758.5453) * 20.0;
            let noise_scale = vec3<f32>(context.custom3.x, context.custom3.y, context.custom3.x);

            // March from the front face of the box away from the camera. The
            // box is the volume; everything inside it is the fire.
            let direction = -normalize(context.view_direction);
            let step_length = length(size) / f32(steps);
            var position = context.world_position;
            var accumulated = vec4<f32>(0.0);

            for (var i: i32 = 0; i < steps; i = i + 1) {
                position = position + direction * step_length;
                // Normalised: xz in -1..1 around the axis, y in 0..1 upwards.
                let local = (position - center) / size;
                if (abs(local.x) > 0.5 || abs(local.z) > 0.5 || abs(local.y) > 0.5) {
                    continue;
                }
                accumulated = accumulated + tn_flame(
                    vec3<f32>(local.x * 2.0, local.y + 0.5, local.z * 2.0),
                    context.custom2.xyz,
                    context.custom2.w,
                    noise_scale,
                    context.custom3.z,
                    context.custom3.w,
                    context.custom1.w,
                    seed,
                    context.time,
                );
                // The core of a flame is already opaque; marching past it only
                // adds light that will be clamped away.
                if (accumulated.a >= 1.0) {
                    break;
                }
            }

            out.albedo = vec3<f32>(0.0);
            out.emissive = accumulated.rgb * context.custom0.w;
            out.alpha = clamp(accumulated.a, 0.0, 1.0) * surface.alpha;
            out.shading_model = SHADING_BASIC;
            return out;
        }
        """;

    /// <summary>
    /// A particle sprite: a soft round grain, fading to nothing at the rim and
    /// changing colour as the particle ages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A vertex carries a position, a normal, a uv and a tangent and nothing
    /// else, so the age and the per particle random ride in the uv alongside the
    /// corner: the corner is squeezed into the fraction 0.25..0.75 and the two
    /// bytes are added as whole numbers. <see cref="ParticleEffect"/> packs them
    /// and <c>tn_sprite</c> unpacks them.
    /// </para>
    /// <para>Material slots:</para>
    /// <list type="bullet">
    /// <item><c>custom0</c> = colour at birth (rgb), edge hardness</item>
    /// <item><c>custom1</c> = colour at death (rgb), brightness</item>
    /// <item><c>custom2</c>.x = how far the colour follows the age, .y = flicker</item>
    /// </list>
    /// </remarks>
    public const string Particle = Sprite + """

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let sprite = tn_sprite(context.uv);
            let hardness = max(context.custom0.w, 0.2);
            let falloff = pow(clamp(1.0 - sprite.radius, 0.0, 1.0), hardness);

            let tint = mix(context.custom0.xyz, context.custom1.xyz, clamp(sprite.age * context.custom2.x, 0.0, 1.0));
            // A little flicker, out of phase per particle, so a shower of sparks
            // does not pulse as one.
            let flicker = 1.0 - context.custom2.y * (0.5 + 0.5 * sin(context.time * 17.0 + sprite.seed * 40.0));

            out.albedo = vec3<f32>(0.0);
            out.emissive = tint * context.custom1.w * falloff * flicker;
            out.alpha = surface.alpha * falloff;
            out.shading_model = SHADING_BASIC;
            return out;
        }
        """;

    /// <summary>
    /// Smoke: the same grain, but lit rather than glowing, thinning as it ages
    /// and broken up by noise so a plume does not read as a wall of discs.
    /// </summary>
    /// <remarks>
    /// <c>custom0</c> = colour (rgb) and edge hardness, <c>custom1</c>.x =
    /// opacity at birth, .y = opacity at death.
    /// </remarks>
    public const string Smoke = Sprite + """

        fn tn_smoke_noise(p: vec2<f32>) -> f32 {
            let i = floor(p);
            let f = fract(p);
            let w = f * f * (3.0 - 2.0 * f);
            let a = mix(tn_sprite_hash(i), tn_sprite_hash(i + vec2<f32>(1.0, 0.0)), w.x);
            let b = mix(tn_sprite_hash(i + vec2<f32>(0.0, 1.0)), tn_sprite_hash(i + vec2<f32>(1.0, 1.0)), w.x);
            return mix(a, b, w.y);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let sprite = tn_sprite(context.uv);
            let hardness = max(context.custom0.w, 0.2);
            // Each puff gets its own slice of the noise, and turns as it rises.
            let angle = sprite.seed * 6.2831853 + sprite.age * 1.2;
            let c = cos(angle);
            let sn = sin(angle);
            let centred = sprite.corner - vec2<f32>(0.5);
            let turned = vec2<f32>(
                centred.x * c - centred.y * sn,
                centred.x * sn + centred.y * c,
            );
            let grain = tn_smoke_noise(turned * 3.0 + vec2<f32>(sprite.seed * 37.0));
            let falloff = pow(clamp(1.0 - sprite.radius, 0.0, 1.0), hardness) * (0.55 + 0.45 * grain);

            out.albedo = context.custom0.xyz;
            out.roughness = 1.0;
            out.metallic = 0.0;
            out.alpha = surface.alpha * falloff * mix(context.custom1.x, context.custom1.y, sprite.age);
            return out;
        }
        """;
}
