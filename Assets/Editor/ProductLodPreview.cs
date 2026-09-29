using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Offscreen check of every product LOD: the prefab as Instantiate places it vs. the GPU-instance transform
// (ItemSpawner.CreateProductDrawTemplate). Writes one PNG strip per product plus metrics.tsv; no play mode needed.
// "GPU" tiles use the tracker's runtime mesh/material conversion (merged submeshes, procedural shader).
// Strip order: prefab LOD0 | GPU LOD0 | prefab LOD1 | GPU LOD1 | prefab LOD2 | prefab LOD3.
public static class ProductLodPreview
{
    private const string ProductsFolder = "Assets/Resources/Prefabs/Products";
    private const int Tile = 256;
    private const string InstancingShaderPath = "Assets/Materials/Shaders/URPLit_Procedural.shadergraph";
    private static readonly Color Background = new(0.5f, 0.55f, 0.6f, 1f);

    [MenuItem("Tools/Products/Render LOD Preview (all)")]
    private static void RenderAllMenu() => Render(null, "Temp/ProductLodPreview", 0f, 0f);

    // itemIds null = every product. yaw orbits the camera around the item (0 = looking down +Z).
    public static string Render(string[] itemIds, string outDir, float yaw, float aisleYaw)
    {
        Directory.CreateDirectory(outDir);
        var ids = new List<string>();
        if (itemIds != null) ids.AddRange(itemIds);
        else
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { ProductsFolder }))
                ids.Add(Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid)));

        Scene scene = EditorSceneManager.NewPreviewScene();
        var camGo = new GameObject("PreviewCam");
        SceneManager.MoveGameObjectToScene(camGo, scene);
        Camera cam = camGo.AddComponent<Camera>();
        cam.scene = scene;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Background;
        cam.fieldOfView = 30f;
        cam.allowMSAA = false;
        cam.allowHDR = false;
        var lightGo = new GameObject("PreviewLight");
        SceneManager.MoveGameObjectToScene(lightGo, scene);
        lightGo.AddComponent<Light>().type = LightType.Directional;

        var rt = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32);
        var tsv = new StringBuilder("item\tcoverage(P0,G0,P1,G1,P2,P3)\tdiffP0G0\tdiffP1G1\tdiffP0P1\n");
        Quaternion aisleRot = Quaternion.Euler(0f, aisleYaw, 0f);
        Shader instancingShader = AssetDatabase.LoadAssetAtPath<Shader>(InstancingShaderPath);

        try
        {
            foreach (string id in ids)
            {
                GameObject prefab = ProductPrefabs.Load(id);
                if (prefab == null) continue;
                RenderItem(scene, cam, lightGo.transform, rt, prefab, id, outDir, yaw, aisleRot, instancingShader, tsv);
            }
        }
        finally
        {
            Object.DestroyImmediate(rt);
            EditorSceneManager.ClosePreviewScene(scene);
        }

        File.WriteAllText(Path.Combine(outDir, "metrics.tsv"), tsv.ToString());
        return $"{ids.Count} items -> {outDir}";
    }

    private static void RenderItem(Scene scene, Camera cam, Transform light, RenderTexture rt, GameObject prefab,
        string id, string outDir, float yaw, Quaternion aisleRot, Shader instancingShader, StringBuilder tsv)
    {
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        inst.transform.SetPositionAndRotation(Vector3.zero, aisleRot);
        foreach (Rigidbody rb in inst.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
        LODGroup group = inst.GetComponentInChildren<LODGroup>();
        Transform[] lods = LodHierarchy.ResolveLodTransforms(prefab);
        ItemSpawner.ProductDrawTemplate template = ItemSpawner.CreateProductDrawTemplate(prefab, aisleRot);
        InstanceData draw = template.At(Vector3.zero);

        Bounds b = default;
        foreach (Renderer r in inst.GetComponentsInChildren<Renderer>())
            if (r.enabled && r.GetComponent<MeshFilter>() != null) { if (b.size == Vector3.zero) b = r.bounds; else b.Encapsulate(r.bounds); }
        float dist = Mathf.Max(b.extents.y, 0.03f) * 1.25f / Mathf.Tan(15f * Mathf.Deg2Rad);
        Quaternion camRot = Quaternion.Euler(0f, yaw, 0f);
        cam.transform.SetPositionAndRotation(b.center + camRot * Vector3.back * dist, camRot);
        cam.nearClipPlane = dist * 0.05f;
        cam.farClipPlane = dist * 4f;
        light.rotation = camRot * Quaternion.Euler(35f, -30f, 0f);

        var tiles = new Color32[6][];
        string[] spec = { "P0", "G0", "P1", "G1", "P2", "P3" };
        for (int i = 0; i < spec.Length; i++)
        {
            int lod = spec[i][1] - '0';
            bool gpu = spec[i][0] == 'G';
            GameObject gpuGo = null;
            if (gpu) gpuGo = SpawnGpuLod(scene, lods[lod], draw[lod], instancingShader);
            SetVisible(inst, !gpu);
            if (!gpu && group != null) group.ForceLOD(lod);
            tiles[i] = Capture(cam, rt);
            if (gpuGo != null) Object.DestroyImmediate(gpuGo);
        }

        Object.DestroyImmediate(inst);

        var strip = new Texture2D(Tile * 6, Tile, TextureFormat.RGBA32, false);
        for (int i = 0; i < tiles.Length; i++) strip.SetPixels32(i * Tile, 0, Tile, Tile, tiles[i]);
        File.WriteAllBytes(Path.Combine(outDir, id + ".png"), strip.EncodeToPNG());
        Object.DestroyImmediate(strip);

        tsv.Append(id).Append('\t');
        for (int i = 0; i < tiles.Length; i++) tsv.Append(Coverage(tiles[i])).Append(i < 5 ? "," : "\t");
        tsv.Append(Diff(tiles[0], tiles[1]).ToString("F3")).Append('\t')
           .Append(Diff(tiles[2], tiles[3]).ToString("F3")).Append('\t')
           .Append(Diff(tiles[0], tiles[2]).ToString("F3")).Append('\n');
    }

    private static GameObject SpawnGpuLod(Scene scene, Transform src, LodTransform t, Shader instancingShader)
    {
        var go = new GameObject("GpuLod");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.SetPositionAndRotation(t.position, new Quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w));
        go.transform.localScale = t.scale;
        if (src.TryGetComponent(out MeshFilter mf) && src.TryGetComponent(out MeshRenderer mr))
        {
            LODDefinition def = GPUInstanceTracker.BuildLod(mf.sharedMesh, mr.sharedMaterials, 0f, instancingShader, true);
            go.AddComponent<MeshFilter>().sharedMesh = def.mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = def.materials;
        }
        return go;
    }

    private static void SetVisible(GameObject inst, bool visible)
    {
        foreach (Renderer r in inst.GetComponentsInChildren<Renderer>(true))
            if (r.GetComponent<MeshFilter>() != null) r.forceRenderingOff = !visible;
    }

    private static Color32[] Capture(Camera cam, RenderTexture rt)
    {
        cam.targetTexture = rt;
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        cam.Render();
        var tex = new Texture2D(Tile, Tile, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, Tile, Tile), 0, 0);
        Color32[] px = tex.GetPixels32();
        Object.DestroyImmediate(tex);
        cam.targetTexture = null;
        RenderTexture.active = prev;
        return px;
    }

    private static bool IsBackground(Color32 c) => Mathf.Abs(c.r - 128) < 4 && Mathf.Abs(c.g - 140) < 4 && Mathf.Abs(c.b - 153) < 4;

    private static int Coverage(Color32[] px)
    {
        int n = 0;
        foreach (Color32 c in px) if (!IsBackground(c)) n++;
        return n;
    }

    // Fraction of the union of both silhouettes where pixels differ noticeably.
    private static float Diff(Color32[] a, Color32[] b)
    {
        int union = 0, differ = 0;
        for (int i = 0; i < a.Length; i++)
        {
            bool fa = !IsBackground(a[i]), fb = !IsBackground(b[i]);
            if (!fa && !fb) continue;
            union++;
            if (fa != fb || Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b) > 60) differ++;
        }
        return union == 0 ? 0f : (float)differ / union;
    }
}
