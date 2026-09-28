using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;

// Moves each product's Barcode plane onto the barcode printed in its label texture (found by Tools/detect_barcodes.py).
public static class BarcodeSnapper
{
    private const string ProductsDirectory = "Assets/Resources/Prefabs/Products";
    private const string DetectScript = "Tools/detect_barcodes.py";
    private const string PythonPrefKey = "BarcodeSnapper.Python";
    private const float MinBarycentric = -0.02f;
    private const float SurfaceOffset = 0.0005f;

    [MenuItem("Tools/Products/Snap Barcodes To Labels")]
    public static void SnapAll()
    {
        string[] prefabPaths = AssetDatabase.FindAssets("t:Prefab", new[] { ProductsDirectory })
            .Select(AssetDatabase.GUIDToAssetPath).ToArray();
        Dictionary<string, Vector2[]> detections = DetectBarcodes(prefabPaths);
        if (detections == null) return;

        var skipped = new List<string>();
        int snapped = 0;
        foreach (string path in prefabPaths)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            string reason = Snap(root, detections);
            if (reason == null)
            {
                PrefabUtility.SaveAsPrefabAsset(root, path);
                snapped++;
            }
            else skipped.Add($"{root.name}: {reason}");
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log($"Snapped {snapped} barcodes. Skipped {skipped.Count}:\n{string.Join("\n", skipped)}");
    }

    // Runs the detector once over every LOD0 texture; returns texture asset path -> barcode UV corners.
    private static Dictionary<string, Vector2[]> DetectBarcodes(string[] prefabPaths)
    {
        string[] textures = prefabPaths
            .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
            .SelectMany(p => LodHierarchy.ResolveLodTransforms(p)[0].GetComponent<MeshRenderer>()?.sharedMaterials ?? new Material[0])
            .Select(LabelTexturePath).Where(t => t != null).Distinct().ToArray();

        string input = Path.GetTempFileName(), output = Path.GetTempFileName();
        File.WriteAllText(input, JsonConvert.SerializeObject(textures.Select(Path.GetFullPath)));

        string python = EditorPrefs.GetString(PythonPrefKey, "python3");
        var psi = new ProcessStartInfo(python, $"\"{Path.GetFullPath(DetectScript)}\" \"{input}\" \"{output}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using (Process process = Process.Start(psi))
        {
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Debug.LogError($"Barcode detection failed ({python}). Needs opencv-python; set EditorPrefs '{PythonPrefKey}' to a python with it.\n{error}");
                return null;
            }
        }

        var raw = JsonConvert.DeserializeObject<Dictionary<string, float[][]>>(File.ReadAllText(output));
        return textures.Where(t => raw.TryGetValue(Path.GetFullPath(t), out float[][] c) && c != null)
            .ToDictionary(t => t, t => raw[Path.GetFullPath(t)].Select(c => new Vector2(c[0], c[1])).ToArray());
    }

    private static string LabelTexturePath(Material m)
    {
        Texture tex = m != null && m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
        return tex != null ? AssetDatabase.GetAssetPath(tex) : null;
    }

    // Returns null on success, otherwise why the product was skipped.
    private static string Snap(GameObject root, Dictionary<string, Vector2[]> detections)
    {
        Transform lod = LodHierarchy.ResolveLodTransforms(root)[0];
        Transform barcode = lod.parent.Find("Barcode");
        if (barcode == null) return "no Barcode child";
        if (!lod.TryGetComponent(out MeshRenderer mr) || !lod.TryGetComponent(out MeshFilter mf) || mf.sharedMesh == null)
            return "no LOD0 mesh";

        Material[] materials = mr.sharedMaterials;
        for (int sub = 0; sub < materials.Length && sub < mf.sharedMesh.subMeshCount; sub++)
        {
            string tex = LabelTexturePath(materials[sub]);
            if (tex == null || !detections.TryGetValue(tex, out Vector2[] corners)) continue;

            var surface = new LabelSurface(lod, mf.sharedMesh, sub, materials[sub], mr.bounds.center);
            if (!surface.TryPlace(barcode, corners)) return $"barcode in {Path.GetFileName(tex)} is off the mesh or fits badly";
            MoveDecalOffBarcode(root, barcode, mr.bounds.center, lod.parent.up);
            return null;
        }
        return "no barcode found in LOD0 textures";
    }

    // Rotates the expiry decal to the far side if it would cover the barcode.
    private static void MoveDecalOffBarcode(GameObject root, Transform barcode, Vector3 center, Vector3 up)
    {
        DecalProjector decal = root.GetComponentInChildren<DecalProjector>(true);
        if (decal == null) return;

        Vector3 toDecal = Vector3.ProjectOnPlane(decal.transform.position - center, up);
        Vector3 toBarcode = Vector3.ProjectOnPlane(barcode.position - center, up);
        float verticalGap = Mathf.Abs(Vector3.Dot(decal.transform.position - barcode.position, up));
        if (Vector3.Angle(toDecal, toBarcode) < 60f && verticalGap < decal.size.y)
            decal.transform.RotateAround(center, up, 180f);
    }

