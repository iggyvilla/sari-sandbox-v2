using UnityEngine;

// Debug: spawns each product as a GameObject (bottom row) and as a GPU instance (top row) to compare them.
public class BatchInstancerPreview : MonoBehaviour
{
    public GameObject[] products;
    public float spacing = 0.15f;
    public float rowGap = 0.3f;

    void Start()
    {
        for (int i = 0; i < products.Length; i++)
        {
            GameObject product = products[i];
            Vector3 x = transform.right * (i * spacing);
            GameObject go = Instantiate(product, transform.position + x, transform.rotation, transform);
            if (go.TryGetComponent(out Rigidbody rb)) rb.isKinematic = true;

            InstanceData data = ItemSpawner.CreateProductDrawTemplate(product, transform.rotation)
                .At(transform.position + x + transform.up * rowGap);
            GPUInstanceTracker.Instance.AddToInstance(product.name, product, data);
        }
    }
}
