using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Proof tools for ProductTextureAtlas (edit mode, no play mode needed):
//  - VerifyCopies: every copied mip of every packed texture equals the source texel for texel (GPU compare).
//  - RenderParity: isolated per-product renders of the classic path vs the array path, pixel-diffed.
public static class ProductTextureAtlasCheck
{
    private const string InstancingShaderPath = "Assets/Materials/Shaders/URPLit_Procedural.shadergraph";
    private const string CompareShaderSource = @"
Shader ""Hidden/AtlasCompare"" {
  SubShader { Pass {
    ZTest Always Cull Off ZWrite Off
    CGPROGRAM
    #pragma vertex vert_img
    #pragma fragment frag
    #pragma target 4.5
    #include ""UnityCG.cginc""
    Texture2D<float4> _Src;
    Texture2DArray<float4> _Arr;
    RWStructuredBuffer<uint> _Mismatch : register(u1);
    int4 _P; // src mip, layer, dst x, dst y (already shifted to the mip)
    int4 _Q; // x = array mip
    float4 frag(v2f_img i) : SV_Target {
      int2 p = int2(i.pos.xy);
      float4 a = _Src.Load(int3(p, _P.x));
      float4 b = _Arr.Load(int4(p + _P.zw, _P.y, _Q.x));
      if (any(a != b)) InterlockedAdd(_Mismatch[0], 1);
      return 0;
    }
    ENDCG
  } }
}";

    // Atlas over every product in Resources, kept for the other checks.
    public static ProductTextureAtlas Atlas { get; private set; }

    public static string BuildAll(int layerSize = 4096)
    {
        Atlas?.Dispose();
        Atlas = new ProductTextureAtlas(layerSize);
        Atlas.Add(ProductTextureAtlas.CollectNeeds(ProductPrefabs.LoadAll(), GPUInstanceTracker.ActiveLodCount));
        return $"layer={layerSize} textures={Atlas.TextureCount} layers={Atlas.LayerCount} arrays={Atlas.Arrays.Count} " +
               $"memory={Atlas.MemoryBytes / 1048576}MB efficiency={Atlas.Efficiency:P2} build={Atlas.BuildMilliseconds:F0}ms";
    }

    // Returns a report; negative control = same compare against the wrong layer (must mismatch).
    public static string VerifyCopies(ProductTextureAtlas atlas)
    {
        Shader shader = ShaderUtil.CreateShaderAsset(CompareShaderSource, false);
        var material = new Material(shader);
        var buffer = new ComputeBuffer(1, sizeof(uint));
        var targets = new Dictionary<Vector2Int, RenderTexture>();
        long texels = 0, mismatches = 0, controlTexels = 0, controlMismatches = 0;
        int mips = 0, checkedTextures = 0;
        var sb = new StringBuilder();

        uint Compare(Texture2D tex, ProductTextureAtlas.Region r, int layer, int mip, int copyX, int copyY)
        {
            int shift = atlas.MipShift(tex);
            Vector2Int packed = atlas.PackedSize(tex);
            int w = packed.x >> mip, h = packed.y >> mip;
            var key = new Vector2Int(w, h);
            if (!targets.TryGetValue(key, out RenderTexture rt))
                targets[key] = rt = new RenderTexture(w, h, 0, RenderTextureFormat.R8) { name = "AtlasCompare" };
            int ox = ((int)(r.rect.x * atlas.LayerSize) + copyX * packed.x) >> mip;
            int oy = ((int)(r.rect.y * atlas.LayerSize) + copyY * packed.y) >> mip;
            material.SetTexture("_Src", tex);
            material.SetTexture("_Arr", atlas.Arrays[r.array]);
            material.SetVector("_P", new Vector4(mip + shift, layer, ox, oy));
            material.SetVector("_Q", new Vector4(mip, 0, 0, 0));
            buffer.SetData(new uint[] { 0 });
            Graphics.SetRandomWriteTarget(1, buffer, false);
            Graphics.Blit(null, rt, material);
            Graphics.ClearRandomWriteTargets();
            var result = new uint[1];
            buffer.GetData(result);
            return result[0];
        }

        try
        {
            foreach (var kv in atlas.Regions)
            {
                var tex = (Texture2D)kv.Key;
                ProductTextureAtlas.Region r = kv.Value;
                checkedTextures++;
                Vector2Int packed = atlas.PackedSize(tex);
                int copiesX = r.spansU ? atlas.LayerSize / packed.x : 1; // wrap slots hold replicated copies
                int copiesY = r.spansV ? atlas.LayerSize / packed.y : 1;
                for (int cy = 0; cy < copiesY; cy++)
                {
                    for (int cx = 0; cx < copiesX; cx++)
                    {
                        for (int mip = 0; mip <= r.maxMip; mip++)
                        {
                            uint bad = Compare(tex, r, r.layer, mip, cx, cy);
                            mismatches += bad;
                            texels += (long)(packed.x >> mip) * (packed.y >> mip);
                            mips++;
                            if (bad > 0) sb.AppendLine($"MISMATCH {tex.name} copy {cx},{cy} mip {mip}: {bad} texels");
                        }
                    }
                }
                // Negative control on mip 0: a different layer must differ (proves the compare is sensitive).
                int other = (r.layer + 1) % atlas.Arrays[r.array].depth;
                if (other != r.layer)
                {
                    controlMismatches += Compare(tex, r, other, 0, 0, 0);
                    controlTexels += (long)packed.x * packed.y;
                }
            }
        }
        finally
        {
            RenderTexture.active = null;
            foreach (RenderTexture rt in targets.Values) Object.DestroyImmediate(rt);
            buffer.Release();
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(shader);
        }

        sb.Insert(0, $"textures={checkedTextures} mips={mips} texels={texels} mismatches={mismatches}; " +
                     $"negative control: {controlMismatches}/{controlTexels} texels differ against the wrong layer\n");
        return sb.ToString();
    }