    // Maps label-texture UVs to points on one submesh of a LOD.
    private readonly struct LabelSurface
    {
        private readonly Transform _lod;
        private readonly Vector3[] _vertices;
        private readonly Vector2[] _uvs;
        private readonly int[] _triangles;
        private readonly Vector2 _tiling, _offset;
        private readonly Vector3 _center;

        public LabelSurface(Transform lod, Mesh mesh, int subMesh, Material material, Vector3 center)
        {
            _lod = lod;
            _vertices = mesh.vertices;
            _uvs = mesh.uv;
            _triangles = mesh.GetTriangles(subMesh);
            _tiling = material.GetTextureScale("_BaseMap");
            _offset = material.GetTextureOffset("_BaseMap");
            _center = center;
        }

        // Fits the plane to the barcode's four UV corners, keeping its current facing (in or out).
        public bool TryPlace(Transform barcode, Vector2[] corners)
        {
            var cornerHits = new List<(Vector3 point, Vector3 normal)>[4];
            for (int i = 0; i < 4; i++)
                if ((cornerHits[i] = Sample(corners[i])).Count == 0) return false;
            var midHits = Sample((corners[0] + corners[1] + corners[2] + corners[3]) / 4f);
            if (midHits.Count == 0) return false;

            // Label UVs can be reused elsewhere (e.g. can lids), so keep the hits that sit closest together.
            float bestSpread = float.PositiveInfinity;
            Vector3 mid = default, normal = default;
            var points = new Vector3[4];
            foreach (var m in midHits)
            {
                var nearest = cornerHits.Select(h => h.OrderBy(c => (c.point - m.point).sqrMagnitude).First().point).ToArray();
                float spread = nearest.Sum(c => (c - m.point).magnitude);
                if (spread >= bestSpread) continue;
                bestSpread = spread;
                (mid, normal) = m;
                points = nearest;
            }

            Vector3 edgeA = (points[1] - points[0] + points[2] - points[3]) / 2f;
            Vector3 edgeB = (points[3] - points[0] + points[2] - points[1]) / 2f;
            if (!IsPlausible(edgeA) || !IsPlausible(edgeB) ||
                Mathf.Abs(Vector3.Dot(edgeA.normalized, edgeB.normalized)) > 0.5f) return false;
            bool facesOut = Vector3.Dot(barcode.up, barcode.position - _center) > 0f;
            Vector3 up = facesOut ? normal : -normal;

            barcode.SetPositionAndRotation(
                mid + normal * SurfaceOffset,
                Quaternion.LookRotation(Vector3.ProjectOnPlane(edgeB, normal), up));

            Vector3 meshSize = barcode.GetComponent<MeshFilter>().sharedMesh.bounds.size;
            Vector3 parentScale = barcode.parent.lossyScale;
            barcode.localScale = new Vector3(
                edgeA.magnitude / meshSize.x / parentScale.x,
                barcode.localScale.y,
                edgeB.magnitude / meshSize.z / parentScale.z);
            return true;
        }

        private static bool IsPlausible(Vector3 edge) => edge.magnitude > 0.002f && edge.magnitude < 0.1f;

        // World points (with outward face normals) of every triangle whose UVs contain uv.
        private List<(Vector3 point, Vector3 normal)> Sample(Vector2 textureUv)
        {
            Vector2 uv = (textureUv - _offset) / _tiling;
            var hits = new List<(Vector3, Vector3)>();

            for (int i = 0; i < _triangles.Length; i += 3)
            {
                Vector2 a = _uvs[_triangles[i]], b = _uvs[_triangles[i + 1]], c = _uvs[_triangles[i + 2]];
                float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                if (Mathf.Abs(d) < 1e-12f) continue;

                float w0 = ((b.y - c.y) * (uv.x - c.x) + (c.x - b.x) * (uv.y - c.y)) / d;
                float w1 = ((c.y - a.y) * (uv.x - c.x) + (a.x - c.x) * (uv.y - c.y)) / d;
                float w2 = 1f - w0 - w1;
                if (Mathf.Min(w0, Mathf.Min(w1, w2)) < MinBarycentric) continue;

                Vector3 A = _lod.TransformPoint(_vertices[_triangles[i]]);
                Vector3 B = _lod.TransformPoint(_vertices[_triangles[i + 1]]);
                Vector3 C = _lod.TransformPoint(_vertices[_triangles[i + 2]]);
                Vector3 point = A * w0 + B * w1 + C * w2;
                Vector3 normal = Vector3.Cross(B - A, C - A).normalized;
                hits.Add((point, Vector3.Dot(normal, point - _center) < 0f ? -normal : normal));
            }
            return hits;
        }
    }
}
