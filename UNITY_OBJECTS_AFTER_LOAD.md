# Unity Objects After Load

**Measured:** 2026-07-31 01:31:48 UTC (2026-07-31 09:31:48 Asia/Manila)  
**Project:** Sari Sandbox V2, current workspace source  
**Scene/store:** `Dev Scene` / `Store 2 v2`  
**Player:** Unity 6000.0.80f1, WindowsPlayer, D3D11  
**Source audit:** `../sari-sandbox-old/PERFORMANCE_AUDIT.md`

## Summary

The settled V2 player had **20,217 loaded `UnityEngine.Object` instances** after the store loaded.

Of those loaded objects:

- **3,356** were GameObjects (**16.60%**).
- **10,217** were Components (**50.54%**).
- **6,644** were other Unity objects (**32.86%**), including materials, meshes, textures, shaders, sprites, text assets, and other engine objects.

The live scene and `DontDestroyOnLoad` scenes contained **1,558 GameObjects**:

- **1,429 active in the hierarchy** (**91.72%**)
- **129 inactive in the hierarchy** (**8.28%**)

The remaining **1,798 loaded GameObjects** were not attached to a valid runtime scene. They are primarily loaded prefab/asset objects and engine-owned objects. Similarly, **5,281 of 10,217 loaded Components** belonged to runtime scene GameObjects; **4,936** belonged to non-scene loaded objects such as prefabs.

## Measurement method

A fresh standalone player was built from the current workspace and run with the existing `SariPerformanceProbe`. The probe:

1. loaded `Dev Scene`;
2. waited for `DataHandler.StoreLoaded`;
3. waited until batch-instancer and GPU-instance counts were unchanged for 120 frames;
4. warmed up for another 2 seconds;
5. captured the object snapshot; and
6. sampled render counters for 2 seconds before exiting.

The scene's distributed-benchmark flag was disabled only in the measurement build because the configured coordinator was unavailable. The scene file and temporary probe instrumentation were restored afterward. No gameplay movement or interaction was performed.

The total-loaded count was captured with `Resources.FindObjectsOfTypeAll<UnityEngine.Object>()`. Runtime-scene counts include objects whose `scene.IsValid()` is true, including `DontDestroyOnLoad`. Active counts use `FindObjectsInactive.Exclude`: this excludes inactive GameObjects but still includes disabled Components attached to active GameObjects.

## Post-load object counts

| Counter | Count | Scope |
|---|---:|---|
| All loaded Unity objects | **20,217** | Scene objects, loaded prefab/assets, and engine objects |
| Loaded GameObjects | **3,356** | Scene and non-scene loaded GameObjects |
| Runtime-scene GameObjects | **1,558** | Active and inactive; valid runtime scene |
| Active-hierarchy GameObjects | **1,429** | Active runtime objects |
| Inactive-hierarchy GameObjects | **129** | Inactive runtime objects |
| Loaded Components | **10,217** | Scene and non-scene/prefab Components |
| Runtime-scene Components | **5,281** | Active and inactive runtime GameObjects |
| MonoBehaviours on active GameObjects | **1,593** | Enabled and disabled behaviours |
| Renderers on active GameObjects | **1,192** | Enabled and disabled renderers; all captured renderers were enabled |
| Colliders on active GameObjects | **251** | All Collider subclasses |
| Rigidbodies on active GameObjects | **18** | Physics bodies in the active hierarchy |
| Lights on active GameObjects | **10** | 9 spot, 1 directional |
| Shadow-casting lights | **1** | Directional light only |
| Cameras on active GameObjects | **2** | 1 enabled, 1 disabled |

### Ratios

| Ratio | Value |
|---|---:|
| MonoBehaviours per active GameObject | 1.115 |
| Renderers per active GameObject | 0.834 |
| Colliders per active GameObject | 0.176 |
| Rigidbodies per active GameObject | 0.013 |
| Runtime-scene GameObjects among all loaded GameObjects | 46.42% |
| Runtime-scene Components among all loaded Components | 51.69% |

## Most common loaded Unity object types

These counts include both live scene objects and loaded prefab/asset objects. For example, the **230 loaded Rigidbodies** do not mean 230 active physics bodies: only **25** belonged to runtime-scene objects and only **18** were on active GameObjects.

