using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Rounds the coarse petaloid base of bottle bodies (COCACOLA_LIGHT/ZERO: waist + flared feet read as jagged
// shards). The outer wall below BaseHeight follows the smooth profile PEPSI_500ML uses and the feet are flattened to
// one level ring; the inner dome is untouched. Writes mesh copies to Assets/Resources/Meshes/Fixed (rebuilt from the
// FBX mesh each run) and points the prefab's LOD0/LOD1 at them.
public static class BottleBaseSmoother
{
    private const string ProductsDirectory = "Assets/Resources/Prefabs/Products";
    private const string OutputDirectory = "Assets/Resources/Meshes/Fixed";
    private const float BaseHeight = 0.0165f;
    private const float FlatBottomHeight = 0.0025f;
    // Valley points between the feet (about 8 mm up) are dropped to this height so the base reads as round.
    private const float ValleyMinHeight = 0.005f;
    private const float ValleyMaxHeight = 0.011f;
    private const float ValleyHeight = 0.0015f;
    private const float WallMinRadius = 0.02f;

    // (height, radius) of PEPSI_500ML's rounded base, bottom to top.
    private static readonly Vector2[] Profile =
    {
        new(0f, 0.0212f), new(0.0079f, 0.0294f), new(0.0164f, 0.0318f),
    };

    [MenuItem("Tools/Products/Smooth Coke Light + Zero Bases")]
    public static void SmoothCokeLightAndZero()
    {
        Apply("COCACOLA_LIGHT_500ML");
        Apply("COCACOLA_ZERO_SUGAR_500ML");
    }

    public static void Apply(string itemId)
    {
        string path = $"{ProductsDirectory}/{itemId}.prefab";
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        Transform[] lods = LodHierarchy.ResolveLodTransforms(root);
        var done = new HashSet<Mesh>();
        bool changed = false;

        for (int i = 0; i < 2; i++)
        {
            if (!lods[i].TryGetComponent(out MeshFilter mf) || !lods[i].TryGetComponent(out MeshRenderer mr)) continue;
            Mesh source = OriginalMesh(itemId, mf.sharedMesh);
            if (source == null || !done.Add(source)) continue;

            Matrix4x4 toRoot = root.transform.worldToLocalMatrix * lods[i].localToWorldMatrix;
            Mesh fixedMesh = SmoothBase(source, mr.sharedMaterials, toRoot);
            if (fixedMesh == null) continue;
            mf.sharedMesh = fixedMesh;
            changed = true;
        }

        if (changed) PrefabUtility.SaveAsPrefabAsset(root, path);
        PrefabUtility.UnloadPrefabContents(root);
        AssetDatabase.SaveAssets();
    }

    // A mesh already replaced by this tool maps back to its FBX source so reruns don't smooth twice.
    private static Mesh OriginalMesh(string itemId, Mesh current)
    {
        if (current == null) return null;
        if (!AssetDatabase.GetAssetPath(current).StartsWith(OutputDirectory)) return current;

        string sourceName = current.name.Replace("_BaseFix", "");
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath($"Assets/Resources/FBX/{itemId}.fbx"))
            if (asset is Mesh mesh && mesh.name == sourceName) return mesh;
        return null;
    }

    private static Mesh SmoothBase(Mesh source, Material[] materials, Matrix4x4 toRoot)
    {
        var bodyVertices = new HashSet<int>();
        for (int s = 0; s < Mathf.Min(source.subMeshCount, materials.Length); s++)
            if (materials[s] != null && materials[s].name.StartsWith("LIQUID_"))
                foreach (int index in source.GetIndices(s)) bodyVertices.Add(index);
        if (bodyVertices.Count == 0) return null;

        Directory.CreateDirectory(OutputDirectory);
        Mesh mesh = Object.Instantiate(source);
        mesh.name = source.name + "_BaseFix";
        Vector3[] vertices = mesh.vertices;
        Matrix4x4 fromRoot = toRoot.inverse;
        var moved = new List<int>();

        foreach (int i in bodyVertices)
        {
            Vector3 p = toRoot.MultiplyPoint3x4(vertices[i]);
            float radius = new Vector2(p.x, p.z).magnitude;
            if (p.y >= BaseHeight || radius < WallMinRadius) continue;

            if (p.y < FlatBottomHeight) p.y = 0f;
            else if (p.y >= ValleyMinHeight && p.y <= ValleyMaxHeight) p.y = ValleyHeight;
            float scale = ProfileRadius(p.y) / radius;
            p.x *= scale;
            p.z *= scale;
            vertices[i] = fromRoot.MultiplyPoint3x4(p);
            moved.Add(i);
        }

        mesh.vertices = vertices;
        Vector3[] normals = mesh.normals;
        mesh.RecalculateNormals();
        Vector3[] smooth = mesh.normals;
        foreach (int i in moved) normals[i] = smooth[i];
        mesh.normals = normals;
        mesh.RecalculateBounds();

        string assetPath = $"{OutputDirectory}/{mesh.name}.asset";
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(mesh, assetPath);
            return mesh;
        }

        EditorUtility.CopySerialized(mesh, existing);
        Object.DestroyImmediate(mesh);
        return existing;
    }

    private static float ProfileRadius(float height)
    {
        for (int i = 1; i < Profile.Length; i++)
            if (height <= Profile[i].x)
                return Mathf.Lerp(Profile[i - 1].y, Profile[i].y, Mathf.InverseLerp(Profile[i - 1].x, Profile[i].x, height));
        return Profile[Profile.Length - 1].y;
    }
}
