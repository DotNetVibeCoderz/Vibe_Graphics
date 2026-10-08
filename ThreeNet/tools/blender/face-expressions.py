"""Authors facial expression shape keys on the model and exports them as glTF
morph targets.

The model ships as one static mesh with no blend shapes, so the expressions are
built here: each one moves the vertices of the face island - the loose part that
holds the eyes, nose, mouth and cheeks - with a smooth radial falloff around
landmarks measured from the rendered head.

    blender --background --python expressions.py -- <in.glb> <out.glb>
"""

import math
import sys
import bmesh
import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
source_path, out_path = argv[0], argv[1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=source_path)
obj = next(o for o in bpy.data.objects if o.type == "MESH")
mesh = obj.data

# ---------------------------------------------------------------- face island
bm = bmesh.new()
bm.from_mesh(mesh)
bm.verts.ensure_lookup_table()
seen = [False] * len(bm.verts)
islands = []
for start in range(len(bm.verts)):
    if seen[start]:
        continue
    stack, island = [start], []
    seen[start] = True
    while stack:
        index = stack.pop()
        island.append(index)
        for edge in bm.verts[index].link_edges:
            other = edge.other_vert(bm.verts[index]).index
            if not seen[other]:
                seen[other] = True
                stack.append(other)
    islands.append(island)
bm.free()

# The face is the island that holds the mouth: in front of everything, spanning
# the eye and chin heights.
def is_face(island):
    ys = [mesh.vertices[v].co.y for v in island]
    zs = [mesh.vertices[v].co.z for v in island]
    return min(ys) < -0.20 and min(zs) < 0.42 and max(zs) > 0.58 and len(island) > 300

face = next((set(i) for i in sorted(islands, key=len, reverse=True) if is_face(i)), None)
if face is None:
    raise SystemExit("REPORT could not find the face island")
print(f"REPORT face island: {len(face)} vertices of {len(mesh.vertices)}")

# --------------------------------------------------------------- seam feather
# The face is its own island sitting against the head, so anything that moves
# its border opens a hole. Distance from the border gives a weight that pins the
# edge and lets the middle move freely.
bm = bmesh.new()
bm.from_mesh(mesh)
bm.verts.ensure_lookup_table()

border = []
for index in face:
    vert = bm.verts[index]
    if any(len(edge.link_faces) < 2 for edge in vert.link_edges):
        border.append(index)

INFINITY = float("inf")
distance = {index: INFINITY for index in face}
frontier = []
for index in border:
    distance[index] = 0.0
    frontier.append(index)

# Dijkstra-ish sweep over the island's edges, measuring real distance.
while frontier:
    frontier.sort(key=lambda i: distance[i])
    current = frontier.pop(0)
    here = bm.verts[current]
    for edge in here.link_edges:
        other = edge.other_vert(here)
        if other.index not in distance:
            continue
        step = distance[current] + (other.co - here.co).length
        if step < distance[other.index] - 1e-6:
            distance[other.index] = step
            frontier.append(other.index)
bm.free()

FEATHER = 0.030
seam_weight = {}
for index in face:
    d = distance[index]
    if d == INFINITY:
        seam_weight[index] = 1.0
    else:
        t = min(1.0, d / FEATHER)
        seam_weight[index] = t * t * (3.0 - 2.0 * t)
print(f"REPORT island border: {len(border)} vertices pinned, feather {FEATHER} m")

# ------------------------------------------------------------------ landmarks
# Measured off the rendered head: the model faces -Y, up is +Z.
MOUTH = Vector((0.000, -0.205, 0.452))
MOUTH_CORNER = 0.060      # how far out the corners sit in x
EYE = Vector((0.075, -0.200, 0.558))
BROW = Vector((0.072, -0.200, 0.600))
CHEEK = Vector((0.105, -0.185, 0.487))
CHIN = Vector((0.000, -0.190, 0.400))

def falloff(distance, radius):
    """Smooth 1 at the centre, 0 at the radius."""
    if distance >= radius:
        return 0.0
    t = 1.0 - distance / radius
    return t * t * (3.0 - 2.0 * t)

