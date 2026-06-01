using System;
using System.Globalization;
using TMPro;
using UnityEngine;

public class ExpirationDateDecalHandler : MonoBehaviour
{
    [SerializeField]
    private TMP_Text expirationDateText;

    public bool HasTextReference => expirationDateText != null;

    public void SetExpirationDate(DateTime date)
    {
        if (expirationDateText == null)
        {
            Debug.LogError($"{nameof(ExpirationDateDecalHandler)} on {name} is missing its TMP text reference.");
            return;
        }

        expirationDateText.isTextObjectScaleStatic = false;
        expirationDateText.text = FormatExpirationDate(date);
        expirationDateText.isTextObjectScaleStatic = true;
    }

    public static string FormatExpirationDate(DateTime date)
    {
        return "EXP: " + date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }
}
