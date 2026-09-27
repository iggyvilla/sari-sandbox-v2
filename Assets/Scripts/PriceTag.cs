using TMPro;
using UnityEngine;

public class PriceTag : MonoBehaviour
{

    [SerializeField]
    private GameObject itemIDText;
    private TextMeshPro _itemIDText;
    
    [SerializeField]
    private GameObject priceText;
    private TextMeshPro _priceText;
    
    [SerializeField]
    private GameObject priceDecimalText;
    private TextMeshPro _priceDecimalText;
    
    [SerializeField]
    private GameObject barcodeText;
    private TextMeshPro _barcodeText;
    
    [SerializeField]
    private GameObject itemWeightText;
    private TextMeshPro _itemWeightText;

    private TextMeshPro[] _allTexts;

    void Awake()
    {
        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        if (_allTexts != null) return;

        _itemIDText = itemIDText.GetComponent<TextMeshPro>();
        _priceText = priceText.GetComponent<TextMeshPro>();
        _priceDecimalText = priceDecimalText.GetComponent<TextMeshPro>();
        _barcodeText = barcodeText.GetComponent<TextMeshPro>();
        _itemWeightText = itemWeightText.GetComponent<TextMeshPro>();

        _allTexts = new[] { _itemIDText, _priceText, _priceDecimalText, _barcodeText, _itemWeightText };
        SetTextScaleStatic(false);
    }

    private void SetTextScaleStatic(bool isStatic)
    {
        foreach (TextMeshPro text in _allTexts)
            text.isTextObjectScaleStatic = isStatic;
    }

    public void SetValues(string idText, float price, string weight)
    {
        EnsureInitialized();

        // Round once to whole centavos so e.g. 107.35 renders as "107" + "35".
        int totalCents = Mathf.RoundToInt(price * 100f);
        _priceText.text = (totalCents / 100).ToString();
        _priceDecimalText.text = (totalCents % 100).ToString("00");
        _itemWeightText.text = weight;
        _itemIDText.text = idText;
        // Only get first 16 letters for barcode 
        // to prevent it to from getting too long
        _barcodeText.text = idText.Substring(0, Mathf.Min(idText.Length, 16));
        
        SetTextScaleStatic(true);
    }
    
}