| Count | Runtime type |
|---:|---|
| 3,356 | `UnityEngine.GameObject` |
| 3,243 | `UnityEngine.Transform` |
| 2,836 | `UnityEngine.TextAsset` |
| 1,749 | `UnityEngine.Material` |
| 1,457 | `UnityEngine.MeshRenderer` |
| 1,441 | `UnityEngine.MeshFilter` |
| 709 | `BakedPriceTag` |
| 709 | `UnityEngine.SpriteRenderer` |
| 698 | `UnityEngine.Mesh` |
| 604 | `UnityEngine.Texture2D` |
| 500 | `UnityEngine.BoxCollider` |
| 306 | `UnityEngine.Sprite` |
| 254 | `UnityEngine.MeshCollider` |
| 230 | `UnityEngine.Rigidbody` |
| 200 | `UnityEngine.ComputeShader` |
| 197 | `BatchInstancer` |
| 197 | `UnityEngine.Rendering.Universal.DecalProjector` |
| 186 | `UnityEngine.LODGroup` |
| 155 | `OutlineFx.OutlineFx` |
| 113 | `UnityEngine.RectTransform` |
| 111 | `OutlineController` |
| 90 | `ItemBBoxInfo` |
| 90 | `ItemBBoxPhysicsProxy` |
| 65 | `ItemSpawner` |
| 65 | `ShelfItemData` |
| 55 | `UnityEngine.Shader` |
| 49 | `TMPro.TextMeshPro` |
| 49 | `UnityEngine.CanvasRenderer` |
| 44 | `SubShelfMarker` |
| 26 | `TMPro.TextMeshProUGUI` |

`Transform` plus `RectTransform` equals the loaded GameObject count: 3,243 + 113 = 3,356.

## Most common runtime-scene Component types

This table includes active and inactive runtime GameObjects.

| Count | Component type |
|---:|---|
| 1,475 | `UnityEngine.Transform` |
| 709 | `BakedPriceTag` |
| 709 | `UnityEngine.SpriteRenderer` |
| 490 | `UnityEngine.MeshFilter` |
| 490 | `UnityEngine.MeshRenderer` |
| 252 | `UnityEngine.BoxCollider` |
| 197 | `BatchInstancer` |
| 152 | `OutlineFx.OutlineFx` |
| 108 | `OutlineController` |
| 90 | `ItemBBoxInfo` |
| 90 | `ItemBBoxPhysicsProxy` |
| 83 | `UnityEngine.RectTransform` |
| 47 | `ItemSpawner` |
| 47 | `ShelfItemData` |
| 44 | `SubShelfMarker` |
| 38 | `UnityEngine.CanvasRenderer` |
| 33 | `TMPro.TextMeshPro` |
| 25 | `UnityEngine.Rigidbody` |
| 20 | `TMPro.TextMeshProUGUI` |
| 18 | `UnityEngine.UI.Image` |
| 12 | `DoorHandle` |
| 10 | `UnityEngine.Light` |
| 10 | `UnityEngine.MeshCollider` |
| 10 | `UnityEngine.Rendering.Universal.UniversalAdditionalLightData` |
| 9 | `ShelfBuilder` |

## Renderer and material-slot statistics

All **1,192** renderers found on active GameObjects were enabled.

| Renderer configuration | Count | Share |
|---|---:|---:|
| `SpriteRenderer`, shadows off, receive shadows off | 709 | 59.48% |
| `MeshRenderer`, shadows on, receive shadows on | 415 | 34.82% |
| `MeshRenderer`, shadows off, receive shadows off | 33 | 2.77% |
| `MeshRenderer`, shadows off, receive shadows on | 31 | 2.60% |
| `SkinnedMeshRenderer`, shadows on, receive shadows on | 4 | 0.34% |
| **Total** | **1,192** | **100%** |

There were **419 shadow-casting renderers** (35.15%) and **450 shadow-receiving renderers** (37.75%).

The renderers referenced **1,268 non-null material slots**:

| Shader | Material slots | Share |
|---|---:|---:|
| `Sprites/Default` | 709 | 55.91% |
| `Universal Render Pipeline/Lit` | 520 | 41.01% |
| `TextMeshPro/Distance Field` | 33 | 2.60% |
| `Universal Render Pipeline/Unlit` | 4 | 0.32% |
| `Universal Render Pipeline/Simple Lit` | 2 | 0.16% |

Material-slot counts are references from active renderers, not unique Material object counts. The loaded-object inventory contained **1,749 Material objects** total.

