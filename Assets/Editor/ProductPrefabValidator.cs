using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// Checks every product prefab against the rules in Docs/NEW_PRODUCT_PREFAB_GUIDE.md.
// Menu: Tools/Products/Validate Product Prefabs. Also callable: ProductPrefabValidator.Run().
public static class ProductPrefabValidator
{
    private const string ProductsFolder = "Assets/Resources/Prefabs/Products";
    private const float PivotTolerance = 0.003f;
    private const float CenterTolerance = 0.008f;

    [MenuItem("Tools/Products/Validate Product Prefabs")]
    private static void RunMenu() => Debug.Log(Run());

    public static string Run()
    {
        var report = new StringBuilder();
        int problems = 0, total = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { ProductsFolder }))
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (root == null) continue;
            total++;
            List<string> issues = Check(root);
            if (issues.Count == 0) continue;
            problems++;
            report.AppendLine($"{root.name}: {string.Join("; ", issues)}");
        }
        return $"Validated {total} product prefabs, {problems} with problems.\n{report}";
    }

    private static List<string> Check(GameObject root)
    {
        var issues = new List<string>();
        if (root.transform.childCount == 0) { issues.Add("no model child"); return issues; }

        // Instantiate(prefab, pos, rot) replaces the root rotation, so the GPU path and the prefab would disagree.
        if (Quaternion.Angle(root.transform.rotation, Quaternion.identity) > 0.1f)
            issues.Add("root rotation must be identity (rotate the model child)");
        Vector3 s = root.transform.localScale;
        if (!Mathf.Approximately(s.x, s.y) || !Mathf.Approximately(s.y, s.z))
            issues.Add("non-uniform root scale (put scale on the model child)");
        if (root.GetComponent<Rigidbody>() == null) issues.Add("no Rigidbody on root");

        Transform[] lods = LodHierarchy.ResolveLodTransforms(root);
        CheckLod(root, lods[0], 0, issues);
        if (lods[1] != lods[0]) CheckLod(root, lods[1], 1, issues);
        CheckLodGroup(root, issues);
        if (lods[0].TryGetComponent(out MeshFilter mf)) CheckMirroredLabels(root, lods[0], mf, issues);
        return issues;
    }

    private static void CheckLod(GameObject root, Transform lod, int index, List<string> issues)
    {
        if (!lod.TryGetComponent(out MeshFilter mf) || mf.sharedMesh == null || !lod.TryGetComponent(out MeshRenderer mr))
        {
            issues.Add($"LOD{index} has no mesh/renderer");
            return;
        }

        if (mf.sharedMesh.subMeshCount != mr.sharedMaterials.Length)
            issues.Add($"LOD{index} submeshes ({mf.sharedMesh.subMeshCount}) != materials ({mr.sharedMaterials.Length})");
        foreach (Material m in mr.sharedMaterials)
        {
            if (m == null) issues.Add($"LOD{index} has a null material");
        }

        if (!mf.sharedMesh.isReadable) issues.Add($"LOD{index} mesh not readable (submesh merge skipped)");

        Bounds b = mf.sharedMesh.bounds;
        Matrix4x4 m2r = root.transform.worldToLocalMatrix * lod.localToWorldMatrix;
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = b.center + new Vector3((i & 1) == 0 ? -b.extents.x : b.extents.x,
                                                    (i & 2) == 0 ? -b.extents.y : b.extents.y,
                                                    (i & 4) == 0 ? -b.extents.z : b.extents.z);
            Vector3 p = m2r.MultiplyPoint3x4(corner);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        Vector3 c = (min + max) * 0.5f;
        if (Mathf.Abs(min.y) > PivotTolerance) issues.Add($"LOD{index} pivot not at bottom (min y {min.y:F3})");
        if (Mathf.Abs(c.x) > CenterTolerance || Mathf.Abs(c.z) > CenterTolerance)
            issues.Add($"LOD{index} pivot not centered (x {c.x:F3}, z {c.z:F3})");
    }

    private static void CheckLodGroup(GameObject root, List<string> issues)
    {
        LODGroup group = root.GetComponentInChildren<LODGroup>(true);
        if (group == null) return;
        foreach (LOD lod in group.GetLODs())
        {
            if (lod.renderers == null || lod.renderers.Length == 0 || System.Array.IndexOf(lod.renderers, null) >= 0)
            {
                issues.Add("LODGroup has an empty/null slot (runtime rebuilds it, but fix the source)");
                break;
            }
        }
    }

    // Label wraps must read correctly from outside; chirality of UV vs surface must be 'unmirrored'.
    private static void CheckMirroredLabels(GameObject root, Transform lod, MeshFilter mf, List<string> issues)
    {
        Mesh mesh = mf.sharedMesh;
        if (!mesh.isReadable || !lod.TryGetComponent(out MeshRenderer mr)) return;
        Matrix4x4 m = root.transform.worldToLocalMatrix * lod.localToWorldMatrix;
        Matrix4x4 n = m.inverse.transpose;
        Vector3[] verts = mesh.vertices, norms = mesh.normals;
        Vector2[] uvs = mesh.uv;
        if (uvs.Length != verts.Length || norms.Length != verts.Length) return;

        for (int s = 0; s < Mathf.Min(mesh.subMeshCount, mr.sharedMaterials.Length); s++)
        {
            Material mat = mr.sharedMaterials[s];
            if (mat == null || !mat.HasProperty("_BaseMap") || mat.GetTexture("_BaseMap") == null) continue;

            float mirrored = 0f, total = 0f;
            int[] idx = mesh.GetTriangles(s);
            for (int t = 0; t < idx.Length; t += 3)
            {
                int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                Vector3 p0 = m.MultiplyPoint3x4(verts[i0]);
                Vector3 e1 = m.MultiplyPoint3x4(verts[i1]) - p0, e2 = m.MultiplyPoint3x4(verts[i2]) - p0;
                Vector3 nrm = (n.MultiplyVector(norms[i0]) + n.MultiplyVector(norms[i1]) + n.MultiplyVector(norms[i2])).normalized;
                if (Mathf.Abs(nrm.y) > 0.6f) continue;

                Vector2 d1 = uvs[i1] - uvs[i0], d2 = uvs[i2] - uvs[i0];
                float det = d1.x * d2.y - d2.x * d1.y;
                if (Mathf.Abs(det) < 1e-12f) continue;
                Vector3 tangent = (e1 * d2.y - e2 * d1.y) / det;
                Vector3 bitangent = (e2 * d1.x - e1 * d2.x) / det;
                float area = Vector3.Cross(e1, e2).magnitude * 0.5f;
                total += area;
                if (Vector3.Dot(Vector3.Cross(tangent, bitangent), nrm) > 0f) mirrored += area;
            }

            // Labels named *_MIRRORED are baked mirrored on purpose (e.g. Heineken logo).
            if (total > 1e-9f && mirrored / total > 0.9f && !mat.GetTexture("_BaseMap").name.Contains("MIRRORED"))
                issues.Add($"label '{mat.GetTexture("_BaseMap").name}' reads mirrored (see MirroredMeshBaker)");
        }
    }
}
