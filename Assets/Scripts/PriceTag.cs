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
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Awake()
    {
        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        if (_itemIDText != null) return;

        _itemIDText = itemIDText.GetComponent<TextMeshPro>();
        _priceText = priceText.GetComponent<TextMeshPro>();
        _priceDecimalText = priceDecimalText.GetComponent<TextMeshPro>();
        _barcodeText = barcodeText.GetComponent<TextMeshPro>();
        _itemWeightText = itemWeightText.GetComponent<TextMeshPro>();

        _itemIDText.isTextObjectScaleStatic = false;
        _priceText.isTextObjectScaleStatic = false;
        _priceDecimalText.isTextObjectScaleStatic = false;
        _barcodeText.isTextObjectScaleStatic = false;
        _itemWeightText.isTextObjectScaleStatic = false;
    }

    public void SetValues(string idText, float price, string weight)
    {
        EnsureInitialized();

        // Price without the decimal
        int priceWhole = (int) (price - price % 1);
        // Only the decimal of the price
        int priceDecimal = Mathf.RoundToInt(price % 1 * 10);
        _priceDecimalText.text = (priceDecimal < 10 ? "0" : "") + priceDecimal;
        _priceText.text = priceWhole.ToString();
        _itemWeightText.text = weight;
        _itemIDText.text = idText;
        // Only get first 16 letters for barcode 
        // to prevent it to from getting too long
        _barcodeText.text = idText.Substring(0, Mathf.Min(idText.Length, 16));
        
        _itemIDText.isTextObjectScaleStatic = true;
        _priceText.isTextObjectScaleStatic = true;
        _priceDecimalText.isTextObjectScaleStatic = true;
        _barcodeText.isTextObjectScaleStatic = true;
        _itemWeightText.isTextObjectScaleStatic = true;
    }
    
}
