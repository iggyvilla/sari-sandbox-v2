using System.Collections.Generic;
using UnityEngine;

public enum GpuSyntheticMeshKind
{
    LowProxy,
    RealProduct,
    HeavyProxy
}

public enum GpuSyntheticProductMode
{
    SingleProduct,
    RoundRobinProducts
}

public struct GpuSyntheticSpawnRequest
{
    public string itemIdPrefix;
    public int instanceCount;
    public GpuSyntheticMeshKind meshKind;
    public GpuSyntheticProductMode productMode;
    public Vector3 origin;
    public Vector3 right;
    public Vector3 forward;
    public float spacing;
    public Material material;
    public GameObject[] productPrefabs;
    public string[] productResourceIds;
}

public class GpuSyntheticIndirectSpawner : MonoBehaviour
{
    private readonly List<GameObject> _generatedProducts = new();
    private readonly List<Mesh> _generatedMeshes = new();
    private Material _generatedMaterial;

    public void Spawn(GpuSyntheticSpawnRequest request)
    {
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker == null)
        {
            Debug.LogError($"{nameof(GpuSyntheticIndirectSpawner)}: missing {nameof(GPUInstanceTracker)}.");
            return;
        }

        if (request.instanceCount <= 0)
            return;

        GameObject[] products = ResolveProducts(request);
        if (products == null || products.Length == 0)
        {
            Debug.LogError($"{nameof(GpuSyntheticIndirectSpawner)}: no usable synthetic products were found.");
            return;
        }

