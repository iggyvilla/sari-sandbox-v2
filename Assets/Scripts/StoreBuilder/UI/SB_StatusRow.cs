using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Check / warning line used by the "Ready to play" list.</summary>
public class SB_StatusRow : MonoBehaviour
{
    public SB_Theme theme;
    public Image icon;
    public TMP_Text label;

    public void Set(bool ok, string text)
    {
        icon.sprite = theme.Icon(ok ? "check" : "warn");
        icon.color = ok ? theme.accent : theme.warn;
        label.text = text;
    }
}
