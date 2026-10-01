using System.Collections.Generic;
using UnityEngine;

// Store shell: modular wall pieces, corner posts, wall tops / height band, street road and the builder wall fade.
public partial class RoomStructure
{
    public const float MinWallHeight = WallLayout.RowHeight;
    public const float RoadWidth = 5f * RoadScale;
    public const float RoadSegmentLength = 10f * RoadScale;

    [Header("Street")]
    public StreetSettings street = new();
    [Tooltip("Road and buildings added on top of the sightline reach past each end of the street wall (whole 30 m segments are used).")]
    public float roadOvershoot = 6f;

    /// <summary>Raised after every wall rebuild, once slots and frames (and, in play scenes, the road) are up to date.</summary>
    public event System.Action WallsBuilt;

    const string PiecesPath = "LoafbrrAssets/Interiors_A/prefabs/";
    const string VentPath = "Vent/Vent_Vent";
    const string PlugsPath = "Decal/Decal_Wall_Plugs";
    const string SwitchPath = "Decal/Decal_Wall_Switch";
    const string PillarPath = "wall/Wall_Pillar_A";
    const float PillarProud = 0.002f;
    const string ThresholdName = "Floor_Threshold";
    const float FloorMeshSize = 10f;   // the floor primitive's size at scale 1
    const string RoadPath = "ModularLowpolyStreetsFree/Prefabs/Complex/Road_1_line_10m";
    const float RoadScale = 3f;
    const float PropClearance = 0.2f;

    private readonly List<WallSlot> _slots = new();
    private readonly List<Mesh> _wallMeshes = new();
    private readonly Dictionary<WallSide, SortedDictionary<int, WallPiece>> _cellOverrides = new();
    private readonly Dictionary<WallSide, (Transform root, FadeGroup fade)> _sideWalls = new();
    private readonly float[] _sideAlpha = { 1f, 1f, 1f, 1f };
    private GameObject _wallsRoot;
    private Material _wallMaterial;
    private List<FadeGroup> _fadeGroups;
    private StreetPlan _streetPlan;
    private WallSide _previewSide;
    private int _previewCell = -1;   // emergency exit shown but not committed; -1 = none

    // -- Public API: layout, frames and road (for emergency exits, street buildings, ...) -----------

    /// <summary>Every piece slot of all four walls, each side ordered left to right as seen from outside.</summary>
    public IReadOnlyList<WallSlot> WallSlots => _slots;

    /// <summary>Street wall frame: origin/right/outward normal. The road spans it; buildings line up beside it and across it.</summary>
    public WallFrame StreetFrame => GetWallFrame(street.wall);

    /// <summary>Outward distance from the street wall's inner face to the far edge of the road.</summary>
    public float RoadOuterEdge => WallLayout.Thickness + RoadWidth;

    /// <summary>Road extent (start, end) in metres along <see cref="StreetFrame"/>.right: whole segments covering the store, both side buildings and every sightline through the street wall's openings. Only built in play scenes.</summary>
    public Vector2 RoadSpan => Plan.roadSpan;

    /// <summary>Smallest scale (1 = 10 m) of the street wall's axis that fits the current exit door, its position and a window.</summary>
    public float MinFloorScale => WallLayout.MinStreetLength(street.door, street.position) * 0.1f;

    public WallFrame GetWallFrame(WallSide side)
    {
        Vector3 normal = side.Normal();
        bool runsAlongZ = normal.x != 0f;
        float halfW = transform.localScale.x * 5f;
        float halfD = transform.localScale.z * 5f;
        float length = (runsAlongZ ? halfD : halfW) * 2f;
        Vector3 right = Vector3.Cross(normal, Vector3.up);
        Vector3 origin = transform.position + normal * (runsAlongZ ? halfW : halfD) - right * (length * 0.5f);
        return new WallFrame(origin, right, normal, length);
    }

    public void SetStreet(StreetSettings value)
    {
        street = value.Clone();
        Vector3 scale = transform.localScale;
        SetFloorDimensions(scale.x, scale.z);   // re-clamps to the new door's minimum size and rebuilds
    }

