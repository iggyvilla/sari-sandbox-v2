using System.Collections.Generic;
using UnityEngine;

public class ItemPoolingManager : MonoBehaviour
{
    public static ItemPoolingManager Instance { get; private set; }

    private const float ItemMaxDepenetrationVelocity = 2f;
    private const int ItemSolverIterations = 12;
    private const int ItemSolverVelocityIterations = 4;
    private const float SpawnOverlapPadding = 0.002f;
    private const int SpawnOverlapResolveIterations = 6;

    private readonly Dictionary<string, Queue<GameObject>> _pool = new();
    private Transform _poolParent;
    private PhysicsMaterial _stableItemMaterial;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        _poolParent = new GameObject("[ItemPool]").transform;
        _stableItemMaterial = CreateStableItemMaterial();
    }

    // Returns a physics-ready item at the given world position and rotation.
    // Reuses a pooled object if available, otherwise instantiates from Resources.
    public GameObject GetOrCreate(string itemId, Vector3 position, Quaternion rotation)
    {
        GameObject obj;

        if (_pool.TryGetValue(itemId, out Queue<GameObject> queue) && queue.Count > 0)
        {
            obj = queue.Dequeue();
            obj.transform.SetParent(null);
            obj.transform.SetPositionAndRotation(position, rotation);
            obj.SetActive(true);
        }
        else
        {
            obj = CreatePhysicsItem(itemId, position, rotation);
        }

        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Extrapolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.maxDepenetrationVelocity = ItemMaxDepenetrationVelocity;
            rb.solverIterations = ItemSolverIterations;
            rb.solverVelocityIterations = ItemSolverVelocityIterations;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        ApplyStablePhysicsMaterial(obj);

        BoxCollider bc = obj.GetComponentInChildren<BoxCollider>(true);
        if (bc != null) bc.enabled = true;

        // ResolveSpawnOverlaps(obj);

        return obj;
    }

    public void ReturnToPool(string itemId, GameObject obj)
    {
        if (obj == null) return;

        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }

        BoxCollider bc = obj.GetComponentInChildren<BoxCollider>(true);
        if (bc != null) bc.enabled = false;

        obj.SetActive(false);
        obj.transform.SetParent(_poolParent);

        if (!_pool.ContainsKey(itemId))
            _pool[itemId] = new Queue<GameObject>();

        _pool[itemId].Enqueue(obj);
    }

    public void ClearPool()
    {
        foreach (var queue in _pool.Values)
            foreach (var obj in queue)
                if (obj != null) Destroy(obj);

        _pool.Clear();
    }

    private GameObject CreatePhysicsItem(string itemId, Vector3 position, Quaternion rotation)
    {
        GameObject prefab = Resources.Load<GameObject>("Prefabs/Products/" + itemId);
        if (prefab == null)
        {
            Debug.LogError($"ItemPoolingManager: prefab not found for {itemId}");
            return null;
        }

        GameObject obj = Instantiate(prefab, position, rotation);
        obj.name = itemId;
        obj.tag = "RetailItem";

        return obj;
    }

    private static PhysicsMaterial CreateStableItemMaterial()
    {
        return new PhysicsMaterial("Stable Retail Item")
        {
            dynamicFriction = 0.7f,
            staticFriction = 0.8f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Maximum,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
    }

    private void ApplyStablePhysicsMaterial(GameObject obj)
    {
        if (_stableItemMaterial == null)
            _stableItemMaterial = CreateStableItemMaterial();

        Collider[] colliders = obj.GetComponentsInChildren<Collider>(true);
        foreach (Collider collider in colliders)
        {
            if (!collider.isTrigger)
                collider.sharedMaterial = _stableItemMaterial;
        }
    }

    private static void ResolveSpawnOverlaps(GameObject obj)
    {
        Collider[] itemColliders = obj.GetComponentsInChildren<Collider>(true);

        for (int i = 0; i < SpawnOverlapResolveIterations; i++)
        {
            bool moved = false;

            foreach (Collider itemCollider in itemColliders)
            {
                if (!ShouldResolveCollider(itemCollider)) continue;

                Bounds bounds = itemCollider.bounds;
                Collider[] overlaps = Physics.OverlapBox(
                    bounds.center,
                    bounds.extents + Vector3.one * SpawnOverlapPadding,
                    Quaternion.identity,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);

                foreach (Collider overlap in overlaps)
                {
                    if (!ShouldResolveCollider(overlap) || overlap.transform.IsChildOf(obj.transform))
                        continue;

                    if (!Physics.ComputePenetration(
                            itemCollider,
                            itemCollider.transform.position,
                            itemCollider.transform.rotation,
                            overlap,
                            overlap.transform.position,
                            overlap.transform.rotation,
                            out Vector3 direction,
                            out float distance))
                        continue;

                    obj.transform.position += direction * (distance + SpawnOverlapPadding);
                    Physics.SyncTransforms();
                    moved = true;
                    break;
                }

                if (moved) break;
            }

            if (!moved) break;
        }
    }

    private static bool ShouldResolveCollider(Collider collider)
    {
        return collider != null && collider.enabled && !collider.isTrigger && collider.gameObject.activeInHierarchy;
    }
}
