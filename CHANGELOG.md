# CHANGELOG

<!-- version list -->

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