    /// <summary>Replaces one 1 m cell of a non-street wall with `piece` (null restores blank wall) and rebuilds.</summary>
    public void SetCellOverride(WallSide side, int cell, WallPiece? piece)
    {
        SortedDictionary<int, WallPiece> cells = Cells(side);
        if (piece.HasValue) cells[cell] = piece.Value;
        else cells.Remove(cell);
        _previewCell = -1;
        BuildWalls();
    }

    SortedDictionary<int, WallPiece> Cells(WallSide side)
    {
        if (!_cellOverrides.TryGetValue(side, out SortedDictionary<int, WallPiece> cells))
            _cellOverrides[side] = cells = new SortedDictionary<int, WallPiece>();
        return cells;
    }

    // -- Emergency exits: 1 m doors on non-street walls, snapped to the 1 m cell grid ---------------------

    /// <summary>Committed exits, ordered by side then cell.</summary>
    public List<WallCell> EmergencyExits
    {
        get
        {
            var exits = new List<WallCell>();
            foreach (WallSide side in WallSides.All)
            {
                if (!_cellOverrides.TryGetValue(side, out SortedDictionary<int, WallPiece> cells)) continue;
                foreach (KeyValuePair<int, WallPiece> entry in cells)
                    if (entry.Value == WallPiece.EmergencyDoor) exits.Add(new WallCell { side = side, cell = entry.Key });
            }

            return exits;
        }
    }

    public bool HasExit(WallSide side, int cell) =>
        _cellOverrides.TryGetValue(side, out var cells) && cells.TryGetValue(cell, out WallPiece piece) && piece == WallPiece.EmergencyDoor;

    /// <summary>True when a free cell can take an exit: not on the street wall, clear of the corners and of other exits.</summary>
    public bool CanPlaceExit(WallSide side, int cell)
    {
        if (side == street.wall || !WallLayout.IsExitCell(GetWallFrame(side).length, cell)) return false;
        return !HasExit(side, cell - 1) && !HasExit(side, cell) && !HasExit(side, cell + 1);
    }

    /// <summary>Removes the exit on this cell, or places one when the cell can take it.</summary>
    public void ToggleExit(WallSide side, int cell)
    {
        if (HasExit(side, cell)) SetCellOverride(side, cell, null);
        else if (CanPlaceExit(side, cell)) SetCellOverride(side, cell, WallPiece.EmergencyDoor);
    }

    /// <summary>Replaces every exit, dropping any that no longer fit the walls. Rebuild afterwards.</summary>
    public void SetEmergencyExits(IEnumerable<WallCell> exits)
    {
        _cellOverrides.Clear();
        _previewCell = -1;
        foreach (WallCell exit in exits)
            if (CanPlaceExit(exit.side, exit.cell)) Cells(exit.side)[exit.cell] = WallPiece.EmergencyDoor;
    }

    /// <summary>Shows an uncommitted exit on this cell; only the affected walls are rebuilt. Follows the last call.</summary>
    public void PreviewExit(WallSide side, int cell)
    {
        if (_previewCell == cell && _previewSide == side) return;
        WallSide? previous = _previewCell >= 0 ? _previewSide : null;
        _previewSide = side;
        _previewCell = cell;
        if (previous.HasValue && previous.Value != side) RebuildSide(previous.Value);
        RebuildSide(side);
    }

    public void ClearExitPreview()
    {
        if (_previewCell < 0) return;
        _previewCell = -1;
        RebuildSide(_previewSide);
    }

    // Committed cells plus the preview, if it is on this side.
    IReadOnlyDictionary<int, WallPiece> CellsFor(WallSide side)
    {
        _cellOverrides.TryGetValue(side, out SortedDictionary<int, WallPiece> cells);
        if (_previewCell < 0 || _previewSide != side) return cells;

        var merged = cells != null ? new SortedDictionary<int, WallPiece>(cells) : new SortedDictionary<int, WallPiece>();
        merged[_previewCell] = WallPiece.EmergencyDoor;
        return merged;
    }