        Vector3 right = request.right.sqrMagnitude > 0.0001f ? request.right.normalized : Vector3.right;
        Vector3 forward = request.forward.sqrMagnitude > 0.0001f ? request.forward.normalized : Vector3.forward;
        float spacing = Mathf.Max(0.01f, request.spacing);
        int columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(request.instanceCount)));
        string prefix = string.IsNullOrEmpty(request.itemIdPrefix) ? "SYNTHETIC" : request.itemIdPrefix;

        for (int i = 0; i < request.instanceCount; i++)
        {
            GameObject product = products[
                request.productMode == GpuSyntheticProductMode.RoundRobinProducts
                    ? i % products.Length
                    : 0];

            int x = i % columns;
            int z = i / columns;
            Vector3 offset =
                right * ((x - (columns - 1) * 0.5f) * spacing) +
                forward * (z * spacing);

            string itemId = request.productMode == GpuSyntheticProductMode.RoundRobinProducts
                ? $"{prefix}_{product.name}"
                : $"{prefix}_{products[0].name}";

            tracker.AddToInstance(itemId, product, MakeInstanceData(product, request.origin + offset));
        }
    }

    public void ClearGeneratedProducts()
    {
        for (int i = 0; i < _generatedProducts.Count; i++)
        {
            if (_generatedProducts[i] != null)
                Destroy(_generatedProducts[i]);
        }

        for (int i = 0; i < _generatedMeshes.Count; i++)
        {
            if (_generatedMeshes[i] != null)
                Destroy(_generatedMeshes[i]);
        }

        if (_generatedMaterial != null)
            Destroy(_generatedMaterial);

        _generatedProducts.Clear();
        _generatedMeshes.Clear();
        _generatedMaterial = null;
    }

    private GameObject[] ResolveProducts(GpuSyntheticSpawnRequest request)
    {
        if (request.meshKind == GpuSyntheticMeshKind.LowProxy)
            return new[] { CreateGeneratedProduct("GPU_PERF_LOW_PROXY", CreateCubeMesh(), request.material) };

        if (request.meshKind == GpuSyntheticMeshKind.HeavyProxy)
            return new[] { CreateGeneratedProduct("GPU_PERF_HEAVY_PROXY", CreateUvSphereMesh(40, 80), request.material) };

        List<GameObject> products = new();
        if (request.productPrefabs != null)
        {
            for (int i = 0; i < request.productPrefabs.Length; i++)
            {
                if (IsUsableProduct(request.productPrefabs[i]))
                    products.Add(request.productPrefabs[i]);
            }
        }

        if (request.productResourceIds != null)
        {
            for (int i = 0; i < request.productResourceIds.Length; i++)
            {
                string id = request.productResourceIds[i];
                if (string.IsNullOrEmpty(id))
                    continue;

                GameObject product = Resources.Load<GameObject>("Prefabs/Products/" + id);
                if (IsUsableProduct(product) && !products.Contains(product))
                    products.Add(product);
            }
        }

        if (products.Count > 0)
            return products.ToArray();

        return new[] { CreateGeneratedProduct("GPU_PERF_FALLBACK_PROXY", CreateCubeMesh(), request.material) };
    }

    private static bool IsUsableProduct(GameObject product)
    {
        return product != null && product.transform.childCount > 0;
    }

    private GameObject CreateGeneratedProduct(string productName, Mesh mesh, Material material)
    {
        Material sourceMaterial = material != null ? material : GetGeneratedMaterial();

        GameObject root = new(productName) { hideFlags = HideFlags.HideAndDontSave };
        GameObject model = new(productName + "_Model") { hideFlags = HideFlags.HideAndDontSave };
        GameObject lod0 = new(productName + "_LOD0") { hideFlags = HideFlags.HideAndDontSave };

        model.transform.SetParent(root.transform, false);
        lod0.transform.SetParent(model.transform, false);

        MeshFilter meshFilter = lod0.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer = lod0.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterials = new[] { sourceMaterial };
        meshRenderer.enabled = false;

        root.SetActive(false);
        _generatedProducts.Add(root);
        _generatedMeshes.Add(mesh);
        return root;
    }

    private Material GetGeneratedMaterial()
    {
        if (_generatedMaterial != null)
            return _generatedMaterial;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");

        _generatedMaterial = new Material(shader)
        {
            name = "GPU Perf Synthetic Material",
            color = Color.white,
            hideFlags = HideFlags.HideAndDontSave
        };
        return _generatedMaterial;
    }

    private static InstanceData MakeInstanceData(GameObject product, Vector3 position)
    {
        Transform[] lods = LodHierarchy.ResolveLodTransforms(product);

        LodTransform MakeLodTransform(Transform src)
        {
            Quaternion q = src.rotation;
            LodTransform transformData = new()
            {
                position = position,
                rotation = new Vector4(q.x, q.y, q.z, q.w),
                scale = src.lossyScale
            };

            Mesh mesh = src.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh != null && src.position != Vector3.zero)
                transformData.position.y += -mesh.bounds.min.y * Mathf.Abs(src.lossyScale.y);

            return transformData;
        }

        return new InstanceData
        {
            lod0 = MakeLodTransform(lods[0]),
            lod1 = MakeLodTransform(lods[1]),
            lod2 = MakeLodTransform(lods[2]),
            lod3 = MakeLodTransform(lods[3])
        };
    }

    private static Mesh CreateCubeMesh()
    {
        Mesh mesh = new()
        {
            name = "GPU Perf Cube Mesh"
        };

        Vector3[] vertices =
        {
            new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f),
            new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f),
            new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)
        };

        int[] triangles =
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            2, 3, 7, 2, 7, 6,
            0, 4, 7, 0, 7, 3,
            1, 2, 6, 1, 6, 5
        };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh CreateUvSphereMesh(int latitudeSegments, int longitudeSegments)
    {
        latitudeSegments = Mathf.Max(4, latitudeSegments);
        longitudeSegments = Mathf.Max(8, longitudeSegments);

        List<Vector3> vertices = new();
        List<int> triangles = new();

        for (int lat = 0; lat <= latitudeSegments; lat++)
        {
            float v = lat / (float)latitudeSegments;
            float phi = Mathf.PI * v;
            float y = Mathf.Cos(phi) * 0.5f;
            float radius = Mathf.Sin(phi) * 0.5f;

            for (int lon = 0; lon <= longitudeSegments; lon++)
            {
                float u = lon / (float)longitudeSegments;
                float theta = Mathf.PI * 2f * u;
                vertices.Add(new Vector3(
                    Mathf.Cos(theta) * radius,
                    y,
                    Mathf.Sin(theta) * radius));
            }
        }

        int stride = longitudeSegments + 1;
        for (int lat = 0; lat < latitudeSegments; lat++)
        {
            for (int lon = 0; lon < longitudeSegments; lon++)
            {
                int a = lat * stride + lon;
                int b = a + stride;
                int c = b + 1;
                int d = a + 1;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(d);
                triangles.Add(d);
                triangles.Add(b);
                triangles.Add(c);
            }
        }

        Mesh mesh = new()
        {
            name = "GPU Perf Heavy Sphere Mesh"
        };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
