using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Folds opaque submeshes with matching render state into one submesh (one indirect draw instead of many).
// Each part's base color, texture weight, metallic and smoothness move into vertex color + UV2, which
// URPLit_Procedural reads when _VertexMaterial is on (see ApplyVertexMaterial in shaderGraphSupport.hlsl).
//
// With a ProductTextureAtlas, parts whose texture is packed in it also merge across textures into one
// submesh per render state that uses one shared material. Their vertex data then also holds:
//   UV0 = UV with tiling/offset and the atlas region applied, UV2.z = layer (-1 = untextured),
//   UV2.w = maxMip + 16 * clampU + 32 * clampV, UV3 = region x, y, w, h (see SampleProductAlbedo).
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
        public int array = -1; // atlas array index; -1 = classic group
        public readonly List<int> submeshes = new();

        public bool IsArray => array >= 0;
    }

    // Part -> atlas region, for parts that sample the arrays.
    private class Layout
    {
        public readonly List<Group> groups = new();
        public readonly Dictionary<int, ProductTextureAtlas.Region> regions = new();
        public bool HasArray => regions.Count > 0;
    }

    public static bool IsArrayCandidate(Material m, MeshTopology topology, out Texture2D texture)
    {
        texture = m != null ? m.GetTexture(BaseMap) as Texture2D : null;
        return texture != null && ProductTextureAtlas.CanPack(texture) && MergeKey(m, topology) != null;
    }

    // Classic merge. `materials` are caller-owned (clones): they get modified. Returns the source mesh/materials
    // untouched when nothing can be merged.
    public static (Mesh mesh, Material[] materials) Merge(Mesh mesh, Material[] materials) =>
        MergeCore(mesh, materials, null, null, m => m, m => m);

    // Merge that also folds packed-texture parts into shared array materials. `sources` are NOT modified;
    // `clone` makes the owned copy for every part that stays on the classic per-material path, and `finish`
    // maps each of those copies once it is final (e.g. to a pooled equal material).
    public static (Mesh mesh, Material[] materials) MergeIntoArrays(
        Mesh mesh, Material[] sources, ProductTextureAtlas atlas, Shader arrayShader,
        Func<Material, Material> clone, Func<Material, Material> finish) =>
        MergeCore(mesh, sources, atlas, arrayShader, clone, finish);

    private static (Mesh mesh, Material[] materials) MergeCore(
        Mesh mesh, Material[] materials, ProductTextureAtlas atlas, Shader arrayShader,
        Func<Material, Material> clone, Func<Material, Material> finish)
    {
        int count = Mathf.Min(mesh.subMeshCount, materials.Length);
        Layout layout = mesh.isReadable && (atlas != null || count >= 2)
            ? BuildGroups(mesh, materials, count, atlas)
            : null;
        if (layout == null || (!layout.HasArray && layout.groups.Count == count))
            return (mesh, Array.ConvertAll(materials, m => finish(clone(m))));

        return (BuildMesh(mesh, materials, layout),
            BuildMaterials(materials, layout.groups, atlas, arrayShader, clone, finish));
    }

    private static Layout BuildGroups(Mesh mesh, Material[] materials, int count, ProductTextureAtlas atlas)
    {
        var layout = new Layout();
        List<Group> groups = layout.groups;
        if (atlas != null) AddArrayGroups(layout, mesh, materials, count, atlas);

        for (int s = 0; s < count; s++)
        {
            if (layout.regions.ContainsKey(s)) continue;
            Material m = materials[s];
            string key = MergeKey(m, mesh.GetTopology(s));
            bool textured = m.GetTexture(BaseMap) != null;

            // Untextured parts ride along in an array group; classic groups hold one texture (same tiling).
            Group g = key == null ? null : (!textured ? groups.Find(x => x.key == key && x.IsArray) : null) ??
                groups.Find(x => x.key == key && !x.IsArray &&
                    (!textured || x.textured == null || SameTexture(x.textured, m)));
            if (g == null)
            {
                g = new Group { key = key };
                groups.Add(g);
            }
            if (textured && !g.IsArray) g.textured = m;
            g.submeshes.Add(s);
        }
        return layout;
    }

    // One group per (render state, array) holding every part whose texture is packed and whose wrap the region supports.
    private static void AddArrayGroups(Layout layout, Mesh mesh, Material[] materials, int count, ProductTextureAtlas atlas)
    {
        var uvs = new List<Vector2>();
        mesh.GetUVs(0, uvs);
        if (uvs.Count == 0) return;

        for (int s = 0; s < count; s++)
        {
            Material m = materials[s];
            if (!IsArrayCandidate(m, mesh.GetTopology(s), out Texture2D tex) ||
                !atlas.TryGetRegion(tex, out ProductTextureAtlas.Region region)) continue;

            var wrap = ProductTextureAtlas.UvWrap(
                uvs, mesh.GetIndices(s), m.GetTextureScale(BaseMap), m.GetTextureOffset(BaseMap), tex);
            if (!region.Supports(wrap)) continue;

            string key = MergeKey(m, mesh.GetTopology(s));
            Group g = layout.groups.Find(x => x.IsArray && x.key == key && x.array == region.array);
            if (g == null)
            {
                g = new Group { key = key, array = region.array };
                layout.groups.Add(g);
            }
            g.submeshes.Add(s);
            layout.regions[s] = region;
        }
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

    private static Mesh BuildMesh(Mesh src, Material[] materials, Layout layout)
    {
        List<Group> groups = layout.groups;
        bool arrays = layout.HasArray;
        var srcPos = new List<Vector3>(); src.GetVertices(srcPos);
        var srcNrm = new List<Vector3>(); src.GetNormals(srcNrm);
        var srcTan = new List<Vector4>(); src.GetTangents(srcTan);
        var srcUv = new List<Vector2>(); src.GetUVs(0, srcUv);

        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var tan = new List<Vector4>();
        var uv = new List<Vector2>(); var colors = new List<Color>(); var matData = new List<Vector4>();
        var regionData = new List<Vector4>();
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
                var data = new Vector4(m.GetFloat(Metallic), m.GetFloat(Smoothness), -1f, 0f);
                var rect = Vector4.zero;
                Vector2 tiling = m.GetTextureScale(BaseMap), offset = m.GetTextureOffset(BaseMap);
                bool inArray = layout.regions.TryGetValue(s, out ProductTextureAtlas.Region region);
                if (inArray)
                {
                    rect = region.rect;
                    int clamp = (region.spansU ? 0 : 16) + (region.spansV ? 0 : 32);
                    data.z = region.layer;
                    data.w = region.maxMip + clamp;
                }

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
                        if (srcUv.Count > 0)
                        {
                            Vector2 t = srcUv[i];
                            if (inArray)
                            {
                                t = new Vector2(t.x * tiling.x + offset.x, t.y * tiling.y + offset.y);
                                t = new Vector2(t.x * rect.z + rect.x, t.y * rect.w + rect.y);
                            }
                            uv.Add(t);
                        }
                        colors.Add(c);
                        matData.Add(data);
                        regionData.Add(rect);
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
        if (arrays)
        {
            mesh.SetUVs(2, matData);
            mesh.SetUVs(3, regionData);
        }
        else
        {
            // Classic layout: UV2 = (metallic, smoothness).
            mesh.SetUVs(2, matData.ConvertAll(d => new Vector2(d.x, d.y)));
        }
        mesh.subMeshCount = groups.Count;
        for (int gi = 0; gi < groups.Count; gi++)
            mesh.SetTriangles(indices[gi], gi, false);
        mesh.bounds = src.bounds;
        return mesh;
    }

    // Single-part groups keep their material; merged groups reuse one member's material in vertex-material mode;
    // array groups use the atlas' shared material for their render state.
    private static Material[] BuildMaterials(
        Material[] materials, List<Group> groups, ProductTextureAtlas atlas, Shader arrayShader,
        Func<Material, Material> clone, Func<Material, Material> finish)
    {
        var result = new Material[groups.Count];
        for (int gi = 0; gi < groups.Count; gi++)
        {
            Group g = groups[gi];
            if (g.IsArray)
            {
                result[gi] = atlas.GetMaterial(g.key, g.array, materials[g.submeshes[0]], arrayShader);
                continue;
            }
            Material m = clone(g.textured ?? materials[g.submeshes[0]]);
            if (g.submeshes.Count > 1)
            {
                m.SetColor(BaseColor, Color.white);
                m.SetFloat(VertexMaterial, 1f);
            }
            result[gi] = finish(m);
        }
        return result;
    }
}