    /// <summary>
    /// The wall cell under a camera ray: the nearest wall face within the wall height. Pure geometry, so it
    /// ignores fading, colliders and piece swaps. False above the 3 m row and on the fractional end cell.
    /// </summary>
    public bool TryGetWallCell(Ray ray, out WallSide side, out int cell)
    {
        side = default;
        cell = 0;
        float nearest = float.MaxValue, nearestHeight = 0f, nearestLength = 0f;

        foreach (WallSide candidate in WallSides.All)
        {
            WallFrame frame = GetWallFrame(candidate);
            float facing = Vector3.Dot(ray.direction, frame.normal);
            if (Mathf.Abs(facing) < 1e-4f) continue;

            float distance = Vector3.Dot(frame.Point(0f, WallLayout.Thickness * 0.5f) - ray.origin, frame.normal) / facing;
            if (distance < 0f || distance >= nearest) continue;

            Vector3 point = ray.GetPoint(distance);
            float along = Vector3.Dot(point - frame.origin, frame.right);
            float height = point.y - frame.origin.y;
            if (along < 0f || along >= frame.length || height < 0f || height > wallHeight) continue;

            (nearest, nearestHeight, nearestLength) = (distance, height, frame.length);
            (side, cell) = (candidate, Mathf.FloorToInt(along));
        }

        return nearest < float.MaxValue && nearestHeight <= WallLayout.RowHeight && cell < WallLayout.WholeCells(nearestLength);
    }

    /// <summary>Finds the slot a collider/renderer belongs to (e.g. from a raycast hit).</summary>
    public bool TryGetSlot(Transform hit, out WallSlot slot)
    {
        foreach (WallSlot candidate in _slots)
        {
            if (candidate.go == null || !hit.IsChildOf(candidate.go.transform)) continue;
            slot = candidate;
            return true;
        }

        slot = null;
        return false;
    }

    // -- Build ----------------------------------------------------------------------------------------

    void BuildStoreWalls(Vector3 center, float halfW, float halfD)
    {
        _wallsRoot = new GameObject("Store Walls");
        _wallsRoot.transform.SetParent(transform.parent, worldPositionStays: true);
        _fadeGroups = _isStoreBuilder ? new List<FadeGroup>() : null;
        _wallMaterial = LoadPrefab(WallPiece.Blank3.Info().prefab)?.GetComponentInChildren<Renderer>().sharedMaterial;

        foreach (WallSide side in WallSides.All) BuildSide(side);

        SpawnPosts(center, halfW, halfD);
        if (_isStoreBuilder) return;   // the builder shows the bare shell; the street is scenery for the play scenes

        SpawnRoad();
        SpawnStreetBuildings();
        SpawnPavement();
    }

    void BuildSide(WallSide side)
    {
        WallFrame frame = GetWallFrame(side);
        Transform sideRoot = NewChild(_wallsRoot.transform, $"Wall {side}");

        List<WallSlot> slots = SpawnSlots(side, frame, sideRoot);
        SpawnBand(frame, sideRoot);
        _sideWalls[side] = (sideRoot, AddFadeGroup(sideRoot, side, side));
        _slots.AddRange(slots);
    }

    // Swaps just this side's pieces (the band, corners, road and props stay), e.g. for the exit preview.
    void RebuildSide(WallSide side)
    {
        if (!_sideWalls.TryGetValue(side, out var wall)) return;

        int at = _slots.FindIndex(slot => slot.side == side);
        foreach (WallSlot old in _slots)
        {
            if (old.side != side || old.go == null) continue;
            ReleaseThreshold(old.go);
            Destroy(old.go);
        }
        _slots.RemoveAll(slot => slot.side == side);

        List<WallSlot> slots = SpawnSlots(side, GetWallFrame(side), wall.root);
        foreach (WallSlot slot in slots)
            if (slot.go != null) wall.fade?.Add(slot.go.transform);
        _slots.InsertRange(at < 0 ? _slots.Count : at, slots);
    }

