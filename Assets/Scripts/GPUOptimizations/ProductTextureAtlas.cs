using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

// Packs opaque product albedo textures into a few shared BC1 Texture2DArrays (LayerSize x LayerSize layers) so
// every product part can use one material (see SubmeshMerger). Textures are placed with Graphics.CopyTexture, one
// region per mip, so no CPU access is needed. Parts store their layer and region in vertex data.
//
// Layers are LayerSize (1K/2K/4K): a larger source is copied from its matching mip (no resize, no recompression);
// smaller sources keep their size. Packing is incremental: Add fills free space, then appends a new array, so
// products that show up later never touch arrays that batchers already use.
//
// Memory: the arrays are only ever written by GPU copies. Never call Apply on them: it uploads the (empty) CPU
// copy, which allocates and fills the whole array a second time.
//
// Wrap: a texture whose UVs leave [0,1] needs hardware Repeat to keep working, so it gets a slot that spans
// the layer on that axis (replicated when the texture is smaller than the layer). Other axes are clamped
// to the region in the shader.
public sealed class ProductTextureAtlas : IDisposable
{
    public const int MaxSourceSize = 4096;
    // A new array holds at least this many layers, so a few late products share one array.
    public const int MinArrayLayers = 4;

    private const int BlockSize = 4;
    private const float WrapToleranceTexels = 16f;
    private const int MaxAnisotropy = 16;
    private const GraphicsFormat LayerFormat = GraphicsFormat.RGBA_DXT1_SRGB;

    private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int BaseMapArray = Shader.PropertyToID("_BaseMapArray");
    private static readonly int TextureArray = Shader.PropertyToID("_TextureArray");
    private static readonly int VertexMaterial = Shader.PropertyToID("_VertexMaterial");
    private static readonly int AnisoMax = Shader.PropertyToID("_ProductAnisoMax");
    private static readonly int LayerSizeId = Shader.PropertyToID("_ProductLayerSize");

    [Flags]
    public enum Wrap { None = 0, U = 1, V = 2 }

    // Where one texture lives. rect = x, y, width, height of one copy in layer UV.
    public readonly struct Region
    {
        public readonly int array;
        public readonly int layer;
        public readonly Vector4 rect;
        public readonly int maxMip;    // last mip copied (smaller regions have no block-aligned mips)
        public readonly bool spansU;   // slot covers the layer width: hardware Repeat wraps correctly
        public readonly bool spansV;

        public Region(int array, int layer, Vector4 rect, int maxMip, bool spansU, bool spansV)
        {
            this.array = array;
            this.layer = layer;
            this.rect = rect;
            this.maxMip = maxMip;
            this.spansU = spansU;
            this.spansV = spansV;
        }

        public bool Supports(Wrap need) =>
            ((need & Wrap.U) == 0 || spansU) && ((need & Wrap.V) == 0 || spansV);
    }

    private struct Placement
    {
        public Texture2D texture;
        public int layer;
        public RectInt slot;
    }

    private readonly List<Texture2DArray> _arrays = new();
    private readonly List<int> _arrayFirstLayer = new(); // global index of each array's first layer
    private readonly List<List<RectInt>> _layers = new(); // free rects per layer, spare layers included
    private readonly Dictionary<Texture, Region> _regions = new();
    private readonly Dictionary<string, Material> _materials = new();
    private long _usedTexels;

    public int LayerSize { get; }
    public int LayerMips { get; }
    public IReadOnlyList<Texture2DArray> Arrays => _arrays;
    public int TextureCount => _regions.Count;
    public double BuildMilliseconds { get; private set; }

    // Layers that hold at least one texture.
    public int LayerCount
    {
        get
        {
            int count = 0;
            foreach (List<RectInt> free in _layers)
                if (free.Count != 1 || free[0].width != LayerSize || free[0].height != LayerSize) count++;
            return count;
        }
    }

    public long MemoryBytes
    {
        get
        {
            long bytes = 0;
            foreach (Texture2DArray array in _arrays) bytes += Profiler.GetRuntimeMemorySizeLong(array);
            return bytes;
        }
    }

    // Share of used layer pixels that hold unique texture data (replicated wrap copies count as waste).
    public float Efficiency => LayerCount == 0 ? 0f : _usedTexels / ((float)LayerCount * LayerSize * LayerSize);

