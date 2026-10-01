using System.Collections.Generic;
using UnityEngine;

// Decorative buildings along the street: one beside each side wall and a row across the road (see StreetLayout).
public partial class RoomStructure
{
    const string BuildingsPath = "russian_buildings/prefabs/buildings_low";
    const string LShapedBuilding = "rus_build_5et_06_low";   // never used: its footprint isn't a rectangle
    const float ExitAlley = 3f;   // gap between the store and a side building when that side wall has an emergency exit
    const string PavementPath = "ModularLowpolyStreetsFree/Prefabs/Roads/Pavement";
    const float PavementTile = 2.5f * RoadScale;   // the prefab is one 2.5 m sidewalk tile
    const float PavementDrop = 0.01f;              // keeps it under any floor a building brings, clear of z-fighting

    private List<BuildingFootprint> _buildingPool;

    // Seeded plan for the current store; rebuilt with the walls.
    StreetPlan Plan => _streetPlan ??= BuildPlan();

    StreetPlan BuildPlan()
    {
        WallFrame frame = StreetFrame;
        float rowFront = RoadFrontRowEdge;
        float reach = WallLayout.SightReach(_slots.FindAll(slot => slot.side == street.wall), frame.length, rowFront) + roadOvershoot;
        return StreetLayout.Plan(street.seed, frame.length, AlleyBeside(-frame.right), AlleyBeside(frame.right), reach,
            RoadSegmentLength, rowFront, BuildingPool);
    }

    // Emergency exits must not open onto a building wall: their side gets an alley. `direction` points from the store to that side.
    float AlleyBeside(Vector3 direction)
    {
        foreach (WallCell exit in EmergencyExits)
            if (Vector3.Dot(exit.side.Normal(), direction) > 0.5f) return ExitAlley;
        return 0f;
    }

    List<BuildingFootprint> BuildingPool => _buildingPool ??= LoadBuildingPool();

    // The road prefab's trees and poles reach past its nominal edge; the far row stands clear of them.
    float RoadFrontRowEdge
    {
        get
        {
            GameObject road = Resources.Load<GameObject>(RoadPath);
            return road != null ? WallLayout.Thickness + RoadWidth * 0.5f + LocalBounds(road).max.x * RoadScale : RoadOuterEdge;
        }
    }

    static List<BuildingFootprint> LoadBuildingPool()
    {
        GameObject[] prefabs = Resources.LoadAll<GameObject>(BuildingsPath);
        System.Array.Sort(prefabs, (a, b) => string.CompareOrdinal(a.name, b.name));   // LoadAll order is unspecified

        var pool = new List<BuildingFootprint>(prefabs.Length);
        foreach (GameObject prefab in prefabs)
        {
            if (prefab.name == LShapedBuilding) continue;
            Bounds bounds = LocalBounds(prefab);
            if (bounds.size.x > 0f) pool.Add(new BuildingFootprint(prefab, bounds, FitsBeside(prefab.name)));
        }

        return pool;
    }

    // Low-rise only: no 9-storey blocks next to the store.
    static bool FitsBeside(string prefabName) => !prefabName.Contains("_9et_");

    void SpawnStreetBuildings()
    {
        WallFrame frame = StreetFrame;
        SpawnDecor("Street Buildings", root =>
        {
            foreach (StreetBuilding building in Plan.buildings)
            {
                Quaternion rotation = building.facesOut ? Quaternion.LookRotation(frame.normal) : frame.Rotation;
                Instantiate(building.prefab, frame.Point(building.along, building.outward), rotation, root);
            }
        });
    }

    // Paves the ground in front of and under the buildings across the road, where only void would show. Tiles sit on the
    // road's own grid at its scale; turned half a turn, the prefab's u = 0 edge meets the road's outer sidewalk edge.
    void SpawnPavement()
    {
        GameObject prefab = Resources.Load<GameObject>(PavementPath);
        if (prefab == null) return;

        WallFrame frame = StreetFrame;
        float gridStart = RoadSpan.x;
        var tiles = new HashSet<Vector2Int>();   // (column along the street, row away from the road)
        foreach (StreetBuilding building in Plan.buildings)
        {
            if (building.facesOut) continue;

            // Across the street a building faces the store: its local +X runs along the street, +Z towards the store.
            Bounds footprint = BuildingPool.Find(b => b.prefab == building.prefab).bounds;
            Vector2Int columns = TileRange(building.along + footprint.min.x - gridStart, building.along + footprint.max.x - gridStart);
            Vector2Int rows = TileRange(building.outward - footprint.max.z - RoadOuterEdge, building.outward - footprint.min.z - RoadOuterEdge);
            for (int column = columns.x; column <= columns.y; column++)
                for (int row = Mathf.Max(0, rows.x); row <= rows.y; row++) tiles.Add(new Vector2Int(column, row));
        }

        Quaternion rotation = Quaternion.LookRotation(-frame.right, Vector3.up);
        SpawnDecor("Street Pavement", root =>
        {
            foreach (Vector2Int tile in tiles)
            {
                // The pivot is the tile's corner on the road side, at the far end of its cell along the street.
                Vector3 position = frame.Point(gridStart + (tile.x + 1) * PavementTile, RoadOuterEdge + tile.y * PavementTile);
                Transform paving = Instantiate(prefab, position + Vector3.down * PavementDrop, rotation, root).transform;
                paving.localScale = Vector3.one * RoadScale;
            }
        });
    }

    // First and last tile index touched by the metres [from, to] on a grid of PavementTile cells.
    static Vector2Int TileRange(float from, float to) => new(
        Mathf.FloorToInt(from / PavementTile + 1e-3f), Mathf.CeilToInt(to / PavementTile - 1e-3f) - 1);

    /// <summary>
    /// Bounds of every mesh in a prefab, in its pivot's frame with its scale applied. Computed from mesh data:
    /// the renderer bounds of an asset that isn't in a scene can't be trusted. Assumes an unrotated root.
    /// </summary>
    static Bounds LocalBounds(GameObject prefab)
    {
        Bounds bounds = default;
        bool any = false;

        foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;

            Matrix4x4 toPivot = Matrix4x4.identity;
            for (Transform t = filter.transform; t != null; t = t.parent)
                toPivot = (t == prefab.transform ? Matrix4x4.Scale(t.localScale) : Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale)) * toPivot;

            Bounds mesh = filter.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var sign = new Vector3((corner & 1) * 2 - 1, (corner >> 1 & 1) * 2 - 1, (corner >> 2) * 2 - 1);
                Vector3 point = toPivot.MultiplyPoint3x4(mesh.center + Vector3.Scale(mesh.extents, sign));
                if (any) bounds.Encapsulate(point);
                else bounds = new Bounds(point, Vector3.zero);
                any = true;
            }
        }

        return bounds;
    }
}