    List<WallSlot> SpawnSlots(WallSide side, WallFrame frame, Transform parent)
    {
        List<WallSlot> slots = side == street.wall
            ? WallLayout.Street(side, frame.length, street)
            : WallLayout.Plain(side, frame.length, CellsFor(side));
        foreach (WallSlot slot in slots) SpawnSlot(slot, frame, parent);
        return slots;
    }

    static GameObject LoadPrefab(string path) => Resources.Load<GameObject>(PiecesPath + path);

    void SpawnSlot(WallSlot slot, WallFrame frame, Transform parent)
    {
        WallPieceInfo info = slot.piece.Info();
        GameObject prefab = LoadPrefab(info.prefab);
        if (prefab == null)
        {
            Debug.LogError($"Missing wall piece prefab '{info.prefab}'.", this);
            return;
        }

        slot.position = frame.Point(slot.start + slot.width * 0.5f, WallLayout.Thickness * 0.5f);
        slot.rotation = frame.Rotation;
        slot.go = Instantiate(prefab, slot.position, slot.rotation, parent);
        slot.go.name = prefab.name;

        float stretch = slot.width / info.width;
        if (!Mathf.Approximately(stretch, 1f))
        {
            // A flat filler: a scaled mesh collider is fragile, a box fits it exactly.
            foreach (MeshCollider meshCollider in slot.go.GetComponentsInChildren<MeshCollider>()) Destroy(meshCollider);
            slot.go.transform.localScale = new Vector3(stretch, 1f, 1f);
            BoxCollider box = slot.go.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, WallLayout.RowHeight * 0.5f, 0f);
            box.size = new Vector3(info.width, WallLayout.RowHeight, WallLayout.Thickness);
        }

        if (slot.pillarStart) SpawnPillar(slot, -1f);
        if (slot.pillarEnd) SpawnPillar(slot, 1f);

