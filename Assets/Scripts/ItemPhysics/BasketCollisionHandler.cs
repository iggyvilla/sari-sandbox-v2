using UnityEngine;

public class BasketCollisionHandler : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("RetailItem")) return;

        Rigidbody rb = other.GetComponentInParent<Rigidbody>();
        if (rb != null)
            rb.isKinematic = true;

        other.transform.SetParent(transform, worldPositionStays: true);
    }
}