    // Classic vs array render of each product LOD at several camera distances. fov/1080p density match the game.
    public static string RenderParity(
        string[] itemIds, ProductTextureAtlas atlas, string outDir, int lod, float[] distances, float fov, float yaw = 0f)
    {
        Directory.CreateDirectory(outDir);
        const int Width = 1920, Height = 1080;
        Shader instancingShader = AssetDatabase.LoadAssetAtPath<Shader>(InstancingShaderPath);
        Scene scene = EditorSceneManager.NewPreviewScene();
        var camGo = new GameObject("ParityCam");
        SceneManager.MoveGameObjectToScene(camGo, scene);
        Camera cam = camGo.AddComponent<Camera>();
        cam.scene = scene;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.5f, 0.55f, 0.6f, 1f);
        cam.fieldOfView = fov;
        cam.allowMSAA = false;
        cam.allowHDR = false;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 100f;
        var lightGo = new GameObject("ParityLight");
        SceneManager.MoveGameObjectToScene(lightGo, scene);
        lightGo.AddComponent<Light>().type = LightType.Directional;
        var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
        var tsv = new StringBuilder("item\tlod\tdist\tunionPx\tdiffPx(>24)\tdiffFrac\tmeanAbs\tmaxAbs\tinteriorDiffPx\tarrayMaterials\n");

        try
        {
            foreach (string id in itemIds)
            {
                GameObject prefab = ProductPrefabs.Load(id);
                if (prefab == null) continue;
                Transform src = LodHierarchy.ResolveLodTransforms(prefab)[lod];
                if (!src.TryGetComponent(out MeshFilter mf) || !src.TryGetComponent(out MeshRenderer mr)) continue;
                LodTransform t = ItemSpawner.CreateProductDrawTemplate(prefab, Quaternion.identity).At(Vector3.zero)[lod];

                LODDefinition classic = GPUInstanceTracker.BuildLod(mf.sharedMesh, mr.sharedMaterials, 0f, instancingShader, true);
                LODDefinition arrays = GPUInstanceTracker.BuildLod(mf.sharedMesh, mr.sharedMaterials, 0f, instancingShader, true, atlas);
                int arrayMaterials = 0;
                foreach (Material m in arrays.materials) if (m.GetFloat("_TextureArray") > 0.5f) arrayMaterials++;

                GameObject go = SpawnLod(scene, t, classic);
                Bounds b = go.GetComponent<MeshRenderer>().bounds;
                var camRot = Quaternion.Euler(0f, yaw, 0f);
                lightGo.transform.rotation = camRot * Quaternion.Euler(35f, -30f, 0f);
                foreach (float dist in distances)
                {
                    cam.transform.SetPositionAndRotation(b.center + camRot * Vector3.back * dist, camRot);
                    Color32[] a = Capture(cam, rt, Width, Height);
                    go.GetComponent<MeshFilter>().sharedMesh = arrays.mesh;
                    go.GetComponent<MeshRenderer>().sharedMaterials = arrays.materials;
                    Color32[] c = Capture(cam, rt, Width, Height);
                    go.GetComponent<MeshFilter>().sharedMesh = classic.mesh;
                    go.GetComponent<MeshRenderer>().sharedMaterials = classic.materials;
                    Metrics(a, c, out int union, out int differ, out float mean, out int max, out RectInt box, out int interior);
                    tsv.AppendLine($"{id}\t{lod}\t{dist}\t{union}\t{differ}\t{(union == 0 ? 0 : (float)differ / union):F5}\t{mean:F3}\t{max}\t{interior}\t{arrayMaterials}");
                    WriteSideBySide(Path.Combine(outDir, $"{id}_L{lod}_{dist}m.png"), a, c, Width, Height, box);
                }
                Object.DestroyImmediate(go);
            }
        }
        finally
        {
            Object.DestroyImmediate(rt);
            EditorSceneManager.ClosePreviewScene(scene);
        }