    public IReadOnlyDictionary<Texture, Region> Regions => _regions;

    public ProductTextureAtlas(int layerSize)
    {
        if (!IsPowerOfTwo(layerSize) || layerSize < BlockSize || layerSize > MaxSourceSize)
            throw new ArgumentOutOfRangeException(nameof(layerSize), layerSize, "Layer size must be a power of two up to 4096.");
        LayerSize = layerSize;
        LayerMips = Log2(layerSize) + 1;
        ApplySamplingSettings();
    }

    public bool TryGetRegion(Texture texture, out Region region) => _regions.TryGetValue(texture, out region);

    // Opaque, base-mapped parts only: BC1 sRGB with a full mip chain, power-of-two, repeat + bilinear.
    public static bool CanPack(Texture texture) =>
        texture is Texture2D t && t.graphicsFormat == LayerFormat && t.wrapMode == TextureWrapMode.Repeat &&
        t.filterMode == FilterMode.Bilinear && IsPowerOfTwo(t.width) && IsPowerOfTwo(t.height) &&
        t.width <= MaxSourceSize && t.height <= MaxSourceSize && Mathf.Min(t.width, t.height) >= BlockSize &&
        t.mipmapCount == FullMipCount(t.width, t.height);

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    private static int Log2(int v)
    {
        int log = 0;
        while ((v >>= 1) > 0) log++;
        return log;
    }

    private static int FullMipCount(int w, int h) => Log2(Mathf.Max(w, h)) + 1;

    // Source mips skipped so the texture fits the layer (both axes, aspect kept).
    public int MipShift(Texture2D t) => Mathf.Max(0, Log2(Mathf.Max(t.width, t.height)) - Log2(LayerSize));

    // Size of the texture as stored in a layer.
    public Vector2Int PackedSize(Texture2D t)
    {
        int shift = MipShift(t);
        return new Vector2Int(t.width >> shift, t.height >> shift);
    }

    // Very elongated textures can shrink below one block at small layer sizes; those stay classic.
    public bool Fits(Texture2D t)
    {
        Vector2Int size = PackedSize(t);
        return Mathf.Min(size.x, size.y) >= BlockSize;
    }

    // Which axes a part needs hardware wrap on: UVs (after tiling/offset) overshoot [0,1] by more than a few texels.
    public static Wrap UvWrap(IReadOnlyList<Vector2> uvs, int[] indices, Vector2 tiling, Vector2 offset, Texture texture)
    {
        float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
        foreach (int i in indices)
        {
            float u = uvs[i].x * tiling.x + offset.x;
            float v = uvs[i].y * tiling.y + offset.y;
            uMin = Mathf.Min(uMin, u); uMax = Mathf.Max(uMax, u);
            vMin = Mathf.Min(vMin, v); vMax = Mathf.Max(vMax, v);
        }

        Wrap wrap = Wrap.None;
        if (Mathf.Max(-uMin, uMax - 1f) * texture.width > WrapToleranceTexels) wrap |= Wrap.U;
        if (Mathf.Max(-vMin, vMax - 1f) * texture.height > WrapToleranceTexels) wrap |= Wrap.V;
        return wrap;
    }

    // Adds the packable textures of one mesh's opaque parts to `needs`, with the wrap each needs.
    public static void CollectNeeds(Mesh mesh, Material[] materials, Dictionary<Texture2D, Wrap> needs)
    {
        if (mesh == null || !mesh.isReadable) return;
        var uvs = new List<Vector2>();
        mesh.GetUVs(0, uvs);
        int count = Mathf.Min(mesh.subMeshCount, materials.Length);
        for (int s = 0; s < count; s++)
        {
            if (!SubmeshMerger.IsArrayCandidate(materials[s], mesh.GetTopology(s), out Texture2D tex)) continue;
            needs.TryGetValue(tex, out Wrap wrap);
            needs[tex] = wrap | UvWrap(uvs, mesh.GetIndices(s), materials[s].GetTextureScale(BaseMap),
                materials[s].GetTextureOffset(BaseMap), tex);
        }
    }