## Product virtualization and GPU object statistics

| Counter | Count |
|---|---:|
| Distinct product batch instancers / SKUs | **197** |
| GPU product instances | **3,862** |
| Virtual item bounding boxes | **3,862** |
| Active real item bounding boxes | **90** |
| Inactive virtual-only item records | **3,772** |
| Pooled item bounding boxes | **0** |
| Indirect draw commands | **798** |

Only **2.33%** of product records were materialized as real nearby bounding-box objects at capture time: one active real proxy per **42.9** virtual records. The average SKU had **19.60** GPU instances; the median was **15**, with a range of **2–72**. Each batch instancer represented 19.60 GPU instances and 4.05 indirect draw commands on average.

### Highest instance-count SKUs

| GPU instances | SKU |
|---:|---|
| 72 | `AJINOMOTO_SOUP&GO_CREAMY_CORN_3x19.2G` |
| 64 | `BINGO_CORNED_BEEF_150G` |
| 64 | `MY_SAN_FITA_SPREADZ_SPICY_TUNA_25G` |
| 60 | `ARGENTINA_LIVER_SPREAD_85G` |
| 60 | `MAYA_CHAMPORADO_227G` |
| 60 | `SAN_MARINO_CORNED_TUNA_100G` |
| 54 | `GLICO_POCKY_COOKIES_AND_CREAM_40G` |
| 54 | `GLICO_POCKY_DOUBLE_CHOCO_39G` |
| 54 | `RELISH_PIECES&STEMS_MUSHROOMS_115G` |
| 54 | `SAN_MARINO_CHILI_CORNED_TUNA_85G` |

## Cameras and lights

- `Main Camera`: enabled, Game camera, renders to screen, full `0xFFFFFFFF` culling mask.
- `Camera Offset`: disabled Camera Component on an active GameObject; full culling mask.
- `Directional Light`: soft shadows enabled.
- Nine cloned spot lights: shadows disabled, range 11.31.

## Related render statistics

These were captured from the same run at 640×360. They describe submitted work, not counts of loaded Unity objects, and should not be compared directly with the old 1920×1080 audit timings.

| Counter | Stable value |
|---|---:|
| Batches | 1,142 |
| Draw calls | 1,142 |
| SetPass calls | 751 |
| Triangles | 37,952 |
| Vertices | 54,092 |

## Comparison with the July 30 audit snapshot

The audit's V2 snapshot used the same active-hierarchy counter semantics. Current changes are concentrated in nearby item proxies:

| Metric | Audit V2 snapshot | Current run | Delta |
|---|---:|---:|---:|
| Active GameObjects | 1,404 | 1,429 | +25 (+1.8%) |
| Active renderers | 1,167 | 1,192 | +25 (+2.1%) |
| Active colliders | 226 | 251 | +25 (+11.1%) |
| Active rigidbodies | 18 | 18 | 0 |
| Active MonoBehaviours | 1,494 | 1,593 | +99 (+6.6%) |
| Active real item bounding boxes | 65 | 90 | +25 (+38.5%) |
| GPU instances | 3,862 | 3,862 | 0 |
| Batch instancers | 197 | 197 | 0 |
| Indirect draw commands | 798 | 798 | 0 |

The exact +25 alignment between active bounding boxes, GameObjects, renderers, and colliders indicates that the higher current active-object count is the nearby-proxy working set at this capture position, not an increase in total stocked products.

The old sandbox log reported **372,681 loaded objects** after unused-asset cleanup. The current V2 probe found **20,217 loaded Unity objects**. That suggests roughly **18.4× fewer** loaded objects, but this is directional rather than a strict apples-to-apples ratio: the old value came from Unity's internal unload log, while the current value came from `Resources.FindObjectsOfTypeAll<UnityEngine.Object>()`.

## Caveats

- “Loaded Unity objects” includes assets, prefab objects, and engine-owned objects; it is broader than the live scene hierarchy.
- Active Component counts include disabled Components on active GameObjects. The camera details demonstrate this: two Camera Components were counted, but only one was enabled.
- Nearby proxy counts depend on the agent/camera position at capture time.
- No user interaction or agent movement occurred during this run.
- This is an object inventory, not a retained-memory snapshot. Counts do not indicate per-object native/GPU memory size.
- The solo build bypassed distributed-coordinator registration so the scene could remain active long enough to measure; it did not change store content.