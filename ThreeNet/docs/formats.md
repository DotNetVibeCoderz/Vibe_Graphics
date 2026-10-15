# Model formats / Format model

Every importer, and what each one actually reads. `Scene.LoadModel(path)` picks
by extension; the named methods take the same arguments and skip the guess.

Setiap importer, dan apa yang benar-benar dibaca masing-masing.
`Scene.LoadModel(path)` memilih dari ekstensi.

| Format | Method | Reads | Does not read |
|---|---|---|---|
| glTF 2.0 / GLB | `LoadGltf` | Meshes, materials, textures, animations, skins, morph targets | — |
| FBX | `LoadFbx` | Meshes, materials, animations, skins | — |
| OBJ | `LoadObj` | Positions, UVs, normals, triangulated polygons | Materials (`.mtl`) |
| STL | `LoadStl` | Triangles, binary and ASCII | Nothing else exists in the format |
| PLY | `LoadPly` | Positions, normals, UVs, faces; ASCII and binary, both endiannesses | Vertex colours (no colour channel in the vertex layout) |
| VOX | `LoadVox` | Voxels and the palette | Materials, the node graph |
| 3DS | `Load3ds` | Meshes and names | Materials, keyframes, cameras, lights |
| Collada | `LoadCollada` | `<triangles>`, `<polylist>`, positions, normals, UVs | Materials, the node hierarchy, skins |
| KMZ | `LoadKmz` | The Collada document inside | The KML: placemarks, overlays, coordinates |
| 3MF | `Load3mf` | Vertices and triangles | Materials, colour groups, build transforms |
| VRML 2.0 | `LoadVrml` | `IndexedFaceSet` coordinates and faces | Transforms, sensors, scripts, interpolators, inlines |
| SVG | `LoadSvg` | Paths, polygons, rects — extruded into solids | Arcs, strokes, fills, text, transforms |
| USD | `LoadUsd` | `def Mesh` prims from a `.usda` layer, or a `.usdz` holding one | Binary `.usdc`, layers, references, variants, materials |
| IFC | `LoadIfc` | `IfcTriangulatedFaceSet`, `IfcPolyLoop` | Swept solids, extrusions, booleans, the placement tree |

Compressed meshes are separate from the importers:

- `Scene.CreateGeometryFromDraco(bytes)` decodes a Draco buffer — the one inside
  a glTF `KHR_draco_mesh_compression` view, or what `draco_encoder` writes.
  Decoding only.
- `Geometry.Optimize()` and `Geometry.Simplify(ratio)` are meshoptimizer:
  reorder for the GPU cache, and generate the levels `SetLevelsOfDetail` wants.

---

## The limits worth knowing before you hit them

### USD: binary layers are not read

A `.usdz` is a zip, and what is inside it is usually `.usdc` — USD's own
memory-mappable crystal format — rather than `.usda` text. Several Apple tools
write binary by default.

Guessing at that format would produce wrong geometry rather than an error, so a
USDZ holding only `.usdc` reports it. Convert and load the result:

```
usdcat -o model.usda model.usdc
```

### IFC: the parametric half needs a geometry kernel

IFC describes most buildings parametrically — a profile swept along a directrix,
solids combined with booleans, all positioned by a placement tree. Evaluating
that is a geometry kernel, not a loader.

What loads is the half that is already polygons: `IfcTriangulatedFaceSet`, which
IFC4 exporters write when tessellation is on, and the older `IfcPolyLoop`. A file
written entirely parametrically says so and tells you to re-export with
tessellation, rather than importing an empty model that looks like a bug.

### SVG: arcs are skipped, not approximated

Curves are flattened into segments, because the triangulator works on polygons.
Arcs (`A` commands) are not, and a path using one is skipped — drawing a straight
line through where the curve belongs would be worse than saying the shape cannot
be read. A file of nothing but arcs reports that.

SVG measures y down the page; outlines are flipped on the way in so a logo is not
extruded upside down.

### Axis conventions

Three of these formats are Z-up and are turned Y-up on import, so models stand
the right way: **VOX**, **3DS** and **IFC**. The rest are already Y-up, or carry
no convention at all.

### Normals

STL, 3MF, VOX, VRML and IFC carry no usable normals, so they are computed from
the faces. An STL facet whose stored normal is zero — common, many exporters
write zeroes — takes its normal from the winding instead.

---

## Format-specific traps the loaders handle

These are the places a naive reader goes wrong, written down because the
symptoms are confusing:

- **STL binary files often start with `solid`.** That is the ASCII marker, and it
  also fits in the 80-byte binary header, so exporters put it there. Binary and
  ASCII are told apart by the file length (`84 + 50 × triangles`), not the first
  word. Reading such a file as text finds no triangles at all.
- **Collada's `<input>` and `<accessor>` are self-closing**, so an XML reader
  watching only for start events never sees the half of the format that describes
  how to read the other half.
- **IFC indices count from one.** Reading them as zero-based skews every face by
  one vertex and still produces a plausible-looking mesh.
- **SVG numbers run together.** `10-5` is two numbers and `.5.5` is two numbers;
  splitting on whitespace loses half the outline.
- **VRML commas are whitespace.** `point [ 0 0 0, 1 0 0 ]` and
  `point [ 0 0 0 1 0 0 ]` mean the same thing.

Faces pointing past the end of a vertex list are dropped rather than trusted, in
every loader that can encounter one. A corrupt file becomes a mesh with a hole
in it rather than an out-of-bounds read later.
