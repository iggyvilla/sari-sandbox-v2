using System.Collections.Generic;
using UnityEngine;

public class ItemPoolingManager : MonoBehaviour
{
    public static ItemPoolingManager Instance { get; private set; }

    private const float ItemMaxDepenetrationVelocity = 0.25f;
    private const int ItemSolverIterations = 12;
    private const int ItemSolverVelocityIterations = 4;
    // Prefabs ship with 50 rad/s and 0.05 damping; a stacked can that gets kicked then spins up and flings its column.
    private const float ItemMaxAngularVelocity = 8f;
    private const float ItemAngularDamping = 0.5f;
    private const float ItemSleepThreshold = 0.02f;
    // Default is 1 cm, which is a fifth of a can: resting stacks keep generating contacts and jitter.
    private const float ItemContactOffset = 0.003f;

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
        _poolParent.SetParent(transform, worldPositionStays: false);
        _stableItemMaterial = CreateStableItemMaterial();
    }

    void OnDestroy()
    {
        if (Instance != this) return;

        Instance = null;
        if (_stableItemMaterial != null) Destroy(_stableItemMaterial);
    }

    // Returns a physics-ready item at the given world position and rotation.
    // Reuses a pooled object if available, otherwise instantiates from Resources.
    public GameObject GetOrCreate(string itemId, Vector3 position, Quaternion rotation)
    {
        GameObject obj = DequeuePooled(itemId);

        if (obj != null)
        {
            obj.transform.SetParent(null);
            obj.transform.SetPositionAndRotation(position, rotation);
            RetailItemRuntimeService.ClearHeldItemLayer(obj);
            obj.SetActive(true);
        }
        else
        {
            obj = CreatePhysicsItem(itemId, position, rotation);
            RetailItemRuntimeService.ClearHeldItemLayer(obj);
        }

        if (obj == null) return null;

        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
        {
            RetailItemRuntimeService.MakeDynamic(rb);
            // ContinuousDynamic sweeps resting, exactly-touching stack members against each other.
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.angularDamping = ItemAngularDamping;
            rb.maxAngularVelocity = ItemMaxAngularVelocity;
            rb.sleepThreshold = ItemSleepThreshold;
            rb.maxDepenetrationVelocity = ItemMaxDepenetrationVelocity;
            rb.solverIterations = ItemSolverIterations;
            rb.solverVelocityIterations = ItemSolverVelocityIterations;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        RetailItemRuntimeService.SetSolidBoxCollidersEnabled(obj, true);

        // Physics previews should remain at their authored shelf pose until something
        // physically touches them. Clearing velocity before activation is not enough:
        // the solver can still create linear and angular velocity while resolving tiny
        // initial overlaps with the shelf or neighboring products.
        if (rb != null) rb.Sleep();

        return obj;
    }

    public void ReturnToPool(string itemId, GameObject obj)
    {
        if (obj == null) return;
        // Guard against double returns handing one object to two items.
        if (!obj.activeSelf && obj.transform.parent == _poolParent) return;

        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
            RetailItemRuntimeService.MakeKinematic(rb);

        RetailItemRuntimeService.SetSolidBoxCollidersEnabled(obj, false);
        RetailItemRuntimeService.ClearHeldItemLayer(obj);

        obj.SetActive(false);
        obj.transform.SetParent(_poolParent);

        if (!_pool.TryGetValue(itemId, out Queue<GameObject> queue))
        {
            queue = new Queue<GameObject>();
            _pool[itemId] = queue;
        }

        queue.Enqueue(obj);
    }

    // Skips pooled objects destroyed externally (e.g. by scene unload).
    private GameObject DequeuePooled(string itemId)
    {
        if (!_pool.TryGetValue(itemId, out Queue<GameObject> queue)) return null;

        while (queue.Count > 0)
        {
            GameObject obj = queue.Dequeue();
            if (obj != null) return obj;
        }

        return null;
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
        GameObject obj = ProductPrefabs.Spawn(itemId, position, rotation);
        if (obj != null) ApplyStablePhysicsMaterial(obj);
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
            if (collider.isTrigger) continue;
            collider.sharedMaterial = _stableItemMaterial;
            collider.contactOffset = ItemContactOffset;
        }
    }
}
