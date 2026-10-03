# Guide: making a new product prefab

Product prefabs live in `Assets/Resources/Prefabs/Products/<ITEM_ID>.prefab` (loaded by `ProductPrefabs`).
Each product exists in two forms: a **GPU instance** (shelf, `BatchInstancer`) and a **physics prefab**
(hand nearby / held / dropped, spawned via `ProductPrefabs.Spawn`). They must look identical, or you get
pops, flicker and shadow jumps. Follow these rules and run the checks at the bottom.

> `Assets/Resources/Prefabs` and `Assets/Resources/Materials` are gitignored — prefab/material edits are local only.

## 1. Hierarchy

```
<ITEM_ID>                 root: Rigidbody, identity rotation, uniform (ideally 1) scale
└─ <ITEM_ID>              model child: LODGroup, BoxCollider (solid footprint)
   ├─ <ITEM_ID>_LOD0      MeshFilter + MeshRenderer (closest, < 5 m)
   ├─ <ITEM_ID>_LOD1      MeshFilter + MeshRenderer (5 – 15 m, farthest drawn)
   ├─ (_LOD2 / _LOD3)     optional; ignored while `GPUInstanceTracker.enableLod2AndLod3` is off
   ├─ Barcode             disabled renderer, tag "Barcode"
   └─ DecalProjector      expiration date
```

- `LodHierarchy.ResolveLodTransforms` finds children by the `_LOD0.._LOD3` name suffix. A missing LOD reuses the previous one.
- LOD0 and LOD1 need a **readable** mesh (else `SubmeshMerger` skips it) and **submesh count == material count**.
- LOD0 and LOD1 should share the same rotation/position/scale (any difference shows as a pop at 5 m).

## 2. Pivot, rotation, scale

- **Pivot = bottom centre** of the LOD0 mesh in root space (`min y ≈ 0`, x/z centred). The shelf spawner places `spawnPosition` at the item's base.
- **Never rotate or non-uniformly scale the root.** `Instantiate(prefab, pos, aisleRot)` *replaces* the root rotation, the GPU path does not — this caused the Coke-290 mismatch. Put rotation/scale on the model child or the LOD children.
- Aisle facing is applied in code: `ItemSpawner` uses `aisleRot = Euler(0, DegreesToAisle(), 0)`; GPU rotation = `aisleRot * LOD child rotation`, physics rotation = `aisleRot` on the root. Do not add your own facing offset.
- The GPU transform of each LOD is computed by `ItemSpawner.CreateProductDrawTemplate` from the LOD child's full matrix (`LodHierarchy.SpawnRelativeMatrix`): any local position/rotation/scale on the LOD child (e.g. a centre-pivot mesh lifted by half its height) is honoured, per LOD. No manual "pivot fix" is needed as long as the prefab looks right in the editor.

## 3. LODs and shadows (automatic)

`ProductLodSetup.Apply` runs on every spawned physics prefab and:
- rebuilds the `LODGroup` from `GPUInstanceTracker` distances (LOD0→LOD1 at 5 m; last LOD never culls) — authored `LODGroup` thresholds are overwritten, so don't tune them;
- disables LOD2/LOD3 renderers the GPU path doesn't draw;
- sets shadow casting to `BatchInstancer.ProductShadowMode` (Off) so the shadow doesn't jump when the item swaps between forms. Enabling shadows for GPU instances instead would need a shadow pass per light/cascade and culling against shadow frustums — expensive.

## 4. Materials and transparency (the z-fighting / "shine in front" bug)

GPU instances of one product are drawn in a single indirect call (per submesh), with no per-bottle sorting. A transparent rear bottle can therefore blend over the bottle in front. Rules:

| Part | Surface | Notes |
|---|---|---|
| Body, cap, label, opaque print | **Opaque** | Preferred for everything that isn't see-through. Opaque parts are merged by `SubmeshMerger`. |
| Liquid | Transparent OK, queue 2990 | Depth write is forced on at runtime (see below). |
| Blended label/logo (textured, alpha) | Transparent OK, queue ≤ 3001 | Prefer alpha-clip/opaque. Depth write forced on. |
| Clear plastic / glass shell | Transparent, queue 3000, alpha **< 0.3**, no texture | Only these keep ZWrite off (`ProductMaterials.ShellMaxAlpha`). Draws last, so highlights sit on top. |

