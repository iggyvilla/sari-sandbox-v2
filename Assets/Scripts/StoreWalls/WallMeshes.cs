using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural boxes for the parts the wall pieces don't cover: they are bare double-sided sheets, so the
/// corners, wall tops and any height above 3 m need real geometry. UVs target the Surface_Wall_A atlas.
/// </summary>
public static class WallMeshes
{
    const float WhiteV = 0.3f;    // atlas rows below 0.25 hold the baked gray stripe
    const float TileMeters = 3f;  // one atlas tile spans 3 m on the pieces
    const float Overlap = 0.002f; // upper parts sink into the row below and stand 2 mm proud, hiding the seam

    /// <summary>Corner column, bottom-centred. Its first 3 m keep the stripe so it matches the pieces beside it.</summary>
    public static Mesh Post(float side, float height)
    {
        var box = new BoxBuilder();
        float lower = Mathf.Min(height, WallLayout.RowHeight);
        box.Add(side, 0f, lower, side, 0f, lower / TileMeters, top: height <= WallLayout.RowHeight);
        if (height > WallLayout.RowHeight)
            box.Add(side + 2f * Overlap, lower - Overlap, height, side + 2f * Overlap, WhiteV, WhiteBandV(height - lower), top: true);
        return box.ToMesh("Wall Post");
    }

    /// <summary>Stripe-free box above the 3 m row, bottom-centred; at height 0 it is just a top cap.</summary>
    public static Mesh Band(float length, float height, float depth)
    {
        var box = new BoxBuilder();
        float overlap = height > 1e-3f ? Overlap : 0f;   // a bare cap has nothing to overlap
        box.Add(length, -overlap, height, depth + 2f * overlap, WhiteV, WhiteBandV(height), top: true);
        return box.ToMesh("Wall Band");
    }

    // Same texel density as the pieces, stretched once the band outgrows the white area.
    static float WhiteBandV(float height) => Mathf.Min(1f, WhiteV + height / TileMeters);

    class BoxBuilder
    {
        readonly List<Vector3> _verts = new();
        readonly List<Vector3> _normals = new();
        readonly List<Vector2> _uvs = new();
        readonly List<int> _tris = new();

        // Box centred on x/z, spanning y0..y1, side faces mapped v0..v1 and a top face in the white area.
        public void Add(float sizeX, float y0, float y1, float sizeZ, float v0, float v1, bool top)
        {
            Vector3 half = new(sizeX * 0.5f, 0f, sizeZ * 0.5f);
            Vector3 lift = Vector3.up * y0;
            Vector3 rise = Vector3.up * (y1 - y0);

            if (y1 - y0 > 1e-3f)
            {
                foreach (Vector3 n in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
                {
                    Vector3 right = Vector3.Cross(n, Vector3.up);
                    float depth = Mathf.Abs(n.x) * half.x + Mathf.Abs(n.z) * half.z;
                    float width = 2f * (Mathf.Abs(right.x) * half.x + Mathf.Abs(right.z) * half.z);
                    AddQuad(n * depth - right * (width * 0.5f) + lift, rise, right * width, n,
                        new Vector2(0f, v0), new Vector2(width / TileMeters, v1));
                }
            }

            if (top)
                AddQuad(new Vector3(-half.x, 0f, -half.z) + Vector3.up * y1, Vector3.forward * sizeZ, Vector3.right * sizeX,
                    Vector3.up, new Vector2(0f, WhiteV), new Vector2(sizeX / TileMeters, WhiteV + sizeZ / TileMeters));
        }

        // Quad from `origin` spanning `up` then `right`; wound clockwise seen from the front (Unity).
        void AddQuad(Vector3 origin, Vector3 up, Vector3 right, Vector3 normal, Vector2 uvMin, Vector2 uvMax)
        {
            int i = _verts.Count;
            _verts.AddRange(new[] { origin, origin + up, origin + up + right, origin + right });
            _uvs.AddRange(new[]
            {
                new Vector2(uvMin.x, uvMin.y), new Vector2(uvMin.x, uvMax.y),
                new Vector2(uvMax.x, uvMax.y), new Vector2(uvMax.x, uvMin.y)
            });
            for (int k = 0; k < 4; k++) _normals.Add(normal);
            _tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(_verts);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uvs);
            mesh.SetTriangles(_tris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }
    }
}
