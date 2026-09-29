# CHANGELOG

<!-- version list -->

## v1.3.0 (2026-09-29)

### Bug Fixes

- **agent**: Reset hand after releasing a door handle without slamming the door
  ([`b5b8a69`](https://github.com/iggyvilla/sari-sandbox-v2/commit/b5b8a693da2814e4655bfdbf34e2c9f1c2bba4c4))

- **gpu**: Show main camera culling results in the scene view
  ([`65326dc`](https://github.com/iggyvilla/sari-sandbox-v2/commit/65326dc682cefba64d5b6e90f2644102a39b72d5))

- **items**: Stabilize stacked physics items
  ([`8b2279a`](https://github.com/iggyvilla/sari-sandbox-v2/commit/8b2279af809c50933b49701d007984c7bd2e5842))

- **items**: Unmirror Royal Tru Orange label and round Coke Light/Zero bases
  ([`1ccde77`](https://github.com/iggyvilla/sari-sandbox-v2/commit/1ccde77811b00dc8c525743304156ae02868340c))

- **render**: Make physics prefabs match GPU instances
  ([`6a03522`](https://github.com/iggyvilla/sari-sandbox-v2/commit/6a03522b2d6f5ce1e12e5ecb2a9d2e5c8e51244a))

- **store-builder**: Fix UI wiring, input and save/load bugs; dedupe builder code
  ([`7684471`](https://github.com/iggyvilla/sari-sandbox-v2/commit/768447139a39d82c96eb664865e9528563255a57))

### Build System

- Add mac and linux build profiles
  ([`85b748b`](https://github.com/iggyvilla/sari-sandbox-v2/commit/85b748b2c1dad5dde05b2119874215a0087aa6f8))

### Chores

- **scene**: Serialize mergeSubmeshes toggle on GPUInstanceTracker
  ([`bf3e93b`](https://github.com/iggyvilla/sari-sandbox-v2/commit/bf3e93bfe0fb5be3a026f46fce58ebee8dc9723c))

- **scene**: Sync dev scene serialized fields and coordinator URL
  ([`3250042`](https://github.com/iggyvilla/sari-sandbox-v2/commit/325004234ff1305f077bb22895f79bc5becd954d))

### Documentation

- Add rendering bugfix log and product prefab guide
  ([`a7903b2`](https://github.com/iggyvilla/sari-sandbox-v2/commit/a7903b27aeaffb3373a7d1fd427d2bb1795128c6))

### Features

- **data**: Report cheapest and lightest items per category
  ([`7068044`](https://github.com/iggyvilla/sari-sandbox-v2/commit/7068044e05f35267e4edeb331fe4fa3ecc9ed7b5))

- **gpu**: Add GameObject vs GPU instance preview scene
  ([`1f22ae0`](https://github.com/iggyvilla/sari-sandbox-v2/commit/1f22ae0f4d5b49060252b27abda90b0637ddeb12))

- **tools**: Add editor tool to snap barcode planes onto label textures
  ([`c69f999`](https://github.com/iggyvilla/sari-sandbox-v2/commit/c69f999df1c1983ea94143b9c75401a827a70aa1))

- **tools**: Add product LOD preview, prefab validator and bottle base smoother
  ([`c908338`](https://github.com/iggyvilla/sari-sandbox-v2/commit/c908338fb0ee9dc0b8f35cf23568b3f74c4824fe))

- **tools**: Bake negative LOD scales into unmirrored mesh copies
  ([`6e5ff0a`](https://github.com/iggyvilla/sari-sandbox-v2/commit/6e5ff0a57a88ab4ce144c84c59b0f2fd111e60fc))

### Performance Improvements

- **render**: Merge compatible product submeshes into single draws
  ([`ee811ba`](https://github.com/iggyvilla/sari-sandbox-v2/commit/ee811ba8b3f3a4c3c3b6ff9611fe0d62f43da1c1))


## v1.2.0 (2026-09-27)

### Features

- **editor**: Add mirrored LOD mesh baker
  ([`000fc99`](https://github.com/iggyvilla/sari-sandbox-v2/commit/000fc99bb624b5f02901b11b9b2570f0f5197b1e))

- **editor**: Add tool to snap barcode planes onto printed barcodes
  ([`5699fd4`](https://github.com/iggyvilla/sari-sandbox-v2/commit/5699fd49c40c75486d339d336f0c56ea2f56a2fb))

- **gpu**: Add batch instancer preview scene
  ([`0ba7af3`](https://github.com/iggyvilla/sari-sandbox-v2/commit/0ba7af3247d3151400a6c38861f6d019005cc84e))


## v1.1.0 (2026-09-27)

### Bug Fixes

- **agent**: Fix IK update, recovery, price tag cents, and input bugs; remove dead code
  ([`b35a06c`](https://github.com/iggyvilla/sari-sandbox-v2/commit/b35a06cb3c7fde5e672360457c9fb390d27d70e1))

- **data**: Remove category products that have no prefab
  ([`5b7c1e6`](https://github.com/iggyvilla/sari-sandbox-v2/commit/5b7c1e6ed06036adea2f336a97dc28a78c6b17f9))

- **items**: Fix bbox pooling, basket delete, and shelf item bugs; speed up item registry
  ([`effca62`](https://github.com/iggyvilla/sari-sandbox-v2/commit/effca6285bbe52c43b245e51fcdba1e0c496b885))

- **sockets**: Fix coordinator lease/reset races and dropped replies; dedupe store builder
  ([`890173b`](https://github.com/iggyvilla/sari-sandbox-v2/commit/890173baa0334619c05ee86d366a0ef3776dacfc))

### Build System

- Add Unity Pipeline package for CLI and MCP editor access
  ([`47f4f22`](https://github.com/iggyvilla/sari-sandbox-v2/commit/47f4f22130db3619aac40e1f88b345e0c47bf67a))

### Features

- **gpu**: Add Hi-Z occlusion culling for instanced products
  ([`ab09dfa`](https://github.com/iggyvilla/sari-sandbox-v2/commit/ab09dfa4f92394e23b5b84746497b14bc6fca35f))

- **gpu**: Add per-camera sphere-based frustum culling for instanced products (WIP, untested)
  ([`5cde705`](https://github.com/iggyvilla/sari-sandbox-v2/commit/5cde705b51fae78b39e2ef018117d0f6398243c0))

- **store**: Add agent-authored store spec with validator and compiler
  ([`6d14a9f`](https://github.com/iggyvilla/sari-sandbox-v2/commit/6d14a9f9200fd81dce39e92df9f59e0c0baeb3cd))

### Performance Improvements

- **gpu**: Cull all instanced products in a single GPU dispatch per viewer
  ([`eeca3ae`](https://github.com/iggyvilla/sari-sandbox-v2/commit/eeca3ae55f9b47497de773d239406c4940d03c3c))

- **gpu**: Default occlusion culling off
  ([`05b5842`](https://github.com/iggyvilla/sari-sandbox-v2/commit/05b5842b57d51989f211a2034e71b665cd49b92d))

- **lidar**: Filter renderers once per capture and batch faces; fix door, baker, and editor bugs
  ([`2831a92`](https://github.com/iggyvilla/sari-sandbox-v2/commit/2831a92a76a1fbb54e15624c20074f91bf863b24))

- **store**: Save shelf items as names and batch store writes
  ([`1d8d3bb`](https://github.com/iggyvilla/sari-sandbox-v2/commit/1d8d3bbb2b4e51247522ca3faf81c2a896ee03fe))


## v1.0.0 (2026-08-24)

- Initial Release
