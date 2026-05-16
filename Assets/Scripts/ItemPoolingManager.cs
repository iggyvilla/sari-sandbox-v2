using System.Collections.Generic;
using UnityEngine;

public class ItemPoolingManager : MonoBehaviour
{
    public static ItemPoolingManager Instance { get; private set; }

    private readonly Dictionary<string, Queue<GameObject>> _pool = new();
    private Transform _poolParent;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        _poolParent = new GameObject("[ItemPool]").transform;
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
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        BoxCollider bc = obj.GetComponentInChildren<BoxCollider>(true);
        if (bc != null) bc.enabled = true;

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
}
