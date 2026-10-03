using UnityEngine;
using UnityEngine.UI;

/// <summary>Sizes a top-anchored scrolling panel to its content, capped so it never reaches the status bar.</summary>
public class SB_FitHeight : MonoBehaviour
{
    public RectTransform content;
    public float topMargin = 64f;
    public float bottomMargin = 46f;

    private RectTransform _rect;
    private RectTransform _canvas;

    void LateUpdate()
    {
        if (_rect == null)
        {
            _rect = (RectTransform)transform;
            _canvas = (RectTransform)GetComponentInParent<Canvas>().rootCanvas.transform;
        }

        float max = _canvas.rect.height - topMargin - bottomMargin;
        float height = Mathf.Min(LayoutUtility.GetPreferredHeight(content), max);
        if (!Mathf.Approximately(_rect.rect.height, height))
            _rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
    }
}
