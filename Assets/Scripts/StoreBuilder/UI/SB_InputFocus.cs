using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Tints an input field's outline with the accent color while it is being edited.</summary>
public class SB_InputFocus : MonoBehaviour
{
    public SB_Theme theme;
    public TMP_InputField input;
    public Image ring;

    private bool _focused;

    void OnEnable()
    {
        _focused = false;
        ring.color = theme.lineStrong;
    }

    void Update()
    {
        if (input.isFocused == _focused) return;
        _focused = input.isFocused;
        ring.DOKill();
        ring.DOColor(_focused ? theme.accent : theme.lineStrong, SB_Theme.Fast).SetUpdate(true);
    }
}
