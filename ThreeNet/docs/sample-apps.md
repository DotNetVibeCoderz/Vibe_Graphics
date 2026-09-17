# Sample apps / Aplikasi contoh

Two larger applications that show Three.Net in a realistic setting. Both are Avalonia 12 apps hosting a
`ThreeNetView`, and both use cascaded shadows, SSAO, bloom, PBR materials and procedurally generated textures.
Detailed 3D assets were generated with **Rodin (Hyper3D)** through the `rodin` MCP server and ship as GLB files.

Dua aplikasi yang lebih besar untuk menunjukkan Three.Net dalam skenario nyata. Keduanya aplikasi Avalonia 12
dengan `ThreeNetView`, memakai bayangan bertingkat (CSM), SSAO, bloom, material PBR dan tekstur prosedural.
Aset 3D detail dibuat dengan **Rodin (Hyper3D)** lewat MCP server `rodin` dan disertakan sebagai file GLB.

Made by Gravicode Studios, led by Kang Fadhil.

---

## Three.Net Motocross (`samples/ThreeNet.Samples.MotoCross`)

![Motocross](images/moto-rodin.png)

```bash
dotnet run --project samples/ThreeNet.Samples.MotoCross
```

| Feature | Implementation |
|---|---|
| Circuit | Closed Catmull-Rom loop resampled per metre; kickers, tables, a double, rollers and whoops; banked berms (`Game/Track.cs`) |
| Terrain | Analytic height field: noise hills blended into the graded corridor; coarse landscape mesh + fine track ribbon (`TerrainField`, `WorldBuilder`) |
| Rider & bike | Rodin generated GLB (`Assets/rider-bike.glb`), scaled to 2.15 m; primitive rig as fallback (`BikeRig`) |
| Scenery | Rodin GLBs `pine-tree`, `oak-tree`, `boulder`, `tire-stack` placed with a `ModelLibrary` (import once, `Node.Clone`); trees far from the track skip shadow maps (`Node.SetShadowsRecursive`); primitives as fallback |
| Physics | Two contact points, suspension, surface dependent grip, slides, air control, landing impacts (`BikePhysics`) |
| Effects | Billboard dust/roost pool, camera shake, speed FOV, headlight and floodlights at night |
| Sound | Real-time synth on `winmm` waveOut: engine revs, wind, tyre scrub, landing thumps, lap chimes (Windows) |
| Race | Ordered checkpoints, 3 laps, best lap, air time, mini map, countdown |

Controls / kontrol: **W/↑** gas · **S/↓** rem · **A/D** belok · **←/→** kontrol di udara · **Space** hop ·
**C** kamera · **T** waktu · **R** ulang · **Backspace** respawn · **H** bantuan.

## Griya Nusantara Residence – Home Complex CAD Viewer (`apps/HomeComplexCad`)

![Estate](images/cad-estate.png)
![Interior](images/cad-interior.png)

```bash
dotnet run --project apps/HomeComplexCad
# start at a place and time / mulai di lokasi dan jam tertentu
dotnet run --project apps/HomeComplexCad -- --location "Ruang Bermain" --group Edelweis --hour 20 --mode fp
```

| Area | Contents |
|---|---|
| Estate | Entrance gate with lit sign and guard post, tree lined boulevard, two streets with lane markings, sidewalks, kerbs, 24 street lamps, street and direction signs rendered from text |
| Houses | 12 lots: single and two storey, gable or flat roofs with solar panels, walls with real window/door openings, glazing, cladding, fence, carport with a car, garden, house numbers |
| Show units | Dahlia (type 90), Edelweis (type 165, 2 floors, stairs), Flamboyan (type 200 with private pool): living and family rooms, kitchen with island, bathrooms, master/guest bedrooms, kids' playroom, room lights |
| Facilities | Shop row (lit signage), clubhouse with 18 × 8 m pool and kids' pool, park with pond, bridge, gazebo and playground, sports court |
| Camera | First person (walks, follows floors and stairs, stops at walls), third person avatar, free fly; smooth flights to locations |
| Time of day | Sun path from sunrise to night, colour temperature, sky and ambient, fog; lamps, room lights, windows and signs switch on after dusk; auto cycle |
| Information | Land and building data for whatever you look at or click: type, block, status, price, land/building area, lot size, rooms, certificate, facing, power, water, construction and features |

Rodin assets: `sofa`, `bed`, `kitchen-island`, `car`, `tree`, `street-lamp`. Each GLB is imported once into a
hidden prototype and placed with `Node.Clone`, so instances share GPU buffers and textures.

Aset Rodin diimpor sekali ke prototipe tersembunyi lalu ditempatkan dengan `Node.Clone`, sehingga semua instance
berbagi buffer GPU dan tekstur yang sama.

## Library additions made for these apps / Tambahan library

- `Node.Clone(parent)` / `tn_node_clone`: deep copies a subtree sharing geometry, materials and textures.
- `Node.Light` getter / `tn_node_get_light`: read back a node's light to toggle it.
- ABI version 3.
