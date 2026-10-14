// Skinning on the GPU: blend the joint matrices by weight and transform the
// bind pose into the buffer the draw reads.
//
// The vertex struct is scalars rather than vectors on purpose. A Vertex is 48
// bytes packed, but WGSL aligns vec3<f32> to 16, so the obvious struct would
// have a stride of 64 and touch every vertex after the first at the wrong
// offset - silently, with no error and merely wrong geometry.

struct Vertex {
    px: f32, py: f32, pz: f32,
    nx: f32, ny: f32, nz: f32,
    u: f32, v: f32,
    tx: f32, ty: f32, tz: f32, tw: f32,
};

@group(0) @binding(0) var<storage, read> bind_pose: array<Vertex>;
// Four joint indices a vertex, widened to u32 because WGSL has no u16.
@group(0) @binding(1) var<storage, read> joints: array<u32>;
@group(0) @binding(2) var<storage, read> weights: array<vec4<f32>>;
@group(0) @binding(3) var<storage, read> matrices: array<mat4x4<f32>>;
@group(0) @binding(4) var<storage, read_write> vertices: array<Vertex>;

@compute @workgroup_size(64)
fn skin(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x;
    if (index >= arrayLength(&bind_pose)) {
        return;
    }

    let bind = bind_pose[index];
    let weight = weights[index];
    let joint_count = arrayLength(&matrices);

    var blended = mat4x4<f32>(
        vec4<f32>(0.0, 0.0, 0.0, 0.0),
        vec4<f32>(0.0, 0.0, 0.0, 0.0),
        vec4<f32>(0.0, 0.0, 0.0, 0.0),
        vec4<f32>(0.0, 0.0, 0.0, 0.0),
    );
    var total = 0.0;
    for (var k = 0u; k < 4u; k = k + 1u) {
        let w = weight[k];
        if (w <= 0.0) {
            continue;
        }
        let joint = joints[index * 4u + k];
        // A joint index past the end of the skeleton is a broken asset, not a
        // reason to read memory that belongs to something else.
        if (joint >= joint_count) {
            continue;
        }
        let m = matrices[joint];
        blended[0] = blended[0] + m[0] * w;
        blended[1] = blended[1] + m[1] * w;
        blended[2] = blended[2] + m[2] * w;
        blended[3] = blended[3] + m[3] * w;
        total = total + w;
    }

    if (total <= 0.0) {
        // Unweighted vertices stay where the bind pose left them, which is what
        // the CPU path does too.
        vertices[index] = bind;
        return;
    }
    if (abs(total - 1.0) > 0.001) {
        // Weights that do not sum to one would scale the vertex as well as move
        // it, which shows up as a limb that swells where the weighting is thin.
        let scale = 1.0 / total;
        blended[0] = blended[0] * scale;
        blended[1] = blended[1] * scale;
        blended[2] = blended[2] * scale;
        blended[3] = blended[3] * scale;
    }

    let position = blended * vec4<f32>(bind.px, bind.py, bind.pz, 1.0);
    // Normals and tangents are directions: the translation must not reach them.
    let rotation = mat3x3<f32>(blended[0].xyz, blended[1].xyz, blended[2].xyz);
    let normal = normalize(rotation * vec3<f32>(bind.nx, bind.ny, bind.nz));
    let tangent = normalize(rotation * vec3<f32>(bind.tx, bind.ty, bind.tz));

    vertices[index].px = position.x;
    vertices[index].py = position.y;
    vertices[index].pz = position.z;
    vertices[index].nx = normal.x;
    vertices[index].ny = normal.y;
    vertices[index].nz = normal.z;
    vertices[index].u = bind.u;
    vertices[index].v = bind.v;
    vertices[index].tx = tangent.x;
    vertices[index].ty = tangent.y;
    vertices[index].tz = tangent.z;
    // Handedness is a property of the UVs, not of the pose.
    vertices[index].tw = bind.tw;
}
