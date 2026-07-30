using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class SelfCheckoutUIHandler : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TextMeshProUGUI itemListText;
    [SerializeField] private TextMeshProUGUI subtotalText;
    [SerializeField] private TextMeshProUGUI taxTotalSavingsText;
    [SerializeField] private GameObject      startScreen;
    [SerializeField] private GameObject      mainScreen;

    [Header("Settings")]
    [SerializeField] private int   itemIDStringLength = 21;
    [Range(0f, 1f)]
    [SerializeField] private float taxRate            = 0.12f;

    // Ordered unique item IDs, so display order is preserved
    private List<string>            itemOrder    = new();
    private Dictionary<string, int> itemQuantities = new();

    // Running totals — recalculated fully on each change to avoid float drift
    private float subtotal;
    private float tax;
    private float totalSavings;

    public void StartScreenButtonPressed()
    {
        // This method is also invoked by BarcodeScanner.onSuccessfulScan.
        // Once the main screen is open, later scans must not reset the basket.
        if (startScreen != null && !startScreen.activeSelf) return;

        if (startScreen != null) startScreen.SetActive(false);
        if (mainScreen  != null) mainScreen.SetActive(true);
        ResetAllItems();
    }

    public void AddScannedItem(string itemId)
    {
        if (itemQuantities.ContainsKey(itemId))
        {
            itemQuantities[itemId]++;
        }
        else
        {
            itemOrder.Add(itemId);
            itemQuantities[itemId] = 1;
        }

        RecalculateTotals();
        RegenerateItemList();
        UpdateTotalsUI();
    }

    public void RemoveLastScannedItem()
    {
        if (itemOrder.Count == 0) return;

        string lastId = itemOrder[itemOrder.Count - 1];
        itemQuantities[lastId]--;

        if (itemQuantities[lastId] <= 0)
        {
            itemQuantities.Remove(lastId);
            itemOrder.RemoveAt(itemOrder.Count - 1);
        }

        RecalculateTotals();
        RegenerateItemList();
        UpdateTotalsUI();
    }

    public void ResetAllItems()
    {
        itemOrder.Clear();
        itemQuantities.Clear();
        subtotal     = 0f;
        tax          = 0f;
        totalSavings = 0f;

        RegenerateItemList();
        UpdateTotalsUI();
    }

    private void RecalculateTotals()
    {
        subtotal = 0f;
        tax      = 0f;

        foreach (string id in itemOrder)
        {
            if (DataHandler.Instance.itemPriceData.TryGetValue(id, out ItemPriceData priceData))
            {
                float lineTotal = priceData.pricePHP * itemQuantities[id];
                subtotal += lineTotal;
                tax      += lineTotal * taxRate;
            }
        }
    }

    private void RegenerateItemList()
    {
        System.Text.StringBuilder sb = new();

        foreach (string id in itemOrder)
        {
            string displayId = id.Length > itemIDStringLength
                ? id.Substring(0, itemIDStringLength) + "..."
                : id;

            float price = DataHandler.Instance.itemPriceData.TryGetValue(id, out ItemPriceData priceData)
                ? priceData.pricePHP
                : 0f;

            int qty = itemQuantities[id];

            sb.AppendLine($"{qty}x {displayId}");
            sb.AppendLine($" - {price:F2}php/pc");
        }

        if (itemListText != null)
            itemListText.text = sb.ToString();
    }

    private void UpdateTotalsUI()
    {
        if (subtotalText != null)
            subtotalText.text = $"Subtotal: {subtotal:F2} php";

        if (taxTotalSavingsText != null)
            taxTotalSavingsText.text = $"Total Savings: {totalSavings:F2}php\nTax: {tax:F2}php";
    }
}
