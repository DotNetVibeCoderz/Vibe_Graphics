namespace ThreeNet.Effects;

public static partial class EffectShaders
{
    /// <summary>
    /// Volumetric clouds: each fragment of a sheet at the cloud base ray marches the
    /// slab of air above it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The march runs from the fragment up through the layer along the view ray. At
    /// every step a density function is asked how much cloud is there - layered value
    /// noise, cut off by the coverage and shaped by height so that bases are flat and
    /// tops are heaped. Where there is cloud, a second, short march toward the sun
    /// finds how much cloud lies in the way of the light (Beer's law), which is what
    /// gives a cloud a bright side, a dark belly and a silver lining when the sun is
    /// behind it.
    /// </para>
    /// <para>Material slots:</para>
    /// <list type="bullet">
    /// <item><c>custom0</c> = direction toward the sun (xyz), coverage 0..1</item>
    /// <item><c>custom1</c> = sun colour (rgb), density (extinction per metre)</item>
    /// <item><c>custom2</c> = ambient sky colour (rgb), thickness of the layer in metres</item>
    /// <item><c>custom3</c> = wind drift x and z in metres, size of the largest billows in metres, march steps
    /// plus the haze fraction (whole part: steps; fraction: how much of the scene's fog the cloud takes)</item>
    /// </list>
    /// <para>
    /// Distance fog is tuned for the ground: a density that hides a street a kilometre
    /// off paints a cloud deck five kilometres away one flat colour. So the shader
    /// works out the fog the forward pass is about to apply and hands over a colour
    /// that comes out of it carrying only the haze fraction of that fog. The sheet
    /// must be tessellated for this (the pass interpolates distance per vertex).
    /// </para>
    /// <para>
    /// The sheet's uv must run 0..1 across it: the cloud thins out toward the rim, so
    /// the edge of the sheet is never seen. Use it with <see cref="AlphaMode.Blend"/>
    /// and no depth writing; <see cref="CloudLayer"/> sets all of that.
    /// </para>
    /// </remarks>
    public const string Clouds = """
        fn tn_cloud_hash(p: vec3<f32>) -> f32 {
            var q = fract(p * 0.3183099 + vec3<f32>(0.71, 0.113, 0.419));
            q = q * 17.0;
            return fract(q.x * q.y * q.z * (q.x + q.y + q.z));
        }

        fn tn_cloud_noise(x: vec3<f32>) -> f32 {
            let i = floor(x);
            let f = fract(x);
            let u = f * f * (3.0 - 2.0 * f);
            return mix(
                mix(mix(tn_cloud_hash(i), tn_cloud_hash(i + vec3<f32>(1.0, 0.0, 0.0)), u.x),
                    mix(tn_cloud_hash(i + vec3<f32>(0.0, 1.0, 0.0)), tn_cloud_hash(i + vec3<f32>(1.0, 1.0, 0.0)), u.x), u.y),
                mix(mix(tn_cloud_hash(i + vec3<f32>(0.0, 0.0, 1.0)), tn_cloud_hash(i + vec3<f32>(1.0, 0.0, 1.0)), u.x),
                    mix(tn_cloud_hash(i + vec3<f32>(0.0, 1.0, 1.0)), tn_cloud_hash(i + vec3<f32>(1.0, 1.0, 1.0)), u.x), u.y),
                u.z);
        }

        /// How much cloud there is at a point, 0..1. `height` is 0 at the base of the
        /// layer and 1 at its top. `fine` adds the small billows, which the march
        /// toward the sun can do without.
        fn tn_cloud_density(p: vec3<f32>, height: f32, coverage: f32, fine: bool) -> f32 {
            if (height <= 0.0 || height >= 1.0) {
                return 0.0;
            }
            // Flat underneath, heaped on top: the base comes in sharply, the crown tapers away.
            let profile = smoothstep(0.0, 0.12, height) * (1.0 - 0.8 * smoothstep(0.25, 1.0, height));
            var n = tn_cloud_noise(p) * 0.58 + tn_cloud_noise(p * 2.07 + vec3<f32>(17.0, 5.0, 9.0)) * 0.27;
            let threshold = mix(0.74, 0.2, clamp(coverage, 0.0, 1.0));
            // Nothing the small billows add can lift this point over the threshold.
            if (n * profile + 0.15 < threshold) {
                return 0.0;
            }
            if (fine) {
                n = n + tn_cloud_noise(p * 4.3 + vec3<f32>(3.0, 31.0, 7.0)) * 0.1 + tn_cloud_noise(p * 9.1) * 0.05;
            } else {
                n = n + 0.075;
            }
            return clamp((n * profile - threshold) * 4.5, 0.0, 1.0);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            out.albedo = vec3<f32>(0.0);
            out.shading_model = SHADING_BASIC;

            let sun = normalize(context.custom0.xyz + vec3<f32>(0.0, 0.0001, 0.0));
            let coverage = context.custom0.w;
            let extinction = context.custom1.w;
            let thickness = max(context.custom2.w, 1.0);
            let scale = 1.0 / max(context.custom3.z, 1.0);
            let steps = clamp(i32(floor(context.custom3.w)), 4, 64);
            let haze = fract(context.custom3.w);
            let wind = vec3<f32>(context.custom3.x, 0.0, context.custom3.y);

            // The sheet lies at the base of the layer and is looked at from below: the
            // ray climbs through the layer. Near the horizon it would run for ever, so
            // its length is capped.
            let direction = -normalize(context.view_direction);
            let climb = max(abs(direction.y), 0.07);
            let step_length = thickness / climb / f32(steps);
            let base = context.world_position.y;
            let rise = sign(direction.y + 0.00001);

            // Each pixel starts at its own depth into the first step, which turns the
            // bands a short march leaves into a grain the eye does not follow.
            let pixel = context.screen_uv * vec2<f32>(1931.0, 1087.0);
            let jitter = fract(52.9829189 * fract(dot(pixel, vec2<f32>(0.06711056, 0.00583715))));
            var position = context.world_position + direction * step_length * jitter;

            // Forward scattering: cloud between the eye and the sun glows at its edges.
            let toward = clamp(dot(direction, sun), 0.0, 1.0);
            let phase = 0.62 + 1.1 * pow(toward, 6.0);
            let light_step = thickness * 0.16;

            var transmittance = 1.0;
            var radiance = vec3<f32>(0.0);
            for (var i: i32 = 0; i < steps; i = i + 1) {
                let height = (position.y - base) * rise / thickness;
                let density = tn_cloud_density((position + wind) * scale, height, coverage, true);
                if (density > 0.002) {
                    // How much cloud the sunlight came through to get here.
                    var shade = 0.0;
                    var probe = position;
                    for (var j: i32 = 0; j < 3; j = j + 1) {
                        probe = probe + sun * light_step;
                        shade = shade + tn_cloud_density((probe + wind) * scale, (probe.y - base) * rise / thickness, coverage, false);
                    }
                    let sunlight = exp(-shade * extinction * light_step);
                    // The sky lights a cloud from all round, its top more than its belly.
                    let ambient = context.custom2.rgb * (0.4 + 0.6 * height);
                    let colour = context.custom1.rgb * sunlight * phase + ambient;
                    let kept = exp(-density * extinction * step_length);
                    radiance = radiance + colour * transmittance * (1.0 - kept);
                    transmittance = transmittance * kept;
                    if (transmittance < 0.03) {
                        break;
                    }
                }
                position = position + direction * step_length;
            }

            // Thin out toward the rim of the sheet.
            let rim = clamp(length(context.uv - vec2<f32>(0.5)) * 2.0, 0.0, 1.0);
            let alpha = 1.0 - transmittance;
            // The blend multiplies by alpha again, so hand over the colour of the cloud itself.
            var colour = radiance / max(alpha, 0.001);

            // The fog the pass applies after this, and the gentler haze the cloud should
            // have instead: solve for the colour that turns into the second under the first.
            let reach = max(length(frame.camera_position.xyz - context.world_position) - frame.fog_params.x, 0.0) * frame.fog_color.w;
            let fog = min(1.0 - exp(-reach * reach), 0.97);
            let soft = 1.0 - exp(-reach * reach * haze * haze);
            colour = max((mix(colour, frame.fog_color.rgb, soft) - frame.fog_color.rgb * fog) / (1.0 - fog), vec3<f32>(0.0));

            out.emissive = colour;
            out.alpha = alpha * (1.0 - smoothstep(0.62, 0.98, rim)) * surface.alpha;
            return out;
        }
        """;
}
