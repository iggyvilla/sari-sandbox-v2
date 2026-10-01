using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshRenderer))]
public partial class RoomStructure : MonoBehaviour
{
    [Header("Materials")]
    public Material ceilingMaterial;
    public Material floorMaterialPlay;
    public Material floorMaterialBuilder;

    [Header("Room")]
    public float wallHeight = 3f;
    public float CeilingY => transform.position.y + wallHeight;

    [Header("Ceiling Lights")]
    public GameObject ceilingLightPrefab;
    [Min(0f)] public float ceilingLightSpacingX = 1f;
    [Min(0f)] public float ceilingLightSpacingZ = 1f;
    public float ceilingLightYOffset = 0f;

    [Header("Generated Props")]
    [SerializeField] private GameObject clockPrefab;
    [SerializeField] private GameObject lightPrefab;

    [Header("Wall Prop Settings")]
    [SerializeField] private WallPropSettings rightWallProps;
    [SerializeField] private WallPropSettings leftWallProps;
    [SerializeField] private WallPropSettings frontWallProps;
    [SerializeField] private WallPropSettings backWallProps;

    [Header("Wall Fading")]
    // Dot-product threshold at which a wall begins to fade (0 = instant, 0.3 = starts when wall begins facing camera)
    [Range(0f, 0.9f)] public float fadeStartDot = 0.2f;
    // Minimum alpha a faded wall reaches (never fully invisible so geometry remains hittable)
    [Range(0f, 0.5f)] public float minWallAlpha = 0.05f;

    private bool _isStoreBuilder;
    private Camera _cam;
    private GameObject _ceiling;
    private GameObject _ceilingLightsRoot;
    private GameObject _wallPropsRoot;
    private GameObject _pointLightsRoot;

    private const int amountOfVents = 3;
    private const float ventToCeilDistance = 0.15f;   // gap between the vent's top edge and the ceiling
    private const int amountOfSockets = 3;
    private const float socketFloorMargin = 0.35f;
    private const float lightSwitchMargin = 0.3f;
    private const float clockToCeilDistance = 0.65f;
    private const float wallSurfaceOffset = 0.00f;
    private const float lightPrefabYOffset = -0.3f;

    private enum LightSwitchSide
    {
        None,
        Left,
        Right
    }

    [System.Serializable]
    private struct WallPropSettings
    {
        public bool shouldSpawnVents;
        public bool shouldSpawnSockets;
        public LightSwitchSide lightSwitchSide;
        public bool shouldSpawnClock;
    }

    void Awake()
    {
        _isStoreBuilder = DataHandler.IsStoreBuilderScene;
        _cam = Camera.main;
        if (_isStoreBuilder && street.seed == 0) street.Reroll();   // a new store gets its own street
        GetComponent<MeshRenderer>().sharedMaterial = _isStoreBuilder ? floorMaterialBuilder : floorMaterialPlay;
        BuildWalls();
    }

    void OnDestroy() => DestroyGeneratedObjects();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Resizes the store; `exits` (null keeps the current ones) are checked against the new walls and street.</summary>
    public void SetFloorDimensions(float width, float height, IEnumerable<WallCell> exits = null)
    {
        // The street wall needs room for its door and a window; the other axis only a minimum size.
        bool streetRunsAlongX = street.wall == WallSide.Front || street.wall == WallSide.Back;
        float otherMin = WallLayout.MinWallLength * 0.1f;
        Vector3 scale = transform.localScale;
        scale.x = Mathf.Max(width, streetRunsAlongX ? MinFloorScale : otherMin);
        scale.z = Mathf.Max(height, streetRunsAlongX ? otherMin : MinFloorScale);
        transform.localScale = scale;
        SetEmergencyExits(exits ?? EmergencyExits);
        BuildWalls();
    }

    public void BuildWalls()
    {
        DestroyGeneratedObjects();
        wallHeight = Mathf.Max(wallHeight, MinWallHeight);

        float halfW = transform.localScale.x * 5f;
        float halfD = transform.localScale.z * 5f;
        Vector3 center = transform.position;

        BuildStoreWalls(center, halfW, halfD);
        SpawnWallProps(center, halfW, halfD);

        if (!_isStoreBuilder)
        {
            _ceiling = SpawnPlane("Ceiling",
                center + new Vector3(0, wallHeight, 0),
                Quaternion.Euler(180, 0, 0),
                new Vector3(transform.localScale.x, 1f, transform.localScale.z),
                transform);
            var mr = _ceiling.GetComponent<MeshRenderer>();
            mr.sharedMaterial = ceilingMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;

            SpawnCeilingLights(center, halfW * 2f, halfD * 2f);
        }

        WallsBuilt?.Invoke();
    }