        File.WriteAllText(Path.Combine(outDir, $"parity_L{lod}.tsv"), tsv.ToString());
        return tsv.ToString();

    }

    private static GameObject SpawnLod(Scene scene, LodTransform t, LODDefinition def)
    {
        var go = new GameObject("ParityLod");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.SetPositionAndRotation(t.position, new Quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w));
        go.transform.localScale = t.scale;
        go.AddComponent<MeshFilter>().sharedMesh = def.mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = def.materials;
        return go;
    }

    private static Color32[] Capture(Camera cam, RenderTexture rt, int w, int h)
    {
        cam.targetTexture = rt;
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        cam.Render();
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        Color32[] px = tex.GetPixels32();
        Object.DestroyImmediate(tex);
        cam.targetTexture = null;
        RenderTexture.active = prev;
        return px;
    }

    private static bool IsBackground(Color32 c) => Mathf.Abs(c.r - 128) < 4 && Mathf.Abs(c.g - 140) < 4 && Mathf.Abs(c.b - 153) < 4;

    // Differences over the union of both silhouettes; box = their bounding box.
    private static void Metrics(Color32[] a, Color32[] b, out int union, out int differ, out float mean, out int max, out RectInt box, out int interiorDiffers)
    {
        union = differ = max = 0;
        interiorDiffers = 0;
        long sum = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        const int Width = 1920;
        for (int i = 0; i < a.Length; i++)
        {
            bool fa = !IsBackground(a[i]), fb = !IsBackground(b[i]);
            if (!fa && !fb) continue;
            union++;
            int d = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
            if (fa != fb) d = Mathf.Max(d, 255);
            sum += d;
            if (d > 24) differ++;
            if (d > max) max = d;
            if (fa && fb && d > 24)
            {
                // interior = not touching background in either image
                int xx = i % 1920, yy = i / 1920;
                bool inner = xx > 0 && xx < 1919 && yy > 0 && yy < 1079 &&
                    !IsBackground(a[i - 1]) && !IsBackground(a[i + 1]) && !IsBackground(a[i - 1920]) && !IsBackground(a[i + 1920]) &&
                    !IsBackground(b[i - 1]) && !IsBackground(b[i + 1]) && !IsBackground(b[i - 1920]) && !IsBackground(b[i + 1920]);
                if (inner) interiorDiffers++;
            }
            int x = i % Width, y = i / Width;
            minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
        }
        mean = union == 0 ? 0f : sum / (float)union;
        box = maxX < 0 ? new RectInt(0, 0, 1, 1) : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // classic | array | |diff| x4, cropped to the product and upscaled so far-away renders are visible.
    private static void WriteSideBySide(string path, Color32[] a, Color32[] b, int w, int h, RectInt box)
    {
        int pad = 4;
        int x0 = Mathf.Max(0, box.x - pad), y0 = Mathf.Max(0, box.y - pad);
        int cw = Mathf.Min(w - x0, box.width + 2 * pad), ch = Mathf.Min(h - y0, box.height + 2 * pad);
        int scale = Mathf.Clamp(Mathf.FloorToInt(480f / Mathf.Max(cw, ch)), 1, 16);
        var outTex = new Texture2D(cw * scale * 3, ch * scale, TextureFormat.RGBA32, false);
        var px = new Color32[outTex.width * outTex.height];
        for (int y = 0; y < ch * scale; y++)
        {
            for (int x = 0; x < cw * scale; x++)
            {
                int si = (y0 + y / scale) * w + x0 + x / scale;
                Color32 ca = a[si], cb = b[si];
                var diff = new Color32(
                    (byte)Mathf.Min(255, Mathf.Abs(ca.r - cb.r) * 4), (byte)Mathf.Min(255, Mathf.Abs(ca.g - cb.g) * 4),
                    (byte)Mathf.Min(255, Mathf.Abs(ca.b - cb.b) * 4), 255);
                px[y * outTex.width + x] = ca;
                px[y * outTex.width + cw * scale + x] = cb;
                px[y * outTex.width + 2 * cw * scale + x] = diff;
            }
        }
        outTex.SetPixels32(px);
        File.WriteAllBytes(path, outTex.EncodeToPNG());
        Object.DestroyImmediate(outTex);
    }
}
