namespace ThreeNet.Effects;

/// <summary>
/// The compute kernel behind <see cref="GpuParticleEffect"/>.
/// </summary>
public static class GpuParticleShaders
{
    /// <summary>
    /// Ages, respawns and moves every particle, then writes its billboard quad.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One invocation per particle, doing both halves. Splitting the step from
    /// the quad building would mean a second dispatch and a second pass over
    /// the same memory for no gain, since each particle's quad depends only on
    /// its own state.
    /// </para>
    /// <para>
    /// The vertex struct is declared as scalars on purpose. A vertex is 48
    /// bytes, but WGSL aligns <c>vec3&lt;f32&gt;</c> to 16, so the struct that
    /// looks right would have a stride of 64 and write every quad after the
    /// first into the wrong place.
    /// </para>
    /// </remarks>
    public const string Step = """
        struct Particle {
            position: vec3<f32>,
            velocity: vec3<f32>,
            age: f32,
            life: f32,
            seed: f32,
            size: f32,
        };

        // Scalars, not vec3: a Vertex is 48 bytes packed, and a vec3 would pad
        // the stride out to 64.
        struct Vertex {
            px: f32, py: f32, pz: f32,
            nx: f32, ny: f32, nz: f32,
            u: f32, v: f32,
            tx: f32, ty: f32, tz: f32, tw: f32,
        };

        @group(0) @binding(0) var<storage, read_write> particles: array<Particle>;
        @group(0) @binding(1) var<storage, read_write> params: array<f32>;
        @group(0) @binding(2) var<storage, read_write> vertices: array<Vertex>;

        fn hash(value: f32) -> f32 {
            return fract(sin(value * 127.1) * 43758.5453);
        }

        /// A direction in a cone of half angle `spread` around `axis`.
        fn scatter(axis: vec3<f32>, spread: f32, seed: f32) -> vec3<f32> {
            let theta = hash(seed) * 6.2831853;
            let radius = spread * sqrt(hash(seed + 7.0));
            // Any vector not parallel to the axis gives a usable basis.
            var side = vec3<f32>(1.0, 0.0, 0.0);
            if (abs(axis.x) > 0.9) {
                side = vec3<f32>(0.0, 1.0, 0.0);
            }
            let right = normalize(cross(axis, side));
            let up = cross(right, axis);
            return normalize(axis + (right * cos(theta) + up * sin(theta)) * radius);
        }

        fn write_quad(index: u32, centre: vec3<f32>, right: vec3<f32>, up: vec3<f32>, size: f32) {
            let base = index * 4u;
            let r = right * size * 0.5;
            let u = up * size * 0.5;
            let corners = array<vec3<f32>, 4>(
                centre - r - u,
                centre + r - u,
                centre + r + u,
                centre - r + u,
            );
            let uvs = array<vec2<f32>, 4>(
                vec2<f32>(0.0, 1.0),
                vec2<f32>(1.0, 1.0),
                vec2<f32>(1.0, 0.0),
                vec2<f32>(0.0, 0.0),
            );
            // The normal faces the camera, which is what the billboard basis
            // already describes.
            let normal = normalize(cross(right, up));
            for (var corner = 0u; corner < 4u; corner = corner + 1u) {
                let at = base + corner;
                let p = corners[corner];
                vertices[at].px = p.x;
                vertices[at].py = p.y;
                vertices[at].pz = p.z;
                vertices[at].nx = normal.x;
                vertices[at].ny = normal.y;
                vertices[at].nz = normal.z;
                vertices[at].u = uvs[corner].x;
                vertices[at].v = uvs[corner].y;
                vertices[at].tx = right.x;
                vertices[at].ty = right.y;
                vertices[at].tz = right.z;
                vertices[at].tw = 1.0;
            }
        }

        @compute @workgroup_size(64)
        fn step(@builtin(global_invocation_id) id: vec3<u32>) {
            let index = id.x;
            if (index >= arrayLength(&particles)) {
                return;
            }

            let dt = params[0];
            let time = params[1];
            let lifetime = params[2];
            let variation = params[3];
            let origin = vec3<f32>(params[4], params[5], params[6]);
            let emitter_radius = params[7];
            let direction = vec3<f32>(params[8], params[9], params[10]);
            let speed = params[11];
            let gravity = vec3<f32>(params[12], params[13], params[14]);
            let drag = params[15];
            let right = vec3<f32>(params[16], params[17], params[18]);
            let spread = params[19];
            let up = vec3<f32>(params[20], params[21], params[22]);
            let size = params[23];
            let growth = params[24];

            var particle = particles[index];
            particle.age = particle.age + dt;

            // A particle with no life has never been born; a particle past its
            // life starts again. Both take the same path, so the buffer needs
            // no initialisation before the first step.
            if (particle.life <= 0.0 || particle.age >= particle.life) {
                let seed = f32(index) + time * 0.61803;
                particle.seed = seed;
                particle.age = 0.0;
                particle.life = lifetime * (1.0 - variation * hash(seed + 3.0));
                let offset = vec3<f32>(
                    hash(seed + 11.0) - 0.5,
                    hash(seed + 13.0) - 0.5,
                    hash(seed + 17.0) - 0.5,
                ) * 2.0 * emitter_radius;
                particle.position = origin + offset;
                particle.velocity = scatter(direction, spread, seed) * speed
                    * (0.6 + 0.8 * hash(seed + 19.0));
                particle.size = size;
            } else {
                particle.velocity = particle.velocity + gravity * dt;
                particle.velocity = particle.velocity * max(1.0 - drag * dt, 0.0);
                particle.position = particle.position + particle.velocity * dt;
            }

            particles[index] = particle;

            // Grow with age, and collapse to nothing at the very end so a
            // particle fades out rather than vanishing mid air.
            let t = clamp(particle.age / max(particle.life, 0.0001), 0.0, 1.0);
            // Grows towards `growth` over its life, then collapses at the very
            // end so a particle fades out rather than vanishing in mid air.
            let scale = particle.size * mix(1.0, growth, t) * (1.0 - t * t);
            write_quad(index, particle.position, right, up, scale);
        }
        """;
}