    public void DestroyGeneratedObjects()
    {
        DestroyWallObjects();

        if (_ceiling != null)
        {
            Destroy(_ceiling);
            _ceiling = null;
        }

        if (_ceilingLightsRoot != null)
        {
            Destroy(_ceilingLightsRoot);
            _ceilingLightsRoot = null;
        }

        if (_wallPropsRoot != null)
        {
            Destroy(_wallPropsRoot);
            _wallPropsRoot = null;
        }

        if (_pointLightsRoot != null)
        {
            Destroy(_pointLightsRoot);
            _pointLightsRoot = null;
        }
    }

    // ── Per-frame fading ─────────────────────────────────────────────────────

    void Update()
    {
        if (_fadeGroups == null || _cam == null || !_isStoreBuilder) return;

        Vector3 toCam = _cam.transform.position - transform.position;
        // Ignore vertical component — fading is purely about horizontal viewing angle
        Vector3 toCamFlat = new Vector3(toCam.x, 0f, toCam.z).normalized;

        foreach (WallSide side in WallSides.All)
        {
            // dot = 1: camera is directly outside this wall (wall blocks the view) → fade
            // dot = 0: wall is side-on to camera → opaque
            float dot = Mathf.Clamp01(Vector3.Dot(toCamFlat, side.Normal()));
            float t = Mathf.Clamp01((dot - fadeStartDot) / (1f - fadeStartDot));
            _sideAlpha[(int)side] = Mathf.Lerp(1f, minWallAlpha, t);
        }

        foreach (FadeGroup group in _fadeGroups) group.Apply(_sideAlpha);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void SpawnCeilingLights(Vector3 roomCenter, float ceilingWidth, float ceilingDepth)
    {
        if (ceilingLightPrefab == null) return;

        _ceilingLightsRoot = new GameObject("Ceiling Lights");
        _ceilingLightsRoot.transform.SetParent(transform.parent, worldPositionStays: true);

        float lightY = roomCenter.y + wallHeight + ceilingLightYOffset;
        Quaternion lightRotation = ceilingLightPrefab.transform.rotation;
        GameObject firstLight = Instantiate(
            ceilingLightPrefab,
            new Vector3(roomCenter.x, lightY, roomCenter.z),
            lightRotation,
            _ceilingLightsRoot.transform);

        if (!TryGetCombinedRendererBounds(firstLight, out Bounds lightBounds))
        {
            Destroy(_ceilingLightsRoot);
            _ceilingLightsRoot = null;
            return;
        }

        float gapX = Mathf.Max(0f, ceilingLightSpacingX);
        float gapZ = Mathf.Max(0f, ceilingLightSpacingZ);
        int countX = GetFixtureCount(ceilingWidth, lightBounds.size.x, gapX);
        int countZ = GetFixtureCount(ceilingDepth, lightBounds.size.z, gapZ);
        if (countX == 0 || countZ == 0)
        {
            Destroy(_ceilingLightsRoot);
            _ceilingLightsRoot = null;
            return;
        }

        float occupiedWidth = countX * lightBounds.size.x + (countX - 1) * gapX;
        float occupiedDepth = countZ * lightBounds.size.z + (countZ - 1) * gapZ;
        float firstCenterX = roomCenter.x - occupiedWidth * 0.5f + lightBounds.size.x * 0.5f;
        float firstCenterZ = roomCenter.z - occupiedDepth * 0.5f + lightBounds.size.z * 0.5f;
        Vector3 boundsCenterOffset = lightBounds.center - firstLight.transform.position;

        bool useFirstLight = true;
        GameObject[,] lights = new GameObject[countX, countZ];
        for (int x = 0; x < countX; x++)
        {
            float boundsCenterX = firstCenterX + x * (lightBounds.size.x + gapX);
            for (int z = 0; z < countZ; z++)
            {
                GameObject light = useFirstLight
                    ? firstLight
                    : Instantiate(ceilingLightPrefab, Vector3.zero, lightRotation, _ceilingLightsRoot.transform);
                useFirstLight = false;

                float boundsCenterZ = firstCenterZ + z * (lightBounds.size.z + gapZ);
                light.transform.position = new Vector3(
                    boundsCenterX - boundsCenterOffset.x,
                    lightY,
                    boundsCenterZ - boundsCenterOffset.z);
                lights[x, z] = light;
            }
        }

        SpawnPointLightsForCeilingFixtures(lights, countX, countZ);
    }

    void SpawnWallProps(Vector3 roomCenter, float halfW, float halfD)
    {
        WallPropSettings[] settings =
        {
            rightWallProps,
            leftWallProps,
            frontWallProps,
            backWallProps
        };

        foreach (WallSide side in WallSides.All)
        {
            WallPropSettings wallSettings = settings[(int)side];
            Vector3 normal = side.Normal();
            float wallLength = Mathf.Abs(normal.x) > 0f ? halfD * 2f : halfW * 2f;

            GameObject vent = wallSettings.shouldSpawnVents ? LoadPrefab(VentPath) : null;
            if (vent != null)
            {
                // The vent's pivot is at its bottom: hang its top edge under the ceiling.
                Bounds footprint = LocalBounds(vent);
                float y = roomCenter.y + wallHeight - ventToCeilDistance - footprint.max.y;
                SpawnEquallySpacedWallProps(vent, normal, roomCenter, halfW, halfD, wallLength, y, amountOfVents, footprint);
            }

            GameObject plugs = wallSettings.shouldSpawnSockets ? LoadPrefab(PlugsPath) : null;
            if (plugs != null)
            {
                float y = roomCenter.y + socketFloorMargin;
                SpawnEquallySpacedWallProps(plugs, normal, roomCenter, halfW, halfD, wallLength, y, amountOfSockets);
            }

            GameObject lightSwitch = wallSettings.lightSwitchSide != LightSwitchSide.None ? LoadPrefab(SwitchPath) : null;
            if (lightSwitch != null)
            {
                float margin = Mathf.Min(lightSwitchMargin, wallLength * 0.5f);
                float offset = wallSettings.lightSwitchSide == LightSwitchSide.Left
                    ? -wallLength * 0.5f + margin
                    : wallLength * 0.5f - margin;
                float y = roomCenter.y + wallHeight * 0.3f;
                SpawnWallProp(lightSwitch, normal, roomCenter, halfW, halfD, y, offset);
            }

            if (wallSettings.shouldSpawnClock && clockPrefab != null)
            {
                float y = roomCenter.y + wallHeight - clockToCeilDistance;
                SpawnWallProp(clockPrefab, normal, roomCenter, halfW, halfD, y, 0f);
            }
        }
    }

    void SpawnEquallySpacedWallProps(
        GameObject prefab,
        Vector3 normal,
        Vector3 roomCenter,
        float halfW,
        float halfD,
        float wallLength,
        float y,
        int amount,
        Bounds footprint = default)
    {
        if (amount <= 0) return;

        for (int i = 0; i < amount; i++)
        {
            float t = (i + 1f) / (amount + 1f);
            float offset = Mathf.Lerp(-wallLength * 0.5f, wallLength * 0.5f, t);
            SpawnWallProp(prefab, normal, roomCenter, halfW, halfD, y, offset, footprint);
        }
    }

    void SpawnWallProp(
        GameObject prefab,
        Vector3 normal,
        Vector3 roomCenter,
        float halfW,
        float halfD,
        float y,
        float offsetAlongWall,
        Bounds footprint = default)
    {
        if (IsInFrontOfOpening(normal, y - roomCenter.y, offsetAlongWall, footprint)) return;

        EnsureWallPropsRoot();

        Vector3 viewerRight = Vector3.Cross(Vector3.up, normal).normalized;
        Vector3 wallCenter = GetWallCenter(roomCenter, normal, halfW, halfD, y);
        Vector3 position = wallCenter + viewerRight * offsetAlongWall - normal * wallSurfaceOffset;
        Quaternion rotation = Quaternion.LookRotation(-normal, Vector3.up);

        GameObject prop = Instantiate(prefab, position, rotation, _wallPropsRoot.transform);
        MoveWallPropInFrontOfWall(prop, wallCenter, -normal);
    }

    static void MoveWallPropInFrontOfWall(GameObject prop, Vector3 wallCenter, Vector3 inward)
    {
        if (!TryGetCombinedRendererBounds(prop, out Bounds bounds)) return;

        float targetMinProjection = Vector3.Dot(wallCenter, inward) + wallSurfaceOffset;
        float minProjection = Vector3.Dot(bounds.center, inward) - GetProjectedBoundsExtent(bounds, inward);
        float inwardDistance = targetMinProjection - minProjection;
        if (inwardDistance > 0f) prop.transform.position += inward * inwardDistance;
    }

    static float GetProjectedBoundsExtent(Bounds bounds, Vector3 direction)
    {
        direction = new Vector3(Mathf.Abs(direction.x), Mathf.Abs(direction.y), Mathf.Abs(direction.z));
        return Vector3.Dot(bounds.extents, direction);
    }

    void EnsureWallPropsRoot()
    {
        if (_wallPropsRoot != null) return;

        _wallPropsRoot = new GameObject("Wall Props");
        _wallPropsRoot.transform.SetParent(transform.parent, worldPositionStays: true);
    }

    static Vector3 GetWallCenter(Vector3 roomCenter, Vector3 normal, float halfW, float halfD, float y)
    {
        return new Vector3(
            roomCenter.x + normal.x * halfW,
            y,
            roomCenter.z + normal.z * halfD);
    }

    void SpawnPointLightsForCeilingFixtures(GameObject[,] fixtures, int countX, int countZ)
    {
        if (lightPrefab == null || countX == 0 || countZ == 0) return;

        List<Vector2Int> selected = GetCeilingLightPattern(countX, countZ);
        if (selected.Count == 0) return;

        _pointLightsRoot = new GameObject("Ceiling Point Lights");
        _pointLightsRoot.transform.SetParent(transform.parent, worldPositionStays: true);

        Quaternion rotation = lightPrefab.transform.rotation;
        foreach (Vector2Int index in selected)
        {
            GameObject fixture = fixtures[index.x, index.y];
            if (fixture == null) continue;

            Vector3 position = fixture.transform.position + Vector3.up * lightPrefabYOffset;
            Instantiate(lightPrefab, position, rotation, _pointLightsRoot.transform);
        }
    }

    static List<Vector2Int> GetCeilingLightPattern(int countX, int countZ)
    {
        List<Vector2Int> selected = new List<Vector2Int>();
        bool canUseInnerGrid = countX > 2 && countZ > 2;
        int minX = canUseInnerGrid ? 1 : 0;
        int maxX = canUseInnerGrid ? countX - 2 : countX - 1;
        int minZ = canUseInnerGrid ? 1 : 0;
        int maxZ = canUseInnerGrid ? countZ - 2 : countZ - 1;
        int midX = Mathf.RoundToInt((minX + maxX) * 0.5f);
        int midZ = Mathf.RoundToInt((minZ + maxZ) * 0.5f);

        AddCeilingLightIndex(selected, minX, minZ);
        AddCeilingLightIndex(selected, minX, maxZ);
        AddCeilingLightIndex(selected, maxX, minZ);
        AddCeilingLightIndex(selected, maxX, maxZ);
        AddCeilingLightIndex(selected, midX, minZ);
        AddCeilingLightIndex(selected, midX, maxZ);
        AddCeilingLightIndex(selected, minX, midZ);
        AddCeilingLightIndex(selected, maxX, midZ);
        AddCeilingLightIndex(selected, midX, midZ);

        return selected;
    }

    static void AddCeilingLightIndex(List<Vector2Int> selected, int x, int z)
    {
        Vector2Int index = new Vector2Int(x, z);
        if (!selected.Contains(index)) selected.Add(index);
    }

    static int GetFixtureCount(float ceilingLength, float fixtureLength, float gap)
    {
        if (ceilingLength <= 0f || fixtureLength <= 0f) return 0;
        return Mathf.Max(0, Mathf.FloorToInt((ceilingLength + gap) / (fixtureLength + gap)));
    }

    static bool TryGetCombinedRendererBounds(GameObject root, out Bounds bounds)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return true;
    }

    static GameObject SpawnPlane(string planeName, Vector3 pos, Quaternion rot, Vector3 scale, Transform floorTransform)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
        go.name = planeName;
        go.AddComponent<Rigidbody>().isKinematic = true;
        go.AddComponent<BoxCollider>();
        // Drop the primitive's MeshCollider; walls use the BoxCollider.
        // Parent to the floor's parent so floor scale doesn't skew the walls
        go.transform.SetParent(floorTransform.parent, worldPositionStays: true);
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = scale;
        Destroy(go.GetComponent<MeshCollider>());
        return go;
    }

    // Sets transparency blend mode on Standard or URP Lit shaders.
    // The wall material's shader must support transparency for fading to work.
    static void EnsureTransparent(Material mat)
    {
        if (mat.HasProperty("_Surface"))
        {
            // URP Lit
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            // Alpha-tested wall/door materials would clip away instead of fading.
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_AlphaToMask", 0f);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = 3000;
        }
        else if (mat.HasProperty("_Mode"))
        {
            // Standard (Built-in RP)
            mat.SetFloat("_Mode", 3f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
    }
}
