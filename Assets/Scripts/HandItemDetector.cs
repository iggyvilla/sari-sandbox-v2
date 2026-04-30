using UnityEngine;

public class HandItemDetector : MonoBehaviour
{
    public ItemBBoxInfo DetectedItemBBoxInfo { get; private set; }
    public GameObject DetectedItem { get; private set; }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("RetailItem")) return;
        DetectedItem = other.gameObject;
        DetectedItemBBoxInfo = other.GetComponent<ItemBBoxInfo>();
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.gameObject != DetectedItem) return;
        DetectedItem = null;
        DetectedItemBBoxInfo = null;
    }
}
