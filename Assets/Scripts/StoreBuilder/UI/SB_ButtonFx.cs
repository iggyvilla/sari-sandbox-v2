using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Hover / press / selected / disabled look for a Store Builder button, tweened with DOTween.</summary>
public class SB_ButtonFx : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    public SB_Theme theme;
    public Graphic target;
    public Image ring;
    public Color normal;
    public Color hover;
    public Color pressed;
    public Color selected;
    public float ringAlpha = 0.6f;
    public bool pressScale = true;

    private Button _button;
    private CanvasGroup _group;
    private bool _over;
    private bool _down;
    private bool _selected;
    private bool _enabled = true;

    void Awake()
    {
        _button = GetComponent<Button>();
        _group = GetComponent<CanvasGroup>();
        Apply(false);
    }

    void OnEnable() => Apply(false);

    void OnDisable()
    {
        _over = _down = false;
        DOTween.Kill(this);
        transform.localScale = Vector3.one;
    }

    void Update()
    {
        bool interactable = _button == null || _button.interactable;
        if (interactable == _enabled) return;
        _enabled = interactable;
        if (_group != null) _group.alpha = interactable ? 1f : 0.4f;
    }

    public void SetColors(Color newNormal, Color newHover, Color newPressed)
    {
        normal = newNormal;
        hover = newHover;
        pressed = newPressed;
        Apply(true);
    }

    public void SetSelected(bool value)
    {
        if (_selected == value) return;
        _selected = value;
        Apply(true);
    }

    public void OnPointerEnter(PointerEventData e) { _over = true; Apply(true); }
    public void OnPointerExit(PointerEventData e) { _over = false; _down = false; Apply(true); }

    public void OnPointerDown(PointerEventData e)
    {
        _down = true;
        Apply(true);
        if (pressScale) transform.DOScale(0.97f, 0.06f).SetUpdate(true).SetId(this);
    }

    public void OnPointerUp(PointerEventData e)
    {
        _down = false;
        Apply(true);
        if (pressScale) transform.DOScale(1f, 0.1f).SetUpdate(true).SetId(this);
    }

    void Apply(bool animate)
    {
        if (target == null) return;
        Color color = _selected ? selected : _down ? pressed : _over ? hover : normal;
        target.DOKill();
        if (animate) target.DOColor(color, SB_Theme.Fast).SetUpdate(true);
        else target.color = color;

        if (ring == null) return;
        Color ringColor = ring.color;
        ringColor.a = _selected ? ringAlpha : 0f;
        ring.DOKill();
        if (animate) ring.DOColor(ringColor, SB_Theme.Fast).SetUpdate(true);
        else ring.color = ringColor;
    }
}
