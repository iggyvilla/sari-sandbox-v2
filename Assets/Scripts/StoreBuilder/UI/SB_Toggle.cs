using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Look of a Toggle as a switch (track + sliding knob) or a checkbox (tick). Polls the toggle, so values set
/// without notification (SetIsOnWithoutNotify) still animate and render correctly.
/// </summary>
[RequireComponent(typeof(Toggle))]
public class SB_Toggle : MonoBehaviour
{
    public SB_Theme theme;
    public Image track;
    public Image trackRing;
    public RectTransform knob;
    public Image knobImage;
    public Image tick;
    public float knobOff = 2f;
    public float knobOn = 18f;

    private Toggle _toggle;
    private CanvasGroup _group;
    private bool _shown;
    private bool _lastOn;
    private bool _lastInteractable = true;

    void Awake()
    {
        _toggle = GetComponent<Toggle>();
        _group = GetComponent<CanvasGroup>();
    }

    void OnEnable()
    {
        if (_toggle == null) Awake();
        Apply(false);
    }

    void OnDisable() => DOTween.Kill(this);

    void Update()
    {
        if (_shown && _toggle.isOn == _lastOn && _toggle.interactable == _lastInteractable) return;
        Apply(_shown);
    }

    void Apply(bool animate)
    {
        _shown = true;
        _lastOn = _toggle.isOn;
        _lastInteractable = _toggle.interactable;
        bool on = _lastOn;
        float d = animate ? SB_Theme.Fast : 0f;

        if (_group != null) _group.alpha = _lastInteractable ? 1f : 0.4f;
        Tint(track, on ? theme.accent : theme.field, d);
        Tint(trackRing, on ? theme.accent : theme.lineStrong, d);

        if (knob != null)
        {
            knob.DOAnchorPosX(on ? knobOn : knobOff, d).SetUpdate(true).SetId(this);
            Tint(knobImage, on ? theme.accentInk : new Color32(197, 203, 209, 255), d);
        }

        if (tick != null)
        {
            Tint(tick, new Color(theme.accentInk.r, theme.accentInk.g, theme.accentInk.b, on ? 1f : 0f), d);
            if (animate && on) tick.rectTransform.DOPunchScale(Vector3.one * 0.25f, 0.18f, 1, 0.5f).SetUpdate(true).SetId(this);
        }
    }

    static void Tint(Graphic graphic, Color color, float duration)
    {
        if (graphic == null) return;
        graphic.DOKill();
        if (duration > 0f) graphic.DOColor(color, duration).SetUpdate(true);
        else graphic.color = color;
    }
}
