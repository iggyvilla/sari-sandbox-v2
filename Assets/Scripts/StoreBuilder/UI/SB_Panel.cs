using DG.Tweening;
using UnityEngine;

/// <summary>Shows and hides a panel or dialog with a fade and a small slide / scale (DOTween). Safe to call repeatedly.</summary>
[RequireComponent(typeof(CanvasGroup))]
public class SB_Panel : MonoBehaviour
{
    [Tooltip("The rect that slides and scales. Defaults to this object.")]
    public RectTransform animated;
    public Vector2 slideFrom = new Vector2(0f, -8f);
    public float scaleFrom = 1f;
    public float duration = 0.16f;

    private CanvasGroup _group;
    private Vector2 _home;
    private bool _ready;
    private bool _open;

    public bool IsOpen => _open;

    void Init()
    {
        if (_ready) return;
        _ready = true;
        _group = GetComponent<CanvasGroup>();
        if (animated == null) animated = (RectTransform)transform;
        _home = animated.anchoredPosition;
        _open = gameObject.activeSelf;
    }

    public void Set(bool open)
    {
        if (open) Show();
        else Hide();
    }

    public void Show()
    {
        Init();
        if (_open && gameObject.activeSelf) return;
        _open = true;
        gameObject.SetActive(true);
        DOTween.Kill(this);

        _group.blocksRaycasts = true;
        _group.alpha = 0f;
        animated.anchoredPosition = _home + slideFrom;
        animated.localScale = Vector3.one * scaleFrom;
        _group.DOFade(1f, duration).SetUpdate(true).SetId(this);
        animated.DOAnchorPos(_home, duration).SetEase(Ease.OutCubic).SetUpdate(true).SetId(this);
        if (scaleFrom != 1f) animated.DOScale(1f, duration).SetEase(Ease.OutCubic).SetUpdate(true).SetId(this);
    }

    public void Hide()
    {
        Init();
        if (!_open && !gameObject.activeSelf) return;
        _open = false;
        DOTween.Kill(this);
        if (!gameObject.activeInHierarchy)
        {
            gameObject.SetActive(false);
            return;
        }

        _group.blocksRaycasts = false;
        _group.DOFade(0f, duration * 0.75f).SetUpdate(true).SetId(this).OnComplete(() =>
        {
            animated.anchoredPosition = _home;
            gameObject.SetActive(false);
        });
    }

    void OnDisable()
    {
        DOTween.Kill(this);
        if (!_ready) return;
        animated.anchoredPosition = _home;
        animated.localScale = Vector3.one;
    }
}
