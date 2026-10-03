using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// A row of mutually exclusive options with a sliding highlight. Each button maps to an integer in
/// <see cref="values"/> (an enum value, say), so the display order does not have to match the enum.
/// A value that matches no option clears the highlight.
/// </summary>
public class SB_Segmented : MonoBehaviour
{
    [Serializable] public class IntEvent : UnityEvent<int> { }

    public SB_Theme theme;
    public RectTransform indicator;
    public Vector2 indicatorY = new Vector2(0f, 1f);
    public Button[] buttons;
    public TMP_Text[] labels;
    public int[] values;
    public IntEvent onValueChanged = new IntEvent();

    private int _index = -1;
    private float _position;
    private bool _ready;

    /// <summary>The selected option's value, or -1 when none is selected.</summary>
    public int value => _index >= 0 ? values[_index] : -1;

    public bool interactable
    {
        get => buttons.Length == 0 || buttons[0].interactable;
        set
        {
            foreach (Button button in buttons) button.interactable = value;
            if (TryGetComponent(out CanvasGroup group)) group.alpha = value ? 1f : 0.4f;
        }
    }

    void Awake() => Init();

    void OnEnable()
    {
        Init();
        Apply(false);
    }

    void OnDisable() => DOTween.Kill(this);

    void Init()
    {
        if (_ready) return;
        _ready = true;
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            buttons[i].onClick.AddListener(() => Select(index));
        }
    }

    public void SetValueWithoutNotify(int newValue)
    {
        Init();
        int index = Array.IndexOf(values, newValue);
        if (index == _index) return;
        _index = index;
        Apply(isActiveAndEnabled);
    }

    void Select(int index)
    {
        if (index == _index) return;
        _index = index;
        Apply(true);
        onValueChanged.Invoke(values[index]);
    }

    void Apply(bool animate)
    {
        if (labels == null) return;
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i].DOKill();
            Color color = i == _index ? theme.text : theme.muted;
            if (animate) labels[i].DOColor(color, SB_Theme.Fast).SetUpdate(true);
            else labels[i].color = color;
        }

        if (indicator == null) return;
        DOTween.Kill(this);
        indicator.gameObject.SetActive(_index >= 0);
        if (_index < 0) return;

        if (!animate || !indicator.gameObject.activeInHierarchy)
        {
            SetIndicator(_index);
            return;
        }
        DOTween.To(() => _position, SetIndicator, _index, SB_Theme.Medium).SetEase(Ease.OutCubic).SetUpdate(true).SetId(this);
    }

    void SetIndicator(float position)
    {
        _position = position;
        float count = Mathf.Max(buttons.Length, 1);
        indicator.anchorMin = new Vector2(position / count, indicatorY.x);
        indicator.anchorMax = new Vector2((position + 1f) / count, indicatorY.y);
    }
}
