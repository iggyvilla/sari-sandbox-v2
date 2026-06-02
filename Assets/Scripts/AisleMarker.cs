using TMPro;
using UnityEngine;

public class AisleMarker : MonoBehaviour
{
    [Header("Aisle Categories")]
    [SerializeField] private TextMeshPro aisleCategory1Front;
    [SerializeField] private TextMeshPro aisleCategory1Back;
    [SerializeField] private TextMeshPro aisleCategory2Front;
    [SerializeField] private TextMeshPro aisleCategory2Back;
    [SerializeField] private TextMeshPro aisleCategory3Front;
    [SerializeField] private TextMeshPro aisleCategory3Back;

    [Header("Aisle Number")]
    [SerializeField] private TextMeshPro aisleNumberFront;
    [SerializeField] private TextMeshPro aisleNumberBack;

    [Header("Cables")]
    [SerializeField] private GameObject cable1;
    [SerializeField] private GameObject cable2;
    [SerializeField] private float cableLength;

    [Header("Marker Information")]
    [SerializeField] private string category1;
    [SerializeField] private string category2;
    [SerializeField] private string category3;
    [SerializeField] private int aisleNumber;

    private bool _hasWarnedAboutScale;

    // Read-only accessors so the Store Builder UI can populate its fields when a
    // marker is selected for editing.
    public string Category1   => category1;
    public string Category2   => category2;
    public string Category3   => category3;
    public int    AisleNumber => aisleNumber;
    public float  CableLength => cableLength;

    void Start()
    {
        BuildAisleMarker(category1, category2, category3, aisleNumber,
            cableLength);
    }

    public void BuildAisleMarker(
        string category1,
        string category2,
        string category3,
        int aisleNumber,
        float cableLength)
    {
        this.category1 = category1;
        this.category2 = category2;
        this.category3 = category3;
        this.aisleNumber = aisleNumber;
        this.cableLength = Mathf.Max(0f, cableLength);

        SetText(aisleCategory1Front, category1);
        SetText(aisleCategory1Back, category1);
        SetText(aisleCategory2Front, category2);
        SetText(aisleCategory2Back, category2);
        SetText(aisleCategory3Front, category3);
        SetText(aisleCategory3Back, category3);

        string aisleNumberText = aisleNumber.ToString();
        SetText(aisleNumberFront, aisleNumberText);
        SetText(aisleNumberBack, aisleNumberText);

        RefreshHeight();
    }

    public void RefreshHeight()
    {
        if (DataHandler.Instance == null)
        {
            Debug.LogError($"{nameof(AisleMarker)} on {name} cannot refresh height because DataHandler.Instance is missing.");
            return;
        }

        cableLength = Mathf.Max(0f, cableLength);

        if (!Mathf.Approximately(transform.lossyScale.y, 1f))
        {
            if (!_hasWarnedAboutScale)
            {
                Debug.LogWarning(
                    $"{nameof(AisleMarker)} on {name} expects an effective Y scale of 1. " +
                    "Cable length may not match the ceiling-to-board distance.");
                _hasWarnedAboutScale = true;
            }
        }
        else
        {
            _hasWarnedAboutScale = false;
        }

        Vector3 markerPosition = transform.position;
        markerPosition.y = DataHandler.Instance.CeilingY - cableLength;
        transform.position = markerPosition;

        SetCableLength(cable1);
        SetCableLength(cable2);
    }

    private void SetCableLength(GameObject cable)
    {
        if (cable == null) return;

        Transform cableTransform = cable.transform;

        Vector3 cablePosition = cableTransform.localPosition;
        cablePosition.y = cableLength * 0.5f;
        cableTransform.localPosition = cablePosition;

        Vector3 cableScale = cableTransform.localScale;
        cableScale.y = cableLength;
        cableTransform.localScale = cableScale;
    }

    private static void SetText(TextMeshPro textMesh, string value)
    {
        if (textMesh != null)
            textMesh.text = value;
    }
}
