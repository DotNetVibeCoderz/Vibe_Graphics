# Water, fire and particles / Air, api dan partikel

`ThreeNet.Effects` holds the three effects that are more than a material: **water** that is actually
simulated, **fire** that is actually a volume, and a **particle system** with the knobs an effect needs. Each
one is a small class you drive from your own update loop, and the WGSL behind it is public, so you can copy a
shader, change the part you want and hand it back.

`ThreeNet.Effects` berisi tiga efek yang lebih dari sekadar material: **air** yang benar-benar disimulasikan,
**api** yang benar-benar volume, dan **sistem partikel** dengan kendali yang dibutuhkan sebuah efek. Semua
WGSL-nya publik, jadi bisa disalin dan diubah sesuai kebutuhan.

Made by Gravicode Studios, led by Kang Fadhil.

| Scene in DemoGraphics | What it shows |
|---|---|
| `WAT` Water simulation | Ripples, caustics, sky reflection, floating things |
| `FIR` Volumetric fire | A ray marched flame, embers, smoke, a guttering light |
| `VFX` Particle laboratory | Emitter shapes, curl noise, attractors, colour over life |

---

## Water

```csharp
using ThreeNet.Effects;

WaterSurface water = new(scene, center: Vector3.Zero, size: 12f, floorHeight: -1.6f)
{
    Style = WaterStyle.Pool,
    SunDirection = sunDirection,
};

// Anything under the water takes the floor shader, and so catches caustics.
Material tiles = water.AddFloor(MaterialOptions.Pbr(Colors.White, 0f, 0.55f) with { BaseColorMap = checker });

// Each frame:
water.Update(deltaSeconds);
boat.Position = boat.Position with { Y = water.HeightAt(boat.Position.X, boat.Position.Z) };
```

**What is simulated.** A height field, stepped with the 2D wave equation - two numbers per cell, height and
vertical velocity:

```text
velocity += c² ∇²h;   velocity *= damping;   height += velocity
```