def shape(name, move):
    """Adds a shape key and moves the face vertices with `move(co, mirrored)`."""
    key = obj.shape_key_add(name=name, from_mix=False)
    data = key.data
    moved = 0
    for index in face:
        co = mesh.vertices[index].co
        # Mirror x so one rule drives both sides.
        side = 1.0 if co.x >= 0.0 else -1.0
        mirrored = Vector((abs(co.x), co.y, co.z))
        offset = move(mirrored, side) * seam_weight[index]
        if offset.length > 1e-6:
            data[index].co = co + offset
            moved += 1
    print(f"REPORT shape '{name}': {moved} vertices moved")
    return key

# The basis has to exist before any other key.
obj.shape_key_add(name="Basis", from_mix=False)

# ----------------------------------------------------------------- expressions

def smile(co, side):
    corner = Vector((MOUTH_CORNER, MOUTH.y, MOUTH.z))
    w = falloff((co - corner).length, 0.075)
    cheek_w = falloff((co - Vector((CHEEK.x, CHEEK.y, CHEEK.z))).length, 0.065) * 0.45
    lift = Vector((0.016 * side, -0.006, 0.030)) * w
    cheek = Vector((0.004 * side, -0.010, 0.012)) * cheek_w
    return lift + cheek

def frown(co, side):
    corner = Vector((MOUTH_CORNER, MOUTH.y, MOUTH.z))
    w = falloff((co - corner).length, 0.075)
    brow_w = falloff((co - Vector((BROW.x * 0.8, BROW.y, BROW.z))).length, 0.055) * 0.5
    return Vector((0.005 * side, 0.005, -0.032)) * w + Vector((-0.008 * side, 0.005, -0.014)) * brow_w

def jaw_open(co, side):
    # Everything below the mouth swings down, strongest at the chin.
    if co.z > MOUTH.z + 0.03:
        return Vector((0.0, 0.0, 0.0))
    reach = max(0.0, (MOUTH.z + 0.03) - co.z) / 0.09
    w = min(1.0, reach) ** 1.4
    narrow = falloff(abs(co.x), 0.14)
    # Modest: this mesh has no mouth interior, so a wide jaw would show a hole.
    return Vector((-0.002 * side * w * narrow, 0.006 * w * narrow, -0.022 * w * narrow))

def brow_raise(co, side):
    w = falloff((co - Vector((BROW.x, BROW.y, BROW.z))).length, 0.075)
    centre = falloff((co - Vector((0.0, BROW.y, BROW.z + 0.005))).length, 0.06) * 0.6
    return Vector((0.0, -0.005, 0.026)) * max(w, centre)

def brow_furrow(co, side):
    w = falloff((co - Vector((BROW.x, BROW.y, BROW.z))).length, 0.07)
    return Vector((-0.010 * side, -0.006, -0.014)) * w

def squint(co, side):
    # The lower lid lifts and the outer corner tightens.
    w = falloff((co - Vector((EYE.x, EYE.y, EYE.z - 0.018))).length, 0.05)
    return Vector((-0.003 * side, 0.002, 0.013)) * w

def cheek_puff(co, side):
    w = falloff((co - Vector((CHEEK.x, CHEEK.y, CHEEK.z))).length, 0.075)
    return Vector((0.020 * side, -0.026, 0.0)) * w

def pucker(co, side):
    corner = Vector((MOUTH_CORNER, MOUTH.y, MOUTH.z))
    w = falloff((co - corner).length, 0.070)
    centre = falloff((co - MOUTH).length, 0.045)
    return Vector((-0.018 * side, -0.006, 0.0)) * w + Vector((0.0, -0.016, 0.0)) * centre

for name, fn in [
    ("smile", smile),
    ("frown", frown),
    ("jawOpen", jaw_open),
    ("browRaise", brow_raise),
    ("browFurrow", brow_furrow),
    ("squint", squint),
    ("cheekPuff", cheek_puff),
    ("pucker", pucker),
]:
    shape(name, fn)

# Every key stays at rest in the exported file; the host drives the weights.
for key in mesh.shape_keys.key_blocks:
    key.value = 0.0

bpy.ops.export_scene.gltf(
    filepath=out_path,
    export_format="GLB",
    export_morph=True,
    export_morph_normal=True,
    export_morph_tangent=False,
    export_apply=False,
)
print(f"REPORT exported {out_path} with {len(mesh.shape_keys.key_blocks) - 1} morph targets")
