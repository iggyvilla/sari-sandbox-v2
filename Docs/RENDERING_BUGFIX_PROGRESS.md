# Rendering / item bugfix pass — progress log

Started 2026-09-29. Source recordings: `bug-recordings/` (repo root, untracked).
Rules followed: no Unity play mode (AGENTS.md) — verification was offscreen editor renders + scans via the unity MCP `eval`.
Related: `Docs/NEW_PRODUCT_PREFAB_GUIDE.md` (how to author prefabs so these don't come back).
**Nothing is committed.** `Assets/Resources/Prefabs` and `Assets/Resources/Materials` are gitignored, so the Coke/Royal prefab edits exist only in the local project.

## Status per issue

| # | Issue | Status | What changed | Still needs play-mode check |
|---|---|---|---|---|
| 1 | Prefab LODs too aggressive | Done | `ProductLodSetup` rebuilds LODGroup from GPU distances (LOD0<5 m, LOD1 beyond, never culled) | hand pulled back stays LOD0; pop at 5 m |
| 2 | Wrong pivot (Sting…) | Done | Was the GPU path, not the assets (see below) | Sting sits on the shelf |
| 3 | Transparent z-issues | Done (runtime rule) | `ProductMaterials`: depth write on for non-shell transparent parts, GPU + prefab | liquids/labels/highlights of rear bottles no longer show over front bottle |
| 4 | Coke rotation prefab vs GPU | Done | `COCACOLA_ORIGINAL_TASTE_290ML` root rotation baked into model child; GPU matrix = what `Instantiate` gives | Coke faces the aisle the same in both forms |
| 5 | Fridge handle release doesn't reset hand | Fixed by analysis (unverified) | `AgentControllerBase.UpdateHandControlMode` synced desired pose from the live pose every physics step while Shift held → overwrote `ResetHandPosition` from `ToggleGrip` before `ApplyDesiredHandPose` ran. Now syncs only when manual control starts | grab handle, open, release (Shift+Enter) → hand returns to default |
| 6 | Flipped labels (Royal) | Done | `ROYAL_TRU_ORANGE_500ML` label reflected (`MirroredMeshBaker`) + model child rotated +90° Y to keep the front facing the same way | Royal reads correctly and faces the aisle |
| 7 | Invisible LOD1 | Done | Prefab LODGroups had empty/null slots (COBRA_PLUS_SMART no LOD2, HAPPYFLAKES LOD1 empty…); rebuilt at runtime with only valid LODs | Cobra/Happyflakes visible at all distances |
| 8 | Water flicker on pull-away | Mitigated (unverified) | Prefab→GPU handoff overlap is now 1 frame (`restorePoolReturnDelayFrames`, was 0.05 s ≈ 8 frames at 160 fps) + LOD/shadow/material parity | no flash/flicker when hand leaves a stack |
| 9 | Shadows differ prefab vs GPU | Done | Prefab renderers now use `BatchInstancer.ProductShadowMode` (Off), same as GPU draws | no shadow jump |

## Root causes

- **#2/#4 (GPU placement)**: `ItemSpawner.BottomPivotOffset` used `mesh.bounds.min.y * lossyScale.y` (ignores LOD rotation, e.g. Sting LOD0 is (270,290,0)) and only `lod0.position` was uploaded, so LOD1+ offsets were ignored. Asset pivots are fine: every prefab's LOD0 bottom is at y≈0 (only CHIPSY_..._25G −4 mm and OISHI_POTATO_FRIES_50G −7 mm, left as is).
- **#4 Coke**: root rotation (0,90,0) dropped by `Instantiate(prefab,pos,rot)`; its LOD yaw with the root kept (205°) matches siblings (191–213°), so the GPU look was the intended one.
- **#7**: see table. In the GPU path LOD0/LOD1 were non-empty for all 240 items in offscreen renders.
- **#3**: instanced transparent draws are unsorted; liquids/labels had ZWrite off so a rear bottle's liquid/label/plastic highlight blended over the front one. URP Lit re-derives `_ZWrite` on import, so asset edits revert — the rule is applied at runtime.
- **#5**: see table.

## Code changes

- `GPUOptimizations/LodHierarchy.cs`: `SpawnRelativeMatrix(product, lod)`.
- `ShelfItemHandlers/ItemSpawner.cs`: `CreateProductDrawTemplate` uses the full LOD matrix (position offset + rotation + scale per LOD); `BottomPivotOffset` removed.
- `GPUOptimizations/BatchInstancer.cs`: `LodRenderData.offset` (48-byte struct), per-LOD bounding sphere, `ProductShadowMode`.
- `Materials/Shaders/shaderGraphSupport.hlsl`: `LodRenderData.offset` added to position (also used by `Resources/LidarLinearDepth.shader` via include).
- `GPUOptimizations/GPUInstanceTracker.cs`: `ActiveLodCount`, `LodMaxDistance`, static `BuildLod`, depth-write on material clones.
- New `GPUOptimizations/ProductLodSetup.cs`, `ProductMaterials.cs`; `ShelfItemHandlers/ProductPrefabs.Spawn` is the single spawn path (used by `ItemPoolingManager`, `RetailItemRuntimeService`).
- `ItemPhysics/RetailItemRuntimeService.cs`: frame-based handoff delay.
- `AgentControllerBase.cs`: hand pose sync fix.
- Editor tools: `Assets/Editor/ProductLodPreview.cs`, `ProductPrefabValidator.cs`; `MirroredMeshBaker` list += Royal-500.
- Local-only assets: `COCACOLA_ORIGINAL_TASTE_290ML.prefab` (model child rotation), `ROYAL_TRU_ORANGE_500ML.prefab` (+ `Assets/Resources/Meshes/Unmirrored/ROYAL_TRU_ORANGE_500ML_LOD{0,1}_LabelFix.asset`, untracked but not ignored).

## Verification done (editor, no play mode)

- Prefab-vs-GPU offscreen render of LOD0/LOD1 for all 240 products: 0.000 pixel diff before the transparency change; afterwards ≤ 0.026 except water/juice items (POCARI 0.09, SUMMIT_500ML 0.12) where the GPU tiles now use depth-writing clones (expected).
- LOD switch distance probed with colored unlit materials: switches at 5 m for both `lodBias` 1 and 2 (`Quality` levels use both).
- Label mirroring scan (UV chirality): all 240 correct now except HEINEKEN (intentional `_LOGO_MIRRORED` texture).
- Z-order test: 6 overlapping bottles, both draw orders: 3833 px differed → 328 px.
- Validator: 11 remaining findings, all minor (see `ProductPrefabValidator.Run()`): 2 tiny pivots, 5 empty LODGroup slots (handled at runtime), 3 non-uniform root scales (HAPPYFLAKES, MAGNOLIA_DAILYQUEZO, REBISCO — GPU matrix handles them, cleaner to move scale to the model child), Coke-290 LOD0 mesh not readable (submeshes not merged).

## Known gaps / notes for next session

- Not run in play mode: items 5 and 8 are reasoned fixes, not observed ones. If #5 persists, look at `IKAgentController` (hand target vs `ikHandColliderSource`) and `HandleManualHandControls` ordering.
- `MirroredMeshBaker.ReflectHorizontally` can rotate which side faces the aisle (Royal-500's front moved 90°); previously fixed products (MOUNTAIN_DEW_500/ZERO, VITAMILK, PASCUAL_GREEK, POPPLE) were not re-checked for facing.
- Reflection-probe selection differs between prefab (per object) and indirect draws (batch bounds at origin) — possible remaining "shine" difference between forms, unverified.
- `GPUInstanceTracker` rebuilds all culling buffers on any add/remove (`InstanceCullingSystem.Prepare`) — a hitch source when many stack members swap at once; not addressed.
- Console noise seen from earlier play sessions (not touched): `Cannot set the parent of the GameObject '<item>' while it is being destroyed` from `ItemBBoxPhysicsProxy.OnDestroy → ReleaseActivePhysicsPreview` during teardown.
- Product `LODGroup`s in assets are still the original (bad) ones — runtime overrides them; fix in source only if the editor view matters.

## Second pass (same day) — user follow-ups

| # | Issue | Status | What changed | Play-mode check |
|---|---|---|---|---|
| A | Sting logo drawn in front of fridge glass when close | Fixed by analysis | `DoorGlass.mat` and `STING_330ML_LABEL` were both queue 3001, so draw order between the instanced label and the glass depended on camera distance to the batch bounds (origin) — not on LOD. Door glass → queue 3100 (asset, tracked). `ProductMaterials` now pulls depth-writing textured labels down to queue 2995 (after liquids 2990, before shells 3000) | logos tinted by the glass like the other labels, at every distance |
| B | Hand slams the door shut on handle release | Fixed by analysis | `AgentControllerBase`: hand/door layer collision stays ignored for `DoorReleaseGraceSeconds` (0.4 s) after release (per-agent timer, still ref-counted globally; expires in `FixedUpdate`) | release handle: door stays where it is |
| C | Coke Light bottom looks jagged | Fixed | Mesh, not a rendering bug: the body ("LIQUID_BLACK" submesh) has a coarse petaloid base — valley points 8 mm up between feet, waist ring narrower than the body. `BottleBaseSmoother` (new editor tool) rounds the outer wall below 16.5 mm to PEPSI_500ML's profile, flattens the feet, drops the valleys, recalculates normals. Applied to COCACOLA_LIGHT_500ML and COCACOLA_ZERO_SUGAR_500ML (same defect), LOD0+LOD1. Meshes: `Assets/Resources/Meshes/Fixed/*_BaseFix.asset` (rebuilt from the FBX on each run) | base looks round from shelf angle |
| D | Cans "explode" when they become physics objects | Mitigated (unverified) | See below | stacked cans (555_FRIED_SARDINES_ESCABECHE_155G) wake without launching |
| E | Some plastics (Royal Tru Orange 500 ML) shinier as prefab than GPU | **Not reproduced** | see below | — |
| F | NATURE_SPRING_500ML (and other water) flickers in place | Fixed (verified in editor) | Culling compute reshuffled draw order every frame — see below | water stacks stop flickering |

### D — cans
Investigated 555_FRIED_SARDINES_ESCABECHE_155G first: nothing malformed. Box collider (57.3×85.5×57.3 mm) equals the mesh bounds and `RetailItemData` dims exactly (0 mm overlap, stacked exactly touching); Rigidbody identical to its siblings. Its one difference from TAUSI is a *solid* `Barcode` MeshCollider (TAUSI's is a trigger). 190 of 288 barcode colliders in the catalogue are solid convex hulls of a flat plane. Changes (applied to every physics prefab):
- `ProductLodSetup.Apply`: all `MeshCollider`s (barcodes) are triggers. Scanning still works: `BarcodeScanner` uses `OnTriggerEnter`/`Physics.Raycast` and `Physics.queriesHitTriggers` is on; held items were already switched to triggers the same way.
- `ItemPoolingManager.GetOrCreate`: `ContinuousSpeculative` instead of `ContinuousDynamic` (which sweeps resting, exactly-touching stack members against each other), `angularDamping` 0.05→0.5, `maxAngularVelocity` 50→8, `sleepThreshold` 0.005→0.02; solid colliders get `contactOffset` 3 mm (project default 10 mm is a fifth of a can).
- Not changed but worth knowing: `MAGNOLIA_DAILYQUEZO_160g` has a 54 mm/collider overlap per stacked unit (box 118 mm vs dims 63.8 mm) — guaranteed to fight when stacked; several bottles have rotated-AABB dims ~1.3× their collider (harmless: more spacing). Rows/stacks are spaced from `MeshRenderer.bounds` of the first MeshRenderer, not the collider.
- If it still happens: capture which items launch and whether it's at wake-up (initial overlap) or after a touch; check `ShelfItemPhysicsStack.ActivatePhysicsPreviews` order and per-body `solverIterations` (12) / `maxDepenetrationVelocity` (0.25).

### E — plastic shine (not reproduced)
Offscreen A/B tests (edit mode, preview scene, no probes) found no difference: URP Lit vs `URPLit_Procedural` (ROYAL_TRU_ORANGE_500ML, NATURE_SPRING_500ML, COCACOLA_REGULAR_500ML: mean brightness identical under directional + point light, with and without ambient); normal draw vs `DrawMeshInstancedIndirect` with origin-centred bounds and a point light 15 m from the origin (identical); Forward vs Forward+ (identical); a stale per-object light list test (identical). The Dev Scene has no reflection probes, so reflections are skybox for both. Remaining suspects need an in-game A/B: main-light shadows (prefab used to cast/receive differently), SSAO, APV sampling, or the outline/bbox cube parented under physics prefabs. Send a screenshot pair (same item, prefab vs instanced, same view) to continue.

### F — water flicker (WATER-BOTTLE-FLICKER.mov)
Frame diffs of the recording (camera static) show the change confined to single overlapping bottles of a Nature Spring stack: the bottle behind flips between visible-through-the-water and hidden. Cause: `FrustumCullingFilterer.compute` appended visible instances with `InterlockedAdd`, so the order of instances inside every (batch, LOD) list is random and different on every re-cull (measured in editor: 100 % of positions differ between two culls of the same view 1e-4 m apart). Transparent instances of one product draw in that order; the water liquid (alpha 0.43, depth write since the z-order fix) therefore shows/hides the bottle behind it depending on the order. Depth write made the two states more different, but blending order was random before too.
Fix: `Cull` now only writes each instance's LOD to `instance_lods`; a new `Compact` kernel (one 128-thread group per batch, packed 8-bit-lane scan per chunk) writes the visible lists in ascending instance index. Draw order is now identical every frame. Instance order is row 0 = back row, so back-to-front, which is the right blend order from the aisle. `BatchCullData.pad` became `firstInstance`; viewers gained an `instanceLods` buffer.
Verified in editor (edit mode, real compute dispatch, 6 batches incl. sizes 1, 7, 128, 129, 300, 600): lists sorted ascending, identical across re-culls of the same view, and membership/counts equal a CPU reference (frustum + LOD distance). Not exercised offscreen: the Hi-Z occlusion re-cull (same dispatch path) and lidar range culling — check both in play mode. `ClearCounts` is now redundant (Compact writes every count) but kept.
