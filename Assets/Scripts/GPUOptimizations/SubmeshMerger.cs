using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Folds opaque submeshes with matching render state into one submesh (one indirect draw instead of many).
// Each part's base color, texture weight, metallic and smoothness move into vertex color + UV2, which
// URPLit_Procedural reads when _VertexMaterial is on (see ApplyVertexMaterial in shaderGraphSupport.hlsl).
public static class SubmeshMerger
{
    private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
    private static readonly int Metallic = Shader.PropertyToID("_Metallic");
    private static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
    private static readonly int Cull = Shader.PropertyToID("_Cull");
    private static readonly int VertexMaterial = Shader.PropertyToID("_VertexMaterial");

    private class Group
    {
        public string key;
        public Material textured;
        public readonly List<int> submeshes = new();
    }

    // Returns the source mesh/materials untouched when nothing can be merged.
    public static (Mesh mesh, Material[] materials) Merge(Mesh mesh, Material[] materials)
    {
        int count = Mathf.Min(mesh.subMeshCount, materials.Length);
        if (count < 2 || !mesh.isReadable) return (mesh, materials);

        List<Group> groups = BuildGroups(mesh, materials, count);
        if (groups.Count == count) return (mesh, materials);

        return (BuildMesh(mesh, materials, groups), BuildMaterials(materials, groups));
    }

    private static List<Group> BuildGroups(Mesh mesh, Material[] materials, int count)
    {
        var groups = new List<Group>();
        for (int s = 0; s < count; s++)
        {
            Material m = materials[s];
            string key = MergeKey(m, mesh.GetTopology(s));
            bool textured = m.GetTexture(BaseMap) != null;

            // A group can hold any number of untextured parts but only one texture (same tiling).
            Group g = key == null ? null : groups.Find(x =>
                x.key == key && (!textured || x.textured == null || SameTexture(x.textured, m)));
            if (g == null)
            {
                g = new Group { key = key };
                groups.Add(g);
            }
            if (textured) g.textured = m;
            g.submeshes.Add(s);
        }
        return groups;
    }

    private static bool SameTexture(Material a, Material b) =>
        a.GetTexture(BaseMap) == b.GetTexture(BaseMap) &&
        a.GetTextureScale(BaseMap) == b.GetTextureScale(BaseMap) &&
        a.GetTextureOffset(BaseMap) == b.GetTextureOffset(BaseMap);

    // Null = never merged (transparent, normal-mapped, or non-triangle parts).
    private static string MergeKey(Material m, MeshTopology topology)
    {
        if (m.renderQueue >= (int)RenderQueue.GeometryLast || m.GetTexture(BumpMap) != null ||
            topology != MeshTopology.Triangles)
            return null;
        return $"{m.renderQueue}|{m.GetFloat(Cull)}|{string.Join(" ", m.shaderKeywords)}";
    }

    private static Mesh BuildMesh(Mesh src, Material[] materials, List<Group> groups)
    {
        var srcPos = new List<Vector3>(); src.GetVertices(srcPos);
        var srcNrm = new List<Vector3>(); src.GetNormals(srcNrm);
        var srcTan = new List<Vector4>(); src.GetTangents(srcTan);
        var srcUv = new List<Vector2>(); src.GetUVs(0, srcUv);

        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var tan = new List<Vector4>();
        var uv = new List<Vector2>(); var colors = new List<Color>(); var matData = new List<Vector2>();
        var indices = new List<int>[groups.Count];
        var remap = new Dictionary<int, int>();
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

        for (int gi = 0; gi < groups.Count; gi++)
        {
            indices[gi] = new List<int>();
            foreach (int s in groups[gi].submeshes)
            {
                Material m = materials[s];
                Color c = m.GetColor(BaseColor);
                if (linear) c = c.linear;
                c.a = m.GetTexture(BaseMap) != null ? 1f : 0f;
                var data = new Vector2(m.GetFloat(Metallic), m.GetFloat(Smoothness));

                // Vertices are duplicated per submesh so shared verts can carry each part's material.
                remap.Clear();
                foreach (int i in src.GetIndices(s))
                {
                    if (!remap.TryGetValue(i, out int ni))
                    {
                        ni = pos.Count;
                        remap[i] = ni;
                        pos.Add(srcPos[i]);
                        if (srcNrm.Count > 0) nrm.Add(srcNrm[i]);
                        if (srcTan.Count > 0) tan.Add(srcTan[i]);
                        if (srcUv.Count > 0) uv.Add(srcUv[i]);
                        colors.Add(c);
                        matData.Add(data);
                    }
                    indices[gi].Add(ni);
                }
            }
        }

        var mesh = new Mesh
        {
            name = src.name + "_merged",
            indexFormat = pos.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
        };
        mesh.SetVertices(pos);
        if (nrm.Count > 0) mesh.SetNormals(nrm);
        if (tan.Count > 0) mesh.SetTangents(tan);
        if (uv.Count > 0) mesh.SetUVs(0, uv);
        mesh.SetColors(colors);
        mesh.SetUVs(2, matData);
        mesh.subMeshCount = groups.Count;
        for (int gi = 0; gi < groups.Count; gi++)
            mesh.SetTriangles(indices[gi], gi, false);
        mesh.bounds = src.bounds;
        return mesh;
    }

    // Single-part groups keep their material; merged groups reuse one member's material in vertex-material mode.
    private static Material[] BuildMaterials(Material[] materials, List<Group> groups)
    {
        var result = new Material[groups.Count];
        for (int gi = 0; gi < groups.Count; gi++)
        {
            Group g = groups[gi];
            Material m = g.textured ?? materials[g.submeshes[0]];
            if (g.submeshes.Count > 1)
            {
                m.SetColor(BaseColor, Color.white);
                m.SetFloat(VertexMaterial, 1f);
            }
            result[gi] = m;
        }
        return result;
    }
}
