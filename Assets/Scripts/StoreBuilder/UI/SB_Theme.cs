using System;
using TMPro;
using UnityEngine;

/// <summary>Colors, fonts and sprites of the Store Builder UI. The generated UI reads this; rebuild it after editing.</summary>
[CreateAssetMenu(menuName = "Sari/Store Builder Theme", fileName = "SB_Theme")]
public class SB_Theme : ScriptableObject
{
    public const float Fast = 0.12f;
    public const float Medium = 0.18f;

    [Header("Colors")]
    public Color panel = new Color32(20, 22, 25, 236);
    public Color panelSolid = new Color32(24, 26, 30, 255);
    public Color field = new Color32(16, 18, 20, 255);
    public Color line = new Color(1f, 1f, 1f, 0.09f);
    public Color lineStrong = new Color(1f, 1f, 1f, 0.2f);
    public Color text = new Color32(236, 239, 242, 255);
    public Color muted = new Color32(163, 170, 178, 255);
    public Color accent = new Color32(111, 211, 164, 255);
    public Color accentHover = new Color32(134, 224, 182, 255);
    public Color accentInk = new Color32(11, 26, 19, 255);
    public Color warn = new Color32(242, 179, 94, 255);
    public Color danger = new Color32(255, 154, 140, 255);
    public Color fill = new Color(1f, 1f, 1f, 0.05f);
    public Color fillHover = new Color(1f, 1f, 1f, 0.12f);
    public Color fillPressed = new Color(1f, 1f, 1f, 0.2f);
    public Color scrim = new Color32(8, 9, 10, 153);

    public Color AccentSoft => new Color(accent.r, accent.g, accent.b, 0.17f);
    public Color Clear => new Color(1f, 1f, 1f, 0f);

    [Header("Fonts")]
    public TMP_FontAsset sans;
    public TMP_FontAsset sansMedium;
    public TMP_FontAsset sansSemiBold;
    public TMP_FontAsset mono;
    public TMP_FontAsset monoMedium;

    [Header("Shapes")]
    public Sprite fillR8;
    public Sprite ringR8;
    public Sprite fillR12;
    public Sprite ringR12;
    public Sprite pill;
    public Sprite ringPill;
    public Sprite pillSmall;
    public Sprite circle;

    [Header("Icons")]
    public IconEntry[] icons;

    [Serializable]
    public struct IconEntry
    {
        public string name;
        public Sprite sprite;
    }

    public Sprite Icon(string iconName)
    {
        foreach (IconEntry entry in icons)
            if (entry.name == iconName) return entry.sprite;
        return null;
    }
}
