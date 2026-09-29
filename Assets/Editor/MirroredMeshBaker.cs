using System.IO;
using UnityEditor;
using UnityEngine;

// Bakes mirroring into mesh copies: the GPU batch renderer can't flip winding for negative-scale instances.
public static class MirroredMeshBaker
{
    private const string ProductsDirectory = "Assets/Resources/Prefabs/Products";
    private const string OutputDirectory = "Assets/Resources/Meshes/Unmirrored";

    // Products whose label texture reads backwards (earlier manual fixes); reflected left-right to correct them.
    private static readonly string[] MirroredLabelProducts =
    {
        "VITAMILK_CHOCO_SHAKE_300ML", "VITAMILK_BANANA_300ML", "VITAMILK_300ML",
        "PASCUAL_GREEK_STRAWBERRY_250ML", "PASCUAL_GREEK_PLAIN_250ML", "PASCUAL_GREEK_MANGO_250ML",
        "POPPLE_APPLE_FLAVORED_500ML", "MOUNTAIN_DEW_500ML", "MOUNTAIN_DEW_ZERO_500ML",
        // Reflection turned its front 90°; the prefab's model child was rotated +90° Y to restore it.
        "ROYAL_TRU_ORANGE_500ML",
    };

    [MenuItem("Tools/Products/Bake Negative LOD Scales")]
    public static void BakeNegativeScales()
    {
        int baked = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { ProductsDirectory }))
            baked += EditLods(AssetDatabase.GUIDToAssetPath(guid), BakeNegativeScale);
        Debug.Log($"Baked {baked} negative-scale LOD meshes to {OutputDirectory}.");
    }

    [MenuItem("Tools/Products/Fix Mirrored Labels")]
    public static void FixMirroredLabels()
    {
        int baked = 0;
        foreach (string name in MirroredLabelProducts)
            baked += EditLods($"{ProductsDirectory}/{name}.prefab", ReflectHorizontally);
        Debug.Log($"Reflected {baked} LOD meshes to {OutputDirectory}.");
    }

    // Applies fix to LOD0/LOD1 (the LODs the batch renderer draws); returns how many LODs changed.
    private static int EditLods(string prefabPath, System.Func<Transform, MeshFilter, bool> fix)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        int baked = 0;
        Transform[] lods = LodHierarchy.ResolveLodTransforms(root);
        for (int i = 0; i < 2; i++)
        {
            if (i > 0 && lods[i] == lods[i - 1]) continue;
            if (lods[i].TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null && fix(lods[i], mf)) baked++;
        }

        if (baked > 0) PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        PrefabUtility.UnloadPrefabContents(root);
        AssetDatabase.SaveAssets();
        return baked;
    }

    // Moves the negative scale axes into the mesh and makes the scale positive; looks identical.
    private static bool BakeNegativeScale(Transform lod, MeshFilter mf)
    {
        Vector3 s = lod.localScale;
        if (s.x * s.y * s.z >= 0f) return false;

        Vector3 sign = new Vector3(Mathf.Sign(s.x), Mathf.Sign(s.y), Mathf.Sign(s.z));
        SwapMesh(lod, mf, CreateMirroredMesh(mf.sharedMesh, sign, mf.sharedMesh.name));
        lod.localScale = Vector3.Scale(s, sign);
        return true;
    }

    // Reflects the mesh across the local axis closest to the product's left-right direction.
    private static bool ReflectHorizontally(Transform lod, MeshFilter mf)
    {
        if (AssetDatabase.GetAssetPath(mf.sharedMesh).StartsWith(OutputDirectory)) return false;

        Transform root = lod.root;
        Vector3 right = root.right;
        float dx = Mathf.Abs(Vector3.Dot(lod.right, right));
        float dy = Mathf.Abs(Vector3.Dot(lod.up, right));
        float dz = Mathf.Abs(Vector3.Dot(lod.forward, right));
        Vector3 sign = dx >= dy && dx >= dz ? new Vector3(-1, 1, 1)
            : dy >= dz ? new Vector3(1, -1, 1)
            : new Vector3(1, 1, -1);

        SwapMesh(lod, mf, CreateMirroredMesh(mf.sharedMesh, sign, mf.sharedMesh.name + "_LabelFix"));
        if (lod.name.EndsWith("_LOD0")) MirrorAttachments(lod, sign);
        return true;
    }

    // Moves the barcode/decal siblings of a reflected LOD0 onto the mirrored label, keeping them unmirrored.
    public static void MirrorAttachments(Transform lod, Vector3 sign)
    {
        Vector3 normal = lod.TransformDirection(sign.x < 0 ? Vector3.right : sign.y < 0 ? Vector3.up : Vector3.forward).normalized;
        Vector3 axisUp = lod.parent.up;
        foreach (Transform t in lod.parent)
        {
            if (t.name.Contains("_LOD")) continue;

            Vector3 offset = t.position - lod.position;
            t.position -= 2f * Vector3.Dot(offset, normal) * normal;

            // The axis facing out of the bottle stays reflected; flip the more horizontal of the other two so the frame stays proper.
            Vector3 radial = Vector3.ProjectOnPlane(t.position - lod.position, axisUp);
            Vector3[] axes = { t.right, t.up, t.forward };
            int facing = 0;
            for (int i = 1; i < 3; i++)
                if (Mathf.Abs(Vector3.Dot(axes[i], radial)) > Mathf.Abs(Vector3.Dot(axes[facing], radial))) facing = i;
            for (int i = 0; i < 3; i++) axes[i] -= 2f * Vector3.Dot(axes[i], normal) * normal;
            int a = (facing + 1) % 3, b = (facing + 2) % 3;
            int flip = Mathf.Abs(Vector3.Dot(axes[a], axisUp)) < Mathf.Abs(Vector3.Dot(axes[b], axisUp)) ? a : b;
            axes[flip] = -axes[flip];

            t.rotation = Quaternion.LookRotation(axes[2], axes[1]);
        }
    }

    private static void SwapMesh(Transform lod, MeshFilter mf, Mesh mesh)
    {
        foreach (MeshCollider mc in lod.GetComponents<MeshCollider>())
            if (mc.sharedMesh == mf.sharedMesh) mc.sharedMesh = mesh;
        mf.sharedMesh = mesh;
    }

    // Copies source with each axis multiplied by sign (one or three -1s) and triangle winding reversed.
    private static Mesh CreateMirroredMesh(Mesh source, Vector3 sign, string name)
    {
        Directory.CreateDirectory(OutputDirectory);
        string path = $"{OutputDirectory}/{name}.asset";
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) return existing;

        Mesh mesh = Object.Instantiate(source);
        mesh.name = name;
        mesh.vertices = System.Array.ConvertAll(source.vertices, v => Vector3.Scale(v, sign));
        mesh.normals = System.Array.ConvertAll(source.normals, n => Vector3.Scale(n, sign));
        // A reflection flips bitangent handedness, so w flips too.
        mesh.tangents = System.Array.ConvertAll(source.tangents, t =>
        {
            Vector3 xyz = Vector3.Scale(t, sign);
            return new Vector4(xyz.x, xyz.y, xyz.z, -t.w);
        });

        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            int[] tris = mesh.GetTriangles(sub);
            for (int i = 0; i < tris.Length; i += 3)
                (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            mesh.SetTriangles(tris, sub, false);
        }

        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }
}
