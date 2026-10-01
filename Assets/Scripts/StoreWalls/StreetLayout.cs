using System.Collections.Generic;
using UnityEngine;

/// <summary>A decorative building prefab: front faces local +Z, pivot on the ground.</summary>
public readonly struct BuildingFootprint
{
    public readonly GameObject prefab;
    public readonly Bounds bounds;   // in the pivot's frame, prefab scale included
    public readonly bool beside;     // low-rise and rectangular: may stand next to the store

    public BuildingFootprint(GameObject prefab, Bounds bounds, bool beside)
    {
        this.prefab = prefab;
        this.bounds = bounds;
        this.beside = beside;
    }

    public float Width => bounds.size.x;
}

/// <summary>Where one building stands. along/outward are the pivot's metres along / away from the street wall's <see cref="WallFrame"/>.</summary>
public readonly struct StreetBuilding
{
    public readonly GameObject prefab;
    public readonly bool facesOut;   // beside the store, facing the street like it; else across the street, facing the store
    public readonly float along;
    public readonly float outward;

    public StreetBuilding(GameObject prefab, bool facesOut, float along, float outward)
    {
        this.prefab = prefab;
        this.facesOut = facesOut;
        this.along = along;
        this.outward = outward;
    }
}

public sealed class StreetPlan
{
    public readonly Vector2 roadSpan;   // road start / end along the street wall, whole segments
    public readonly List<StreetBuilding> buildings;

    public StreetPlan(Vector2 roadSpan, List<StreetBuilding> buildings)
    {
        this.roadSpan = roadSpan;
        this.buildings = buildings;
    }
}

/// <summary>
/// Pure, seeded street layout: one building beside each side wall (front level with the street wall), a road
/// spanning them, and a gapless row across it. Same seed and inputs always give the same plan.
/// </summary>
public static class StreetLayout
{
    const int FillAttempts = 64;
    const float Eps = 1e-3f;

    /// <param name="facadeLength">Street wall length, inner face to inner face.</param>
    /// <param name="leftGap">Alley left between the store and the building beside its start (0 = flush).</param>
    /// <param name="rightGap">Same for the building beside its far end.</param>
    /// <param name="reach">Road and row extend at least this far past each end of the street wall (see <see cref="WallLayout.SightReach"/>).</param>
    /// <param name="rowFront">Outward distance where the far row's fronts stand (just past the road).</param>
    public static StreetPlan Plan(int seed, float facadeLength, float leftGap, float rightGap, float reach, float segmentLength,
        float rowFront, IReadOnlyList<BuildingFootprint> pool)
    {
        var rng = new System.Random(seed);
        var buildings = new List<StreetBuilding>();
        float wall = WallLayout.Thickness;
        float start = -wall - leftGap, end = facadeLength + wall + rightGap;   // store outer faces plus alleys

        BuildingFootprint? left = PickBeside(rng, pool), right = PickBeside(rng, pool);
        if (left.HasValue)
        {
            BuildingFootprint b = left.Value;   // mirrored axes: local +X runs against the street frame
            buildings.Add(new StreetBuilding(b.prefab, true, start + b.bounds.min.x, wall - b.bounds.max.z));
            start -= b.Width;
        }

        if (right.HasValue)
        {
            BuildingFootprint b = right.Value;
            buildings.Add(new StreetBuilding(b.prefab, true, end + b.bounds.max.x, wall - b.bounds.max.z));
            end += b.Width;
        }

        float low = Mathf.Min(start, -reach), high = Mathf.Max(end, facadeLength + reach);
        float total = Mathf.Max(1, Mathf.CeilToInt((high - low) / segmentLength)) * segmentLength;
        float middle = (low + high) * 0.5f;
        Vector2 roadSpan = new(middle - total * 0.5f, middle + total * 0.5f);

        PlaceRow(buildings, rng, pool, roadSpan, rowFront);
        return new StreetPlan(roadSpan, buildings);
    }

    static BuildingFootprint? PickBeside(System.Random rng, IReadOnlyList<BuildingFootprint> pool)
    {
        var candidates = new List<BuildingFootprint>();
        foreach (BuildingFootprint building in pool)
            if (building.beside) candidates.Add(building);
        return candidates.Count > 0 ? candidates[rng.Next(candidates.Count)] : null;
    }

    // Contiguous buildings across the street covering the road; the overhang past its ends is shared by both.
    static void PlaceRow(List<StreetBuilding> buildings, System.Random rng, IReadOnlyList<BuildingFootprint> pool,
        Vector2 span, float rowFront)
    {
        List<BuildingFootprint> row = FillRow(rng, pool, span.y - span.x, out float overhang);
        float along = span.x - overhang * 0.5f;

        foreach (BuildingFootprint b in row)
        {
            buildings.Add(new StreetBuilding(b.prefab, false, along - b.bounds.min.x, rowFront + b.bounds.max.z));
            along += b.Width;
        }
    }

    // Random rows that cover `length` with no gaps (the last building may stick out); the one overhanging least of several tries wins.
    static List<BuildingFootprint> FillRow(System.Random rng, IReadOnlyList<BuildingFootprint> pool, float length, out float overhang)
    {
        var best = new List<BuildingFootprint>();
        overhang = 0f;
        if (pool.Count == 0) return best;

        overhang = float.MaxValue;
        for (int attempt = 0; attempt < FillAttempts && overhang > Eps; attempt++)
        {
            var row = new List<BuildingFootprint>();
            float room = length;
            while (room > Eps)
            {
                BuildingFootprint pick = pool[rng.Next(pool.Count)];
                if (row.Count > 0 && row[^1].prefab == pick.prefab && pool.Count > 1) continue;   // no twins side by side
                row.Add(pick);
                room -= pick.Width;
            }

            if (-room >= overhang) continue;
            best = row;
            overhang = -room;
        }

        return best;
    }
}