This is the model from Evan Wallace's WebGL Water, by way of
[jeantimex/threejs-water](https://github.com/jeantimex/threejs-water). It buys the things a sum of sine waves
cannot do: a ripple **spreads** from where it started, **reflects** off the walls, **interferes** with
another ripple and **dies away**. A drop pushed in at one corner arrives at the far wall a moment later and
comes back.

The field runs on the CPU, at `WaterSimulation.StepRate` fixed steps a second, and is published as an
`Rgba32Float` texture - R height, G velocity, B and A the x and z of the normal, the same layout the
reference uses. A 128×128 field is ~16k cells and costs well under a millisecond. Keeping it on the CPU is
also what makes `HeightAt` and `NormalAt` possible, which is what makes a boat bob.

**What the surface shader does.** The mesh is lifted by the height field in `user_vertex`; the normal comes
from the same field in `user_surface`, with two scrolling noise fields adding chop the grid is too coarse to
hold. Then:

- **Fresnel** against water's own reflectance: nearly clear face on, nearly a mirror at a glancing angle.
- **Reflection** from `sky_color(reflect(...))`, the same function the sky pass uses, so a lake mirrors the
  sky actually overhead - including the sun, the clouds and, at night, the moon.
- **Absorption** by Beer-Lambert along the path to the bottom, so shallow water keeps its own colour and
  deep water loses everything but the blue. This is also what makes the water go clear at the edge.
- **Foam** where the surface is steep and along the shore, where the bottom comes up to meet it.

**Caustics.** The floor shader walks a sunbeam *backwards*: from the floor fragment up to where the ray
crossed the surface, bends it there with Snell's law, follows it back down, and compares the area one pixel
covers before and after with `dpdx`/`dpdy`. Where the waves act as a lens the patch shrinks and the same
light lands in a smaller place - the bright web on the bottom of a pool. That is the differential area method
the reference uses, evaluated from the receiving side.

| Type | What it is for |
|---|---|
| `WaterSimulation` | The field on its own: `AddDrop`, `Step`, `SampleHeight`, `SampleNormal`, `RainRate`, `Damping` |
| `WaterSurface` | Simulation + mesh + material + caustics, wired together |
| `WaterStyle` | `Pool`, `Lake`, `Ocean`, or your own colours, absorption, foam and caustics |
| `EffectShaders.Water`, `.WaterFloor`, `.HeightField` | The WGSL, to copy or to read |

```csharp
water.Splash(hit.Point, radius: 0.26f, strength: 0.11f);  // a stone goes in
water.Simulation.RainRate = 160f;                         // it starts raining
water.Simulation.Damping = 0.9985f;                       // the swell holds longer
```

---

## Fire

```csharp
FireEffect fire = new(scene, position: Vector3.Zero, size: new Vector3(1.1f, 1.8f, 1.1f))
{
    Style = FireStyle.Campfire,   // Torch, Candle, Magic
};

fire.Update(deltaSeconds);
light.Intensity = 14f * fire.Flicker;   // the room gutters with the flame
```

**A flame is a volume, not a picture.** The box is drawn front faces only; each fragment marches 24 steps
into it and asks a flame function for colour and density at every step. Because it is a volume it has depth,
it reads correctly from any angle, it does not turn to face you, and it needs no texture at all.

The technique is [THREE.Fire](https://github.com/typeWolffo/THREE.Fire) and the "real-time procedural
volumetric fire" work behind it:

- **Turbulence** - octaves of `|simplex noise|`, whose creases are what read as torn flame edges.
- **The noise field falls as the fire rises**, which is what makes the flame climb rather than shimmer.
- **An envelope** broad at the base and drawn to a point at the top, torn by the turbulence.
- **A temperature ramp** from white through yellow and orange to the red that is nearly smoke. The reference
  looks this up in a texture; here it is built in code, so a fire ships with nothing beside it.

The material is `AlphaMode.Additive` with no depth writing: fire is light, not paint, so it never darkens
what is behind it and never needs sorting against other flames.

A flame is not a light source. Put a `Light.Point` inside one and multiply its intensity by `fire.Flicker`,
which wanders the way a flame does - it eases towards a new target several times a second rather than
strobing.

| Knob | What it does |
|---|---|
| `Magnitude` | How far the turbulence tears the flame apart: low is a candle, high is a bonfire |
| `Speed` | How fast the flame climbs |
| `NoiseScale` | Size of the turbulence; larger numbers make a finer, busier flame |
| `Brightness` | Over 1 the flame blooms |
| `Tint` | Multiplied over the flame's own ramp - `FireStyle.Magic` is this and nothing else |

---

## Particles

```csharp
ParticleEffect embers = new(capacity: 2400)
{
    Shape = EmitterShape.Disc,
    Origin = new Vector3(0f, 0.3f, 0f),
    Rate = 260f,
    Lifetime = 2.2f,
    Gravity = new Vector3(0f, 1.1f, 0f),   // embers float before they cool
    Turbulence = 2.2f,
    StretchBySpeed = 0.05f,
};

Shader sprite = scene.CreateShader(EffectShaders.Particle);
Material material = scene.CreateMaterial(
    ParticleEffect.GlowMaterial(sprite, birth: new Vector3(1f, 0.6f, 0.2f), death: new Vector3(0.5f, 0.05f, 0f)));
Geometry geometry = embers.CreateGeometry(scene);
scene.AddMesh(geometry, material);

// Each frame: simulate, then rebuild the quads facing the camera.
embers.Update(deltaSeconds, totalSeconds);
embers.Upload(geometry, camera.WorldMatrix);
```

One system is one draw call. The simulation is the familiar emit-age-integrate-retire loop, with the parts
that make an effect look deliberate rather than sprayed - the vocabulary
[Three VFX](https://github.com/mustache-dev/Three-VFX) settled on:

| Feature | Property |
|---|---|
| Emitter shapes | `Shape`: `Point`, `Box`, `Sphere`, `Disc`, `Cone`, `Ring`, with `SurfaceOnly` |
| Radial launch | `Radial` - fire along the spawn offset, which is what an explosion does |
| Curl noise turbulence | `Turbulence`, `TurbulenceScale`, `TurbulenceSpeed` - divergence free, so particles swirl through it instead of piling up |
| Attractors | `Attractors`, up to four, each a point or a vortex, with a radius |
| Collision | `FloorHeight`, `Bounce` |
| Stretch by speed | `StretchBySpeed` - a fast particle becomes a streak along where it is going |
| Colour over life | `custom0` at birth, `custom1` at death, mixed by the particle's own age |
| Bursts | `Burst(count)` for an impact or a puff |

**How colour over life works without a vertex colour channel.** A vertex carries a position, a normal, a uv
and a tangent and nothing else, so the age (0 at birth, 1 at death) and a per particle random ride *in the
uv*: the quad corner is squeezed into the fraction 0.25..0.75 and the two bytes are added on as whole
numbers, which `float32` holds exactly. `EffectShaders.Sprite` unpacks them, and `Particle` and `Smoke` both
build on it.

Use `AlphaMode.Additive` (`GlowMaterial`) for anything that glows and `AlphaMode.Blend` (`SmokeMaterial`) for
anything that does not.

**Where this caps out.** Everything runs on the CPU and the geometry is re-uploaded every frame, which is
honest and portable but tops out in the low thousands per system. A compute path would need compute pipelines
in the core first; see `PLAN.md`.

---

## Both kinds of fire, side by side

`FIR` and `VFX` in DemoGraphics make fire two different ways on purpose. The ray marched volume has depth and
reads right from any angle, but it cannot throw a spark across the yard. The billboards can, but a flat quad
cannot look like the inside of a flame. Real fires in `FIR` use both: a `FireEffect` for the flame, a
`ParticleEffect` for the embers and another for the smoke.
