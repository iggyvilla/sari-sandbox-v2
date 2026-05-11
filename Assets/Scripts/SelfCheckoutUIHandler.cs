using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class SelfCheckoutUIHandler : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI itemListText;
    public TextMeshProUGUI subtotalText;
    public TextMeshProUGUI taxTotalSavingsText;
    public GameObject      startScreen;
    public GameObject      mainScreen;

    [Header("Settings")]
    public int   itemIDStringLength = 18;
    [Range(0f, 1f)]
    public float taxRate            = 0.12f;

    private List<string> scannedItemIds = new();
    private float        subtotal;
    private float        tax;
    private float        totalSavings;

    public void StartScreenButtonPressed()
    {
        if (startScreen != null) startScreen.SetActive(false);
        if (mainScreen  != null) mainScreen.SetActive(true);
        ResetAllItems();
    }

    public void AddScannedItem(string itemId)
    {
        scannedItemIds.Add(itemId);

        if (DataHandler.Instance.itemPriceData.TryGetValue(itemId, out ItemPriceData priceData))
        {
            subtotal     += priceData.pricePHP;
            tax          += priceData.pricePHP * taxRate;
        }

        RegenerateItemList();
        UpdateTotalsUI();
    }

    public void RemoveLastScannedItem()
    {
        if (scannedItemIds.Count == 0) return;

        string lastId = scannedItemIds[scannedItemIds.Count - 1];
        scannedItemIds.RemoveAt(scannedItemIds.Count - 1);

        if (DataHandler.Instance.itemPriceData.TryGetValue(lastId, out ItemPriceData priceData))
        {
            subtotal     -= priceData.pricePHP;
            tax          -= priceData.pricePHP * taxRate;
        }

        RegenerateItemList();
        UpdateTotalsUI();
    }

    public void ResetAllItems()
    {
        scannedItemIds.Clear();
        subtotal     = 0f;
        tax          = 0f;
        totalSavings = 0f;

        RegenerateItemList();
        UpdateTotalsUI();
    }

    private void RegenerateItemList()
    {
        System.Text.StringBuilder sb = new();

        foreach (string id in scannedItemIds)
        {
            string displayId = id.Length > itemIDStringLength
                ? id.Substring(0, itemIDStringLength) + "..."
                : id;

            float price = DataHandler.Instance.itemPriceData.TryGetValue(id, out ItemPriceData priceData)
                ? priceData.pricePHP
                : 0f;

            sb.AppendLine($"{displayId} {price:F2}PHP");
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
