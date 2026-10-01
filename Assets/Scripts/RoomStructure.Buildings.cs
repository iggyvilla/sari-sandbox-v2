using System.Collections.Generic;
using UnityEngine;

// Decorative buildings along the street: one beside each side wall and a row across the road (see StreetLayout).
public partial class RoomStructure
{
    const string BuildingsPath = "russian_buildings/prefabs/buildings_low";
    const string LShapedBuilding = "rus_build_5et_06_low";   // never used: its footprint isn't a rectangle
    const float ExitAlley = 3f;   // gap between the store and a side building when that side wall has an emergency exit

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