        if (info.door == null) return;
        SpawnThreshold(slot);
        GameObject door = LoadPrefab(info.door);
        if (door != null) Instantiate(door, slot.position, slot.rotation, slot.go.transform);
    }

    // Covers the hollow end of an open window edge (`side` -1 = start, +1 = end); lives under the slot so it is rebuilt
    // and faded with it. It stands just past the edge on the neighbouring wall, so the window keeps its full width.
    // Squeezed to the wall's depth (2 mm proud) and just under the row top: wall props then stay visible on it and
    // its faces don't z-fight with the sheets or the top cap.
    void SpawnPillar(WallSlot slot, float side)
    {
        GameObject prefab = LoadPrefab(PillarPath);
        if (prefab == null) return;

        Vector3 size = LocalBounds(prefab).size;
        Transform pillar = Instantiate(prefab, slot.go.transform).transform;
        pillar.localPosition = new Vector3(side * (slot.width + size.x) * 0.5f, 0f, 0f);
        pillar.localScale = new Vector3(1f, (WallLayout.RowHeight - PillarProud) / size.y, (WallLayout.Thickness + 2f * PillarProud) / size.z);
    }

    // The floor stops at the wall's inner face: a strip of the floor under each door keeps the void out of the doorway.
    void SpawnThreshold(WallSlot slot)
    {
        Transform parent = slot.go.transform;
        float halfWidth = slot.width * 0.5f, halfDepth = WallLayout.Thickness * 0.5f;
        var corners = new[]   // clockwise from above, local +Z is into the store
        {
            new Vector3(-halfWidth, 0f, -halfDepth), new Vector3(-halfWidth, 0f, halfDepth),
            new Vector3(halfWidth, 0f, halfDepth), new Vector3(halfWidth, 0f, -halfDepth)
        };

        // Same mapping as the floor primitive (u and v run against x and z), so the tiles carry on across the joint.
        var uvs = new Vector2[corners.Length];
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 onFloor = transform.InverseTransformPoint(parent.TransformPoint(corners[i]));
            uvs[i] = new Vector2(0.5f - onFloor.x / FloorMeshSize, 0.5f - onFloor.z / FloorMeshSize);
        }

        Mesh mesh = TrackMesh(new Mesh { name = ThresholdName });
        mesh.SetVertices(corners);
        mesh.SetNormals(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();

        GameObject strip = NewChild(parent, ThresholdName).gameObject;
        strip.AddComponent<MeshFilter>().sharedMesh = mesh;
        strip.AddComponent<MeshRenderer>().sharedMaterial = GetComponent<MeshRenderer>().sharedMaterial;
        BoxCollider box = strip.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, -0.05f, 0f);
        box.size = new Vector3(slot.width, 0.1f, WallLayout.Thickness);
    }

    void ReleaseThreshold(GameObject slotObject)
    {
        MeshFilter strip = slotObject.transform.Find(ThresholdName)?.GetComponent<MeshFilter>();
        if (strip == null) return;
        _wallMeshes.Remove(strip.sharedMesh);
        Destroy(strip.sharedMesh);
    }

    // Stripe-free wall above the 3 m row; at exactly 3 m it is only the top cap that closes the sheets.
    void SpawnBand(WallFrame frame, Transform parent)
    {
        float height = wallHeight - WallLayout.RowHeight;
        Vector3 position = frame.Point(frame.length * 0.5f, WallLayout.Thickness * 0.5f) + Vector3.up * WallLayout.RowHeight;
        Mesh mesh = TrackMesh(WallMeshes.Band(frame.length, height, WallLayout.Thickness));
        GameObject band = NewMeshObject("Wall_Band", mesh, parent, position, frame.Rotation);
        if (height > 0.01f) AddBoxCollider(band, mesh);
    }

    // Columns filling the four outer corners, so wall ends never overflow and no sheet edge shows.
    void SpawnPosts(Vector3 center, float halfW, float halfD)
    {
        float inset = WallLayout.Thickness * 0.5f;
        Mesh mesh = TrackMesh(WallMeshes.Post(WallLayout.Thickness, wallHeight));
        foreach (WallSide xSide in new[] { WallSide.Right, WallSide.Left })
        foreach (WallSide zSide in new[] { WallSide.Front, WallSide.Back })
        {
            Vector3 offset = xSide.Normal() * (halfW + inset) + zSide.Normal() * (halfD + inset);
            GameObject post = NewMeshObject("Wall_Corner", mesh, _wallsRoot.transform, center + offset, Quaternion.identity);
            AddBoxCollider(post, mesh);
            AddFadeGroup(post.transform, xSide, zSide);
        }
    }

    void SpawnRoad()
    {
        GameObject prefab = Resources.Load<GameObject>(RoadPath);
        if (prefab == null) return;

        WallFrame frame = StreetFrame;
        Vector2 span = RoadSpan;
        // Road length runs along the facade, its width away from the store; the pivot is at one end.
        Quaternion rotation = Quaternion.LookRotation(frame.right, Vector3.up);
        SpawnDecor("Road", road =>
        {
            for (float along = span.x; along < span.y - 0.01f; along += RoadSegmentLength)
            {
                Vector3 position = frame.Point(along, WallLayout.Thickness + RoadWidth * 0.5f);
                GameObject segment = Instantiate(prefab, position, rotation, road);
                segment.transform.localScale = Vector3.one * RoadScale;
            }
        });
    }

    // Look-only scenery: built under an inactive root and stripped of colliders before it goes live, so none is ever registered or cooked.
    void SpawnDecor(string rootName, System.Action<Transform> spawn)
    {
        Transform root = NewChild(_wallsRoot.transform, rootName);
        root.gameObject.SetActive(false);
        spawn(root);
        foreach (Collider collider in root.GetComponentsInChildren<Collider>(true)) DestroyImmediate(collider);
        root.gameObject.SetActive(true);
    }

    Mesh TrackMesh(Mesh mesh)
    {
        _wallMeshes.Add(mesh);
        return mesh;
    }

    GameObject NewMeshObject(string objectName, Mesh mesh, Transform parent, Vector3 position, Quaternion rotation)
    {
        var go = new GameObject(objectName);
        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.SetPositionAndRotation(position, rotation);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = _wallMaterial;
        return go;
    }

    static void AddBoxCollider(GameObject go, Mesh mesh)
    {
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.center = mesh.bounds.center;
        box.size = mesh.bounds.size;
    }

    static Transform NewChild(Transform parent, string childName)
    {
        Transform child = new GameObject(childName).transform;
        child.SetParent(parent, worldPositionStays: false);
        return child;
    }

    void DestroyWallObjects()
    {
        if (_fadeGroups != null)
        {
            foreach (FadeGroup group in _fadeGroups) group.DestroyMaterials();
            _fadeGroups = null;
        }

        foreach (Mesh mesh in _wallMeshes) Destroy(mesh);
        _wallMeshes.Clear();
        _sideWalls.Clear();
        _slots.Clear();
        _streetPlan = null;

        if (_wallsRoot != null)
        {
            Destroy(_wallsRoot);
            _wallsRoot = null;
        }
    }

    // True when a prop at this pivot height / offset (along the inside viewer's right) would overlap a window or door.
    // `footprint` is the prop's mesh bounds around its pivot; the default (a point) tests the pivot alone.
    bool IsInFrontOfOpening(Vector3 normal, float y, float offsetAlongWall, Bounds footprint = default)
    {
        foreach (WallSlot slot in _slots)
        {
            WallPieceInfo info = slot.piece.Info();
            if (!info.HasOpening || slot.side.Normal() != normal) continue;
            if (y + footprint.max.y < info.openMinY - PropClearance || y + footprint.min.y > info.openMaxY + PropClearance) continue;

            // The frame's right runs opposite to the inside viewer's right.
            float along = GetWallFrame(slot.side).length * 0.5f - offsetAlongWall;
            if (Mathf.Abs(along - (slot.start + slot.width * 0.5f)) < slot.width * 0.5f + footprint.extents.x + PropClearance) return true;
        }

        return false;
    }

    // -- Builder fade ---------------------------------------------------------------------------------

    // Applied straight away so a rebuilt wall never shows unfaded for a frame.
    FadeGroup AddFadeGroup(Transform root, WallSide a, WallSide b)
    {
        if (_fadeGroups == null) return null;

        var group = new FadeGroup(root, a, b);
        group.Apply(_sideAlpha);
        _fadeGroups.Add(group);
        return group;
    }

    /// <summary>
    /// Own transparent copies of every material under a wall (walls, frames, doors, glass) so a side can
    /// fade without touching shared assets. Fades with the more see-through of its two sides (corners use two).
    /// </summary>
    class FadeGroup
    {
        readonly WallSide _a, _b;
        readonly Dictionary<Material, Material> _clones = new();
        readonly List<(Material material, float baseAlpha)> _materials = new();

        public FadeGroup(Transform root, WallSide a, WallSide b)
        {
            _a = a;
            _b = b;
            Add(root);
        }

        /// <summary>Swaps every material under `root` for this group's fadeable copies (made on first use).</summary>
        public void Add(Transform root)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (renderer.name == ThresholdName) continue;   // floor, not wall: it stays solid
                Material[] shared = renderer.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    if (shared[i] == null) continue;
                    if (!_clones.TryGetValue(shared[i], out Material clone))
                    {
                        clone = new Material(shared[i]);
                        EnsureTransparent(clone);
                        _clones[shared[i]] = clone;
                        _materials.Add((clone, shared[i].color.a));
                    }
                    shared[i] = clone;
                }
                renderer.sharedMaterials = shared;
            }
        }

        // Scales each material's own alpha (glass is already see-through) instead of overwriting it.
        public void Apply(float[] sideAlpha)
        {
            float fade = Mathf.Min(sideAlpha[(int)_a], sideAlpha[(int)_b]);
            foreach ((Material material, float baseAlpha) in _materials)
            {
                Color color = material.color;
                float alpha = baseAlpha * fade;
                if (Mathf.Approximately(color.a, alpha)) continue;
                color.a = alpha;
                material.color = color;
            }
        }

        public void DestroyMaterials()
        {
            foreach ((Material material, _) in _materials) UnityEngine.Object.Destroy(material);
        }
    }
}