- **URP Lit re-derives `_ZWrite` from the surface type on import**, so setting `_ZWrite` in a material asset does not stick. `ProductMaterials` applies the rule at runtime: GPU material clones (`GPUInstanceTracker.CloneMaterialsForInstancing`) and physics prefab renderers (`ProductLodSetup`) both get depth-writing copies of transparent materials that aren't shells. Same rule → same look in both forms.
- Queue order (enforced by `ProductMaterials`, don't fight it): opaque 2000 < liquids 2990 < blended labels/logos 2995 < shells 3000 < fridge door glass 3100 (`Assets/Materials/DoorGlass.mat`). Anything at or above the glass queue draws unsorted against it (the Sting-label-over-glass bug).
- Editor test (2026-09-29): 6 overlapping bottles rendered in both draw orders: 3833 px differed with the old materials, 328 px with the rule (mostly semi-transparent water).
- Water liquids (alpha ≈ 0.43) still show minor order dependence; make a liquid opaque if it doesn't need to be see-through.

## 5. Labels

- Label text must read correctly from outside. Run the validator; it flags label submeshes whose UV chirality is mirrored (only `ROYAL_TRU_ORANGE_500ML` was, now fixed; textures named `*_MIRRORED` are exempt).
- To fix one: add it to `MirroredMeshBaker.MirroredLabelProducts` and run *Tools ▸ Products ▸ Fix Mirrored Labels*. **The tool reflects across the mesh axis nearest world X, which can rotate which side faces the aisle.** Afterwards render the item at yaw 90 (`ProductLodPreview.Render(new[]{id}, dir, 90f, 0f)`) and compare with the pre-fix front; rotate the model child about Y if the front moved (Royal-500 needed +90°).
- Negative LOD scales aren't supported by the GPU path; bake them with *Tools ▸ Products ▸ Bake Negative LOD Scales*.

## 5b. Colliders and physics

- Root `Rigidbody` + one solid `BoxCollider` whose bounds equal the LOD0 mesh bounds (the shelf spaces items from `MeshRenderer.bounds`; a taller collider makes stacked cans overlap and launch).
- `Barcode` MeshCollider: any setting works — `ProductLodSetup` forces every MeshCollider to trigger at spawn (solid flat hulls destabilise stacks). Scanning uses triggers/raycasts.
- Pool defaults (`ItemPoolingManager`): speculative CCD, 8 rad/s max spin, 0.5 angular damping, 3 mm contact offset. Don't set these on the prefab; they're overwritten.
- Keep the bottle base smooth: petaloid bases need enough rings; a coarse one (Coke Light/Zero) looks like shards. `BottleBaseSmoother` shows how to round one.

## 6. Lighting

- Product lighting comes from the scene (Adaptive Probe Volumes + reflection probes). Don't add per-item lights or light-probe proxies.
- Possible remaining difference (not verified in play): physics prefabs pick a reflection probe by object position, indirect draws by the batch bounds (centred at the origin). If plastic reflections change when an item swaps form, look here first.

## 6b. Texture arrays and render presets (on by default: Balanced)

Per-machine render settings (`RenderingSettings`), changed live in the Store Builder's *Agent Settings* menu (Render Preset, Texture Arrays, Indirect Draw, Array Resolution):

| Preset | Indirect draw | Texture arrays | Cost / gain (full store) |
|---|---|---|---|
| Low memory | on | off | no extra memory, ~0.6 ms/frame faster than the legacy path |
| Balanced (default) | on | 2K | ~+290 MB, ~1 ms/frame faster, SetPass 1250 -> 750 |
| High quality | on | 4K | ~+1 GB, same speed, sharper only in extreme close-ups |

Any other toggle combination reads *Custom* (all off = the legacy path, kept for debugging; 1K is ~+70 MB). Choices are saved in PlayerPrefs (`sari.render*`) and applied before products spawn. Precedence: command-line flags (`-sariRenderPreset low|balanced|hq`, then `-sariTextureArrays`, `-sariIndirectArgs`, `-sariTextureRes`) > PlayerPrefs > the `GPUInstanceTracker` inspector values (defaults = Balanced). Flags are per-launch only; they are not saved.

**Texture arrays** (`GPUInstanceTracker.useTextureArrays`):

Opaque, textured parts can sample one packed `Texture2DArray` instead of their own material (`ProductTextureAtlas`, `SubmeshMerger`). A part is packed when its albedo is **BC1 sRGB, power-of-two, ≤ 4096, full mip chain, Repeat + Bilinear, min side ≥ 4 after downscaling**; anything else (transparent, normal-mapped, other formats) silently stays on the classic per-material path.

- Layer size = `GPUInstanceTracker.textureArrayResolution` (1K / 2K default / 4K, `-sariTextureRes 1024|2048|4096`). Bigger sources are copied from the matching mip (no resize, no recompression); smaller ones keep their size. Physics prefabs and classic parts always use the full-res originals.
- Only products that get a GPU batcher are packed, and all batchers created in a frame are packed together (`GPUInstanceTracker.FlushPending`). Products that appear later (Store Builder, random fills) fill free space or append a new array; existing batchers never change.
- Changing a setting at runtime rebuilds every batcher (~0.2 s for the full store) and frees the arrays/materials it no longer needs.
- Labels that tile (UVs far outside 0..1) get a full-width/height slot so hardware Repeat still wraps; keep tiling to the textures that need it, they cost a whole layer row/column.
- Never call `Apply` on the arrays: it uploads an empty CPU copy and spikes memory by ~3x the array size.
- Check copies/visual parity in edit mode with `ProductTextureAtlasCheck.BuildAll(layerSize)` / `VerifyCopies` / `RenderParity`.

## 7. Checklist / tools

1. *Tools ▸ Products ▸ Validate Product Prefabs* (`ProductPrefabValidator.Run()`): root rotation/scale, pivot, missing LOD mesh/renderer, submesh/material mismatch, empty `LODGroup` slots, mirrored labels.
2. `ProductLodPreview.Render(ids or null, outDir, yaw, aisleYaw)` (also *Tools ▸ Products ▸ Render LOD Preview (all)*): offscreen strips `prefab LOD0 | GPU LOD0 | prefab LOD1 | GPU LOD1 | prefab LOD2 | prefab LOD3` + `metrics.tsv` (coverage, prefab-vs-GPU pixel diff). Prefab vs GPU diff should be ≈ 0 (small values on transparent items are expected: GPU tiles use depth-writing clones). No play mode needed.
3. In play mode: hover a hand over the item and pull it back — no pop in size, rotation, shadow, lighting; no flicker.
