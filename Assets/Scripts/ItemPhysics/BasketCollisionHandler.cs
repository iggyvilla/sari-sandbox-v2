using UnityEngine;

public class BasketCollisionHandler : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("RetailItem")) return;

        Rigidbody rb = other.GetComponentInParent<Rigidbody>();
        if (rb != null)
            rb.isKinematic = true;

        // Move the whole item (its Rigidbody root), not just the collider that entered.
        Transform item = rb != null ? rb.transform : other.transform;
        item.SetParent(transform, worldPositionStays: true);
    }
}