    // Same for these products' first `lodCount` LODs.
    public static Dictionary<Texture2D, Wrap> CollectNeeds(IEnumerable<GameObject> products, int lodCount)
    {
        var needs = new Dictionary<Texture2D, Wrap>();
        foreach (GameObject product in products)
        {
            if (product == null || product.transform.childCount == 0) continue;
            Transform[] lods = LodHierarchy.ResolveLodTransforms(product);
            for (int lod = 0; lod < lodCount; lod++)
            {
                if (lods[lod].TryGetComponent(out MeshFilter mf) && lods[lod].TryGetComponent(out MeshRenderer mr))
                    CollectNeeds(mf.sharedMesh, mr.sharedMaterials, needs);
            }
        }
        return needs;
    }

    // The shader clamps the mip it samples to the region's last copied mip, so it needs the anisotropy the sampler
    // will really use. Arrays keep the sources' level (1), so only ForceEnable makes them anisotropic (measured: 16x).
    // Re-apply if the quality level changes at runtime.
    public static void ApplySamplingSettings() =>
        Shader.SetGlobalFloat(AnisoMax, QualitySettings.anisotropicFiltering == AnisotropicFiltering.ForceEnable ? MaxAnisotropy : 1f);

    // Packs the textures that aren't in the atlas yet (biggest slots first) and returns how many. Textures already
    // packed keep their regions; space is reused before a new array is appended.
    public int Add(Dictionary<Texture2D, Wrap> needs)
    {
        var timer = Stopwatch.StartNew();
        var items = new List<KeyValuePair<Texture2D, Wrap>>();
        foreach (KeyValuePair<Texture2D, Wrap> need in needs)
            if (!_regions.ContainsKey(need.Key) && Fits(need.Key)) items.Add(need);
        if (items.Count == 0) return 0;

        // Wrap-constrained slots are the biggest; name as tie-break for stable layouts.
        items.Sort((a, b) =>
        {
            int c = SlotArea(b.Key, b.Value).CompareTo(SlotArea(a.Key, a.Value));
            return c != 0 ? c : string.CompareOrdinal(a.Key.name, b.Key.name);
        });

        var placements = new List<Placement>(items.Count);
        foreach (KeyValuePair<Texture2D, Wrap> item in items)
        {
            RectInt slot = Allocate(SlotSize(item.Key, item.Value), out int layer);
            placements.Add(new Placement { texture = item.Key, layer = layer, slot = slot });
            Vector2Int size = PackedSize(item.Key);
            _usedTexels += (long)size.x * size.y;
        }

        AddArrayIfNeeded();
        foreach (Placement p in placements) CopyIntoArray(p);
        BuildMilliseconds += timer.Elapsed.TotalMilliseconds;
        return items.Count;
    }

    // Slots wrap-constrained on an axis span the layer on it.
    private Vector2Int SlotSize(Texture2D tex, Wrap wrap)
    {
        Vector2Int size = PackedSize(tex);
        return new Vector2Int((wrap & Wrap.U) != 0 ? LayerSize : size.x, (wrap & Wrap.V) != 0 ? LayerSize : size.y);
    }

    private long SlotArea(Texture2D tex, Wrap wrap)
    {
        Vector2Int s = SlotSize(tex, wrap);
        return (long)s.x * s.y;
    }

    // Buddy-style best fit: free rects are power-of-two and stay aligned to their own size, so every slot's
    // origin is a multiple of its size (keeps mip regions on the 4x4 block grid). Appends a layer when none fits.
    private RectInt Allocate(Vector2Int size, out int layer)
    {
        layer = -1;
        int freeIndex = -1;
        long best = long.MaxValue;
        for (int l = 0; l < _layers.Count; l++)
        {
            for (int i = 0; i < _layers[l].Count; i++)
            {
                RectInt r = _layers[l][i];
                long area = (long)r.width * r.height;
                if (r.width < size.x || r.height < size.y || area >= best) continue;
                best = area; layer = l; freeIndex = i;
            }
        }

        if (layer < 0)
        {
            _layers.Add(new List<RectInt> { new(0, 0, LayerSize, LayerSize) });
            layer = _layers.Count - 1;
            freeIndex = 0;
        }

        List<RectInt> free = _layers[layer];
        RectInt rect = free[freeIndex];
        free.RemoveAt(freeIndex);
        while (rect.width > size.x || rect.height > size.y)
        {
            // Halve whichever axis is further from the requested size.
            bool splitX = rect.width > size.x && (rect.height == size.y || rect.width / size.x >= rect.height / size.y);
            if (splitX)
            {
                int half = rect.width / 2;
                free.Add(new RectInt(rect.x + half, rect.y, half, rect.height));
                rect.width = half;
            }
            else
            {
                int half = rect.height / 2;
                free.Add(new RectInt(rect.x, rect.y + half, rect.width, half));
                rect.height = half;
            }
        }
        return rect;
    }

