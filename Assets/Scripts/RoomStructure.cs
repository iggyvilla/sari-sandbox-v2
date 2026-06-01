using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(MeshRenderer))]
public class RoomStructure : MonoBehaviour
{
    [Header("Materials")]
    public Material ceilingMaterial;
    public Material floorMaterialPlay;
    public Material floorMaterialBuilder;
    public Material wallMaterialTransparent;
    public Material wallMaterialOpaque;

    [Header("Room")]
    public float wallHeight = 3f;
    public float CeilingY => transform.position.y + wallHeight;

    [Header("Ceiling Lights")]
    public GameObject ceilingLightPrefab;
    [Min(0f)] public float ceilingLightSpacingX = 1f;
    [Min(0f)] public float ceilingLightSpacingZ = 1f;
    public float ceilingLightYOffset = 0f;

    [Header("Wall Fading")]
    // Dot-product threshold at which a wall begins to fade (0 = instant, 0.3 = starts when wall begins facing camera)
    [Range(0f, 0.9f)] public float fadeStartDot = 0.2f;
    // Minimum alpha a faded wall reaches (never fully invisible so geometry remains hittable)
    [Range(0f, 0.5f)] public float minWallAlpha = 0.05f;

    private bool _isStoreBuilder;
    private Camera _cam;
    private GameObject _ceiling;
    private GameObject _ceilingLightsRoot;

    private struct WallEntry
    {
        public GameObject go;
        public Vector3 outwardNormal;
        public Material mat;
    }
    private WallEntry[] _walls;

    void Awake()
    {
        _isStoreBuilder = SceneManager.GetActiveScene().name == "StoreBuilder";
        _cam = Camera.main;
        GetComponent<MeshRenderer>().sharedMaterial = _isStoreBuilder ? floorMaterialBuilder : floorMaterialPlay;
        BuildWalls();
    }

    void OnDestroy() => DestroyGeneratedObjects();

    // ── Public API ────────────────────────────────────────────────────────────

    public void SetFloorDimensions(float width, float height)
    {
        Vector3 scale = transform.localScale;
        scale.x = width;
        scale.z = height;
        transform.localScale = scale;
        BuildWalls();
    }

    public void BuildWalls()
    {
        DestroyGeneratedObjects();

        // Unity plane: 1 unit of scale = 10 world units, centered
        float halfW = transform.localScale.x * 5f;
        float halfD = transform.localScale.z * 5f;
        Vector3 center = transform.position;
        float midY = center.y + wallHeight * 0.5f;

        // Rotation logic: Unity plane normal is local +Y. Negated to face inward (into the room):
        //   Euler(0,0, 90)  → normal = +X   Euler(0,0,-90) → normal = -X
        //   Euler(-90,0,0)  → normal = +Z   Euler(90,0,0)  → normal = -Z
        var defs = new (Vector3 pos, Vector3 euler, Vector3 scale, Vector3 normal)[]
        {
            (center + new Vector3( halfW, midY, 0), new Vector3(  0,  0,  90), new Vector3(wallHeight / 10f, 1f, halfD * 2f / 10f), Vector3.right),
            (center + new Vector3(-halfW, midY, 0), new Vector3(  0,  0, -90), new Vector3(wallHeight / 10f, 1f, halfD * 2f / 10f), Vector3.left),
            (center + new Vector3(0, midY,  halfD), new Vector3(-90,  0,   0), new Vector3(halfW * 2f / 10f, 1f, wallHeight / 10f),  Vector3.forward),
            (center + new Vector3(0, midY, -halfD), new Vector3( 90,  0,   0), new Vector3(halfW * 2f / 10f, 1f, wallHeight / 10f),  Vector3.back),
        };

        Material wallMaterialSource = _isStoreBuilder ? wallMaterialTransparent : wallMaterialOpaque;
        _walls = new WallEntry[4];
        for (int i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            var go = SpawnPlane($"Wall_{d.normal}", d.pos, Quaternion.Euler(d.euler), d.scale, transform);
            var mat = new Material(wallMaterialSource);
            if (_isStoreBuilder) EnsureTransparent(mat);
            go.GetComponent<MeshRenderer>().material = mat;
            _walls[i] = new WallEntry { go = go, outwardNormal = d.normal, mat = mat };
        }

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
    }

    public void DestroyGeneratedObjects()
    {
        if (_walls != null)
        {
            foreach (var w in _walls)
            {
                if (w.go  != null) Destroy(w.go);
                if (w.mat != null) Destroy(w.mat);
            }
            _walls = null;
        }

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
    }

    // ── Per-frame fading ─────────────────────────────────────────────────────

    void Update()
    {
        if (_walls == null || _cam == null || !_isStoreBuilder) return;

        Vector3 center = transform.position;
        Vector3 toCam = _cam.transform.position - center;
        // Ignore vertical component — fading is purely about horizontal viewing angle
        Vector3 toCamFlat = new Vector3(toCam.x, 0f, toCam.z).normalized;

        foreach (var w in _walls)
        {
            if (w.go == null || w.mat == null) continue;

            // dot = 1: camera is directly outside this wall (wall blocks the view) → fade
            // dot = 0: wall is side-on to camera → opaque
            float dot = Mathf.Clamp01(Vector3.Dot(toCamFlat, w.outwardNormal));
            float t = Mathf.Clamp01((dot - fadeStartDot) / (1f - fadeStartDot));
            float alpha = Mathf.Lerp(1f, minWallAlpha, t);

            Color c = w.mat.color;
            if (!Mathf.Approximately(c.a, alpha))
            {
                c.a = alpha;
                w.mat.color = c;
            }
        }
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
            }
        }
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
        // Parent to the floor's parent so floor scale doesn't skew the walls
        go.transform.SetParent(floorTransform.parent, worldPositionStays: true);
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = scale;
        Destroy(go.GetComponent<Collider>());
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
