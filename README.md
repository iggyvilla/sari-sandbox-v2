<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://github.com/user-attachments/assets/11696796-f752-4df3-a655-1a8db557ba5c">
    <img width="1270" alt="0" src="https://github.com/user-attachments/assets/11696796-f752-4df3-a655-1a8db557ba5c" />
  </picture>
</p>




<h1 align="center">Sari Sandbox²</h1>

The second-generation [Sari Sandbox](https://sarisandbox.github.io/), a virtual retail environment for embodied agents.

> [!NOTE]  
> Assets and store files will be released once cleaned up.

## Performance improvements

### Stable frame loop

Lower frame-time values are better. “1% low FPS” is calculated from the mean of the slowest 1% of frames.

| Metric | Old Store 2 | V2 Store 2 v2 | Improvement |
|---|---:|---:|---:|
| Average frame time | **11.241 ms** | **6.236 ms** | **44.5% lower** |
| Median / p50 | 11.585 ms | 5.791 ms | 50.0% lower |
| p90 | 13.599 ms | 8.228 ms | 39.5% lower |
| p95 | 14.320 ms | 8.759 ms | 38.8% lower |
| p99 | 17.530 ms | 9.485 ms | 45.9% lower |
| Maximum after transient exclusion | 27.534 ms | 10.425 ms | 62.1% lower |
| Average FPS | **88.96** | **160.35** | **1.80× / +80.2%** |
| 1% low FPS | 52.27 | 102.32 | +95.7% |
| 0.1% low FPS | 39.14 | 97.06 | +148.0% |
| CPU busy per frame | **10.880 ms** | **5.238 ms** | **51.9% lower** |
| GPU busy per frame | 2.458 ms | 4.432 ms | 80.3% higher utilization |

The higher V2 GPU-busy value is expected and healthy here. The old build spends most of its frame on the CPU and leaves the GPU underfed. V2 roughly halves CPU work, submits frames faster, and makes fuller use of the GPU while nearly doubling throughput.

### Frame-budget consistency

| Frame budget | Old Store 2 | V2 Store 2 v2 |
|---|---:|---:|
| ≤ 8.33 ms (120 FPS budget) | 4.7% of frames | **90.9%** |
| ≤ 11.11 ms (90 FPS budget) | 43.7% | **100%** |
| ≤ 16.67 ms (60 FPS budget) | 98.4% | **100%** |

### Startup and footprint

| Metric | Old Store 2 | V2 Store 2 v2 | Change |
|---|---:|---:|---:|
| Warm-cache process start to ready signal | **9.96 s** | **1.96 s** | **80.3% lower / 5.08× faster** |
| Standalone build size | 533.7 MiB | 425.7 MiB | 108.0 MiB / 20.2% smaller |
| Static scene GameObjects in YAML | 186 | 34 | 81.7% fewer |
| Static scene MonoBehaviours in YAML | 207 | 43 | 79.2% fewer |
| Runtime log lines in final run | 33,771 | 154 | 99.5% fewer |
| Runtime log size in final run | 2.10 MB | 9.4 KB | about 224× smaller |

The readiness measurement is warm-cache, not a first-ever cold launch. Old readiness is the `StoreManager done.` message; V2 readiness is emitted after `Store 2 v2` has loaded and its command server starts.