    // Layers Allocate appended need an array; its spare layers stay free for later Adds.
    private void AddArrayIfNeeded()
    {
        int capacity = _arrays.Count == 0 ? 0 : _arrayFirstLayer[^1] + _arrays[^1].depth;
        int missing = _layers.Count - capacity;
        if (missing <= 0) return;

        int depth = Mathf.Max(missing, MinArrayLayers);
        for (int i = missing; i < depth; i++)
            _layers.Add(new List<RectInt> { new(0, 0, LayerSize, LayerSize) });

        // Nothing ever uploads to it: DontUploadUponCreate skips the CPU copy, copies below fill it on the GPU.
        var array = new Texture2DArray(LayerSize, LayerSize, depth, LayerFormat,
            TextureCreationFlags.MipChain | TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate,
            LayerMips)
        {
            name = $"ProductAlbedoArray{_arrays.Count}",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 1
        };
        _arrayFirstLayer.Add(capacity);
        _arrays.Add(array);
    }

    private void CopyIntoArray(Placement p)
    {
        Texture2D tex = p.texture;
        int arrayIndex = _arrayFirstLayer.Count - 1;
        while (_arrayFirstLayer[arrayIndex] > p.layer) arrayIndex--;
        int layerInArray = p.layer - _arrayFirstLayer[arrayIndex];
        Texture2DArray array = _arrays[arrayIndex];

        int shift = MipShift(tex);
        Vector2Int size = PackedSize(tex);
        int copiesX = p.slot.width / size.x;
        int copiesY = p.slot.height / size.y;
        // Mips below one 4x4 block can't be copied as a block region; they stay unwritten and are never sampled.
        int maxMip = Log2(Mathf.Min(size.x, size.y) / BlockSize);

        for (int cy = 0; cy < copiesY; cy++)
        {
            for (int cx = 0; cx < copiesX; cx++)
            {
                int x = p.slot.x + cx * size.x;
                int y = p.slot.y + cy * size.y;
                for (int mip = 0; mip <= maxMip; mip++)
                {
                    Graphics.CopyTexture(tex, 0, mip + shift, 0, 0, size.x >> mip, size.y >> mip,
                        array, layerInArray, mip, x >> mip, y >> mip);
                }
            }
        }

        var rect = new Vector4(
            p.slot.x / (float)LayerSize, p.slot.y / (float)LayerSize,
            size.x / (float)LayerSize, size.y / (float)LayerSize);
        _regions[tex] = new Region(
            arrayIndex, layerInArray, rect, maxMip,
            p.slot.width == LayerSize, p.slot.height == LayerSize);
    }

    // The one material every array part of this render state shares. It carries no per-product data:
    // base color, metallic, smoothness, texture layer and region all come from vertex data.
    public Material GetMaterial(string stateKey, int array, Material template, Shader shader)
    {
        Shader.SetGlobalFloat(LayerSizeId, LayerSize); // read by SampleProductAlbedo; the last atlas in use wins
        string key = $"{stateKey}|{array}";
        if (_materials.TryGetValue(key, out Material material) && material != null) return material;

        material = new Material(template)
        {
            name = $"ProductArray{array} ({stateKey})",
            shader = shader,
            enableInstancing = true
        };
        material.SetTexture(BaseMap, null);
        material.SetTextureScale(BaseMap, Vector2.one);
        material.SetTextureOffset(BaseMap, Vector2.zero);
        material.SetColor(BaseColor, Color.white);
        material.SetFloat(VertexMaterial, 1f);
        material.SetFloat(TextureArray, 1f);
        material.SetTexture(BaseMapArray, _arrays[array]);
        _materials[key] = material;
        return material;
    }

    public void Dispose()
    {
        foreach (Material m in _materials.Values) Destroy(m);
        foreach (Texture2DArray a in _arrays) Destroy(a);
        _materials.Clear();
        _arrays.Clear();
        _arrayFirstLayer.Clear();
        _layers.Clear();
        _regions.Clear();
    }

    private static void Destroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Object.Destroy(o);
        else Object.DestroyImmediate(o);
    }
}
