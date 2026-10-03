using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Widget factory for the generated Store Builder UI. Every widget matches the design mock-up's style kit.</summary>
public class StoreBuilderUIKit
{
    public enum ButtonStyle { Secondary, Primary, Warn, Ghost, Danger }
    public enum ChipKind { Neutral, Warn, Ok }

    public struct ButtonParts
    {
        public Button button;
        public SB_ButtonFx fx;
        public TMP_Text label;
        public Image icon;
    }

    public readonly SB_Theme T;

    public StoreBuilderUIKit(SB_Theme theme) => T = theme;

    // ── Primitives ────────────────────────────────────────────────────────────

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    public static void Stretch(RectTransform r, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(left, bottom);
        r.offsetMax = new Vector2(-right, -top);
    }

    public static void Place(RectTransform r, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
    {
        r.anchorMin = r.anchorMax = anchor;
        r.pivot = pivot;
        r.anchoredPosition = position;
        r.sizeDelta = size;
    }

    public static Image Img(RectTransform r, Sprite sprite, Color color, bool raycast = false)
    {
        var image = r.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = raycast;
        image.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
        return image;
    }

    /// <summary>An outline over its parent's shape; ignored by layout groups.</summary>
    public static Image Ring(RectTransform parent, Sprite ring, Color color)
    {
        RectTransform rect = Rect("Ring", parent);
        Stretch(rect);
        IgnoreLayout(rect.gameObject);
        return Img(rect, ring, color);
    }

    public TextMeshProUGUI Text(Transform parent, string text, TMP_FontAsset font, float size, Color color,
        TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, string name = "Text")
    {
        RectTransform rect = Rect(name, parent);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = size;
        label.color = color;
        label.alignment = align;
        label.text = text;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        return label;
    }

    public TextMeshProUGUI Body(Transform parent, string text, float size = 13f, bool muted = false, bool wrap = false)
    {
        TextMeshProUGUI label = Text(parent, text, T.sans, size, muted ? T.muted : T.text);
        if (wrap) label.textWrappingMode = TextWrappingModes.Normal;
        return label;
    }

    /// <summary>Small uppercase section caption.</summary>
    public TextMeshProUGUI Caption(Transform parent, string text)
    {
        TextMeshProUGUI label = Text(parent, text.ToUpperInvariant(), T.sansSemiBold, 11f, T.muted);
        label.characterSpacing = 6f;
        return label;
    }

    public Image Icon(Transform parent, string iconName, float size, Color color, string name = "Icon")
    {
        RectTransform rect = Rect(name, parent);
        Image image = Img(rect, T.Icon(iconName), color);
        image.preserveAspect = true;
        Fixed(rect.gameObject, size, size);
        return image;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    public static RectOffset Pad(float all) => new RectOffset((int)all, (int)all, (int)all, (int)all);
    public static RectOffset Pad(float horizontal, float vertical) => new RectOffset((int)horizontal, (int)horizontal, (int)vertical, (int)vertical);
    public static RectOffset Pad(float left, float right, float top, float bottom) => new RectOffset((int)left, (int)right, (int)top, (int)bottom);

    public static HorizontalLayoutGroup H(GameObject go, float spacing = 0f, RectOffset padding = null,
        TextAnchor align = TextAnchor.MiddleLeft, bool expandWidth = false, bool expandHeight = false)
    {
        var group = go.AddComponent<HorizontalLayoutGroup>();
        group.spacing = spacing;
        group.padding = padding ?? new RectOffset();
        group.childAlignment = align;
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = expandWidth;
        group.childForceExpandHeight = expandHeight;
        return group;
    }

    public static VerticalLayoutGroup V(GameObject go, float spacing = 0f, RectOffset padding = null,
        TextAnchor align = TextAnchor.UpperLeft, bool expandWidth = true, bool expandHeight = false)
    {
        var group = go.AddComponent<VerticalLayoutGroup>();
        group.spacing = spacing;
        group.padding = padding ?? new RectOffset();
        group.childAlignment = align;
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = expandWidth;
        group.childForceExpandHeight = expandHeight;
        return group;
    }

    static LayoutElement Element(GameObject go) =>
        go.TryGetComponent(out LayoutElement element) ? element : go.AddComponent<LayoutElement>();

    /// <summary>Fixed size in a layout group (-1 leaves that axis to the content).</summary>
    public static LayoutElement Fixed(GameObject go, float width = -1f, float height = -1f)
    {
        LayoutElement element = Element(go);
        if (width >= 0f) element.minWidth = element.preferredWidth = width;
        if (height >= 0f) element.minHeight = element.preferredHeight = height;
        return element;
    }

    public static LayoutElement Flex(GameObject go, float width = 1f, float height = -1f)
    {
        LayoutElement element = Element(go);
        element.flexibleWidth = width;
        if (height >= 0f) element.flexibleHeight = height;
        return element;
    }

    /// <summary>Equal-width cell: no preferred width, so flexible siblings split the row evenly.</summary>
    public static LayoutElement Equal(GameObject go)
    {
        LayoutElement element = Element(go);
        element.minWidth = 0f;
        element.preferredWidth = 0f;
        element.flexibleWidth = 1f;
        return element;
    }

    public static void IgnoreLayout(GameObject go) => Element(go).ignoreLayout = true;

    public static void FitToContent(GameObject go, bool horizontal = false, bool vertical = true)
    {
        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = horizontal ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = vertical ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
    }

    public RectTransform Spacer(Transform parent, float flexible = 1f)
    {
        RectTransform rect = Rect("Spacer", parent);
        Flex(rect.gameObject, flexible);
        return rect;
    }

    public RectTransform Divider(Transform parent, bool vertical = false)
    {
        RectTransform rect = Rect("Divider", parent);
        Img(rect, null, T.lineStrong);
        if (vertical) Fixed(rect.gameObject, 1f, 24f);
        else Fixed(rect.gameObject, -1f, 1f);
        return rect;
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    /// <summary>Label + optional icon button. Text-less buttons are square icon buttons.</summary>
    public ButtonParts Btn(Transform parent, string name, string text, string iconName = null,
        ButtonStyle style = ButtonStyle.Secondary, float height = 34f, float iconSize = 18f,
        float padding = 12f, float labelSize = 13f, float gap = 8f)
    {
        RectTransform rect = Rect(name, parent);
        Color normal = style == ButtonStyle.Primary ? T.accent : style == ButtonStyle.Warn ? T.warn
            : style == ButtonStyle.Ghost ? T.Clear : T.fill;
        Color hover = style == ButtonStyle.Primary ? T.accentHover : style == ButtonStyle.Warn ? Color.Lerp(T.warn, Color.white, 0.2f) : T.fillHover;
        Color pressed = style == ButtonStyle.Primary ? Color.Lerp(T.accent, Color.black, 0.15f)
            : style == ButtonStyle.Warn ? Color.Lerp(T.warn, Color.black, 0.15f) : T.fillPressed;
        Color content = style == ButtonStyle.Primary ? T.accentInk : style == ButtonStyle.Warn ? new Color32(42, 26, 0, 255)
            : style == ButtonStyle.Danger ? T.danger : T.text;

        Image background = Img(rect, T.fillR8, normal, true);
        if (style == ButtonStyle.Secondary || style == ButtonStyle.Danger)
        {
            Color ringColor = style == ButtonStyle.Danger ? new Color(T.danger.r, T.danger.g, T.danger.b, 0.45f) : T.lineStrong;
            Ring(rect, T.ringR8, ringColor);
        }

        var parts = new ButtonParts { button = rect.gameObject.AddComponent<Button>() };
        parts.button.targetGraphic = background;
        parts.button.transition = Selectable.Transition.None;
        rect.gameObject.AddComponent<CanvasGroup>();
        parts.fx = AddFx(rect.gameObject, background, normal, hover, pressed);

        bool iconOnly = string.IsNullOrEmpty(text);
        H(rect.gameObject, gap, Pad(iconOnly ? 0f : padding, 0f), TextAnchor.MiddleCenter);
        Fixed(rect.gameObject, iconOnly ? height : -1f, height);

        if (iconName != null) parts.icon = Icon(rect, iconName, iconSize, content);
        if (!iconOnly)
        {
            parts.label = Text(rect, text, T.sansMedium, labelSize, content, TextAlignmentOptions.Center);
            if (style == ButtonStyle.Primary || style == ButtonStyle.Warn) parts.label.font = T.sansSemiBold;
        }
        return parts;
    }

    SB_ButtonFx AddFx(GameObject go, Graphic target, Color normal, Color hover, Color pressed, Image ring = null)
    {
        var fx = go.AddComponent<SB_ButtonFx>();
        fx.theme = T;
        fx.target = target;
        fx.ring = ring;
        fx.normal = normal;
        fx.hover = hover;
        fx.pressed = pressed;
        fx.selected = T.AccentSoft;
        return fx;
    }

    /// <summary>A selectable list row: transparent until hovered, accent-tinted with an outline when selected.</summary>
    public ButtonParts Row(Transform parent, string name, float height)
    {
        RectTransform rect = Rect(name, parent);
        Image background = Img(rect, T.fillR8, T.Clear, true);
        Image ring = Ring(rect, T.ringR8, T.accent);
        ring.color = new Color(T.accent.r, T.accent.g, T.accent.b, 0f);

        var parts = new ButtonParts { button = rect.gameObject.AddComponent<Button>() };
        parts.button.targetGraphic = background;
        parts.button.transition = Selectable.Transition.None;
        rect.gameObject.AddComponent<CanvasGroup>();
        parts.fx = AddFx(rect.gameObject, background, T.Clear, new Color(1f, 1f, 1f, 0.08f), T.fillPressed, ring);
        H(rect.gameObject, 10f, Pad(10f, 8f, 0f, 0f));
        Fixed(rect.gameObject, -1f, height);
        return parts;
    }

    public ButtonParts ToolRow(Transform parent, string name, string iconName, string label, string key)
    {
        ButtonParts parts = Row(parent, name, 36f);
        parts.icon = Icon(parts.button.transform, iconName, 18f, T.text);
        parts.label = Text(parts.button.transform, label, T.sansMedium, 13f, T.text);
        Flex(parts.label.gameObject);
        if (key != null) Keycap(parts.button.transform, key);
        return parts;
    }

    public TextMeshProUGUI Keycap(Transform parent, string text)
    {
        RectTransform rect = Rect("Keycap", parent);
        Img(rect, T.fillR8, new Color(1f, 1f, 1f, 0.04f));
        Ring(rect, T.ringR8, T.lineStrong);
        H(rect.gameObject, 0f, Pad(6f, 1f), TextAnchor.MiddleCenter);
        LayoutElement size = Element(rect.gameObject);
        size.minWidth = 18f;
        size.minHeight = 20f;
        return Text(rect, text, T.monoMedium, 11f, T.muted, TextAlignmentOptions.Center);
    }

    public (RectTransform root, TextMeshProUGUI label) Chip(Transform parent, string text, ChipKind kind)
    {
        Color background = kind == ChipKind.Warn ? new Color(T.warn.r, T.warn.g, T.warn.b, 0.18f)
            : kind == ChipKind.Ok ? new Color(T.accent.r, T.accent.g, T.accent.b, 0.18f) : new Color(1f, 1f, 1f, 0.1f);
        Color content = kind == ChipKind.Warn ? T.warn : kind == ChipKind.Ok ? T.accent : T.text;

        RectTransform rect = Rect("Chip", parent);
        Img(rect, T.pill, background);
        H(rect.gameObject, 0f, Pad(8f, 0f), TextAnchor.MiddleCenter);
        Fixed(rect.gameObject, -1f, 20f);
        return (rect, Text(rect, text, T.sansSemiBold, 11f, content, TextAlignmentOptions.Center));
    }

    // ── Toggles ───────────────────────────────────────────────────────────────

    public Toggle Switch(Transform parent, string name)
    {
        RectTransform rect = Rect(name, parent);
        Fixed(rect.gameObject, 38f, 22f);
        Image track = Img(rect, T.pill, T.field, true);
        Image ring = Ring(rect, T.ringPill, T.lineStrong);

        RectTransform knob = Rect("Knob", rect);
        Place(knob, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(2f, 0f), new Vector2(16f, 16f));
        Image knobImage = Img(knob, T.circle, new Color32(197, 203, 209, 255));

        rect.gameObject.AddComponent<CanvasGroup>();
        Toggle toggle = AddToggle(rect.gameObject, track);
        var visual = rect.gameObject.AddComponent<SB_Toggle>();
        visual.theme = T;
        visual.track = track;
        visual.trackRing = ring;
        visual.knob = knob;
        visual.knobImage = knobImage;
        return toggle;
    }

    public Toggle Checkbox(Transform parent, string name)
    {
        RectTransform rect = Rect(name, parent);
        Fixed(rect.gameObject, 22f, 22f);
        Image track = Img(rect, T.fillR8, T.field, true);
        Image ring = Ring(rect, T.ringR8, T.lineStrong);
        Image tick = Icon(rect, "check", 14f, new Color(T.accentInk.r, T.accentInk.g, T.accentInk.b, 0f), "Tick");
        Place(tick.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(14f, 14f));

        rect.gameObject.AddComponent<CanvasGroup>();
        Toggle toggle = AddToggle(rect.gameObject, track);
        var visual = rect.gameObject.AddComponent<SB_Toggle>();
        visual.theme = T;
        visual.track = track;
        visual.trackRing = ring;
        visual.tick = tick;
        return toggle;
    }

    static Toggle AddToggle(GameObject go, Graphic target)
    {
        var toggle = go.AddComponent<Toggle>();
        toggle.targetGraphic = target;
        toggle.transition = Selectable.Transition.None;
        toggle.isOn = false;
        return toggle;
    }

    // ── Segmented control and tabs ────────────────────────────────────────────

    /// <param name="experimental">Options (by index) that get a warning icon next to their label.</param>
    public SB_Segmented Segmented(Transform parent, string name, string[] labels, int[] values, bool underline = false,
        float height = 32f, bool[] experimental = null)
    {
        RectTransform root = Rect(name, parent);
        Fixed(root.gameObject, -1f, height);
        if (!underline)
        {
            Img(root, T.fillR8, T.field, true);
            Ring(root, T.ringR8, T.line);
        }

        RectTransform slots = Rect("Slots", root);
        Stretch(slots, underline ? 0f : 2f, underline ? 0f : 2f, underline ? 0f : 2f, underline ? 0f : 2f);
        H(slots.gameObject, underline ? 0f : 2f, null, TextAnchor.MiddleCenter, true, true);

        RectTransform indicator = Rect("Indicator", slots);
        IgnoreLayout(indicator.gameObject);
        if (underline)
        {
            Img(indicator, null, T.accent);
            indicator.pivot = new Vector2(0.5f, 0f);
            indicator.sizeDelta = new Vector2(0f, 2f);
        }
        else
        {
            Img(indicator, T.fillR8, T.AccentSoft);
            Ring(indicator, T.ringR8, new Color(T.accent.r, T.accent.g, T.accent.b, 0.6f));
            indicator.offsetMin = indicator.offsetMax = Vector2.zero;
        }

        var segmented = root.gameObject.AddComponent<SB_Segmented>();
        segmented.theme = T;
        segmented.indicator = indicator;
        segmented.indicatorY = underline ? new Vector2(0f, 0f) : new Vector2(0f, 1f);
        segmented.values = values;
        segmented.buttons = new Button[labels.Length];
        segmented.labels = new TMP_Text[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            RectTransform cell = Rect(labels[i], slots);
            Equal(cell.gameObject);
            Image hit = Img(cell, null, T.Clear, true);
            var button = cell.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            TextMeshProUGUI label = Text(cell, labels[i], T.sansMedium, underline ? 13f : 12f, T.muted, TextAlignmentOptions.Center);
            if (experimental != null && experimental[i])
            {
                H(cell.gameObject, 6f, null, TextAnchor.MiddleCenter);
                Icon(cell, "warn", 13f, T.warn);
            }
            else
            {
                Stretch(label.rectTransform);
            }
            segmented.buttons[i] = button;
            segmented.labels[i] = label;
        }
        return segmented;
    }

    // ── Inputs ────────────────────────────────────────────────────────────────

    /// <summary>A text or number field. `suffix` is a unit such as "m"; `ghost` drops the box (used for the store name).</summary>
    public TMP_InputField Input(Transform parent, string name, TMP_InputField.ContentType type, float height = 32f,
        string suffix = null, string placeholder = "", bool ghost = false, TMP_FontAsset font = null, float size = 13f)
    {
        font = font != null ? font : (type == TMP_InputField.ContentType.Standard ? T.sansMedium : T.monoMedium);
        RectTransform root = Rect(name, parent);
        Fixed(root.gameObject, -1f, height);
        Image background = Img(root, T.fillR8, ghost ? T.Clear : T.field, true);
        Image ring = ghost ? null : Ring(root, T.ringR8, T.lineStrong);

        RectTransform area = Rect("Text Area", root);
        Stretch(area, ghost ? 8f : 10f, 0f, suffix != null ? 30f : ghost ? 8f : 10f, 0f);
        area.gameObject.AddComponent<RectMask2D>();

        TextMeshProUGUI hint = Text(area, placeholder, font, size, new Color(T.muted.r, T.muted.g, T.muted.b, 0.7f), TextAlignmentOptions.MidlineLeft, "Placeholder");
        hint.fontStyle = FontStyles.Italic;
        Stretch(hint.rectTransform);
        TextMeshProUGUI text = Text(area, "", font, size, T.text, TextAlignmentOptions.MidlineLeft, "Text");
        Stretch(text.rectTransform);

        var input = root.gameObject.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = hint;
        input.fontAsset = font;
        input.pointSize = size;
        input.targetGraphic = background;
        input.transition = Selectable.Transition.None;
        input.contentType = type;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.richText = false;
        input.onFocusSelectAll = true;
        input.customCaretColor = true;
        input.caretColor = T.accent;
        input.caretWidth = 2;
        input.selectionColor = new Color(T.accent.r, T.accent.g, T.accent.b, 0.35f);

        if (ring != null)
        {
            var focus = root.gameObject.AddComponent<SB_InputFocus>();
            focus.theme = T;
            focus.input = input;
            focus.ring = ring;
        }
        else
        {
            AddFx(root.gameObject, background, T.Clear, new Color(1f, 1f, 1f, 0.08f), T.fillPressed).pressScale = false;
        }

        if (suffix != null)
        {
            TextMeshProUGUI unit = Text(root, suffix, T.mono, 12f, T.muted, TextAlignmentOptions.MidlineRight, "Suffix");
            Stretch(unit.rectTransform, 0f, 0f, 10f, 0f);
        }
        return input;
    }

    /// <summary>Caption above a control; returns the column so the control can be added to it.</summary>
    public RectTransform Labeled(Transform parent, string label, string name = "Field", bool experimental = false)
    {
        RectTransform column = Rect(name, parent);
        V(column.gameObject, 5f);
        if (!experimental)
        {
            Text(column, label, T.sans, 12f, T.muted);
            return column;
        }

        RectTransform caption = Rect("Caption", column);
        H(caption.gameObject, 6f);
        Text(caption, label, T.sans, 12f, T.muted);
        Icon(caption, "warn", 13f, T.warn);
        return column;
    }

    // ── Scrolling ─────────────────────────────────────────────────────────────

    /// <summary>Mouse-wheel step per notch; trackpads send many small events, so keep it low.</summary>
    public const float ScrollSensitivity = 4f;

    /// <summary>Vertical scrolling with a slim scrollbar on the right of `host` that hides when everything fits.</summary>
    public void StyleScroll(ScrollRect scroll, RectTransform host)
    {
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = ScrollSensitivity;

        RectTransform track = Rect("Scrollbar", host);
        track.anchorMin = new Vector2(1f, 0f);
        track.anchorMax = new Vector2(1f, 1f);
        track.pivot = new Vector2(1f, 0.5f);
        track.sizeDelta = new Vector2(8f, -12f);
        track.anchoredPosition = new Vector2(-4f, 0f);
        Img(track, T.pillSmall, new Color(1f, 1f, 1f, 0.05f), true);

        RectTransform area = Rect("Sliding Area", track);
        Stretch(area);
        RectTransform handle = Rect("Handle", area);
        handle.anchorMin = Vector2.zero;
        handle.anchorMax = Vector2.one;
        handle.offsetMin = handle.offsetMax = Vector2.zero;
        Image handleImage = Img(handle, T.pillSmall, new Color(1f, 1f, 1f, 0.5f), true);

        var bar = track.gameObject.AddComponent<Scrollbar>();
        bar.direction = Scrollbar.Direction.BottomToTop;
        bar.handleRect = handle;
        bar.targetGraphic = handleImage;
        bar.transition = Selectable.Transition.ColorTint;
        bar.colors = new ColorBlock
        {
            normalColor = new Color(1f, 1f, 1f, 0.6f), highlightedColor = Color.white, pressedColor = Color.white,
            selectedColor = new Color(1f, 1f, 1f, 0.6f), disabledColor = new Color(1f, 1f, 1f, 0.2f),
            colorMultiplier = 1f, fadeDuration = 0.1f
        };
        scroll.verticalScrollbar = bar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
    }

    public TMP_Dropdown Dropdown(Transform parent, string name, float height = 32f)
    {
        RectTransform root = Rect(name, parent);
        Fixed(root.gameObject, -1f, height);
        Image background = Img(root, T.fillR8, T.field, true);
        Ring(root, T.ringR8, T.lineStrong);

        TextMeshProUGUI caption = Text(root, "", T.sansMedium, 13f, T.text, TextAlignmentOptions.MidlineLeft, "Label");
        Stretch(caption.rectTransform, 10f, 0f, 30f, 0f);
        Image arrow = Icon(root, "chevron", 14f, T.muted, "Arrow");
        Place(arrow.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-10f, 0f), new Vector2(14f, 14f));

        RectTransform template = Rect("Template", root);
        Place(template, new Vector2(0f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(0f, 190f));
        template.anchorMax = new Vector2(1f, 0f);
        Img(template, T.fillR8, T.panelSolid, true);
        Ring(template, T.ringR8, T.lineStrong);
        var scroll = template.gameObject.AddComponent<ScrollRect>();

        RectTransform viewport = Rect("Viewport", template);
        Stretch(viewport, 0f, 4f, 0f, 4f);
        viewport.gameObject.AddComponent<RectMask2D>();
        RectTransform content = Rect("Content", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0f, 30f);

        RectTransform item = Rect("Item", content);
        item.anchorMin = new Vector2(0f, 0.5f);
        item.anchorMax = new Vector2(1f, 0.5f);
        item.sizeDelta = new Vector2(0f, 30f);
        RectTransform itemBackground = Rect("Item Background", item);
        Stretch(itemBackground, 4f, 0f, 4f, 0f);
        // White base: the toggle's color tint supplies the (transparent until hovered) look.
        Image itemImage = Img(itemBackground, T.fillR8, Color.white, true);
        Image check = Icon(item, "check", 14f, T.accent, "Item Checkmark");
        Place(check.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(14f, 14f));
        TextMeshProUGUI itemLabel = Text(item, "Option", T.sansMedium, 13f, T.text, TextAlignmentOptions.MidlineLeft, "Item Label");
        Stretch(itemLabel.rectTransform, 32f, 0f, 10f, 0f);

        var itemToggle = item.gameObject.AddComponent<Toggle>();
        itemToggle.targetGraphic = itemImage;
        itemToggle.graphic = check;
        itemToggle.isOn = true;
        itemToggle.transition = Selectable.Transition.ColorTint;
        itemToggle.colors = new ColorBlock
        {
            normalColor = new Color(1f, 1f, 1f, 0f), highlightedColor = new Color(1f, 1f, 1f, 0.1f),
            pressedColor = new Color(1f, 1f, 1f, 0.18f), selectedColor = new Color(1f, 1f, 1f, 0f),
            disabledColor = new Color(1f, 1f, 1f, 0f), colorMultiplier = 1f, fadeDuration = 0.08f
        };

        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = ScrollSensitivity;

        var dropdown = root.gameObject.AddComponent<TMP_Dropdown>();
        dropdown.targetGraphic = background;
        dropdown.transition = Selectable.Transition.None;
        dropdown.template = template;
        dropdown.captionText = caption;
        dropdown.itemText = itemLabel;
        dropdown.options.Clear();
        template.gameObject.SetActive(false);
        return dropdown;
    }

    // ── Sections ──────────────────────────────────────────────────────────────

    /// <summary>A padded block of an inspector with a divider on top.</summary>
    public RectTransform Section(Transform parent, string title = null, bool first = false, string name = "Section")
    {
        RectTransform rect = Rect(name, parent);
        V(rect.gameObject, 10f, Pad(14f, 14f, 12f, 12f));
        if (!first)
        {
            RectTransform line = Rect("Divider", rect);
            IgnoreLayout(line.gameObject);
            line.anchorMin = new Vector2(0f, 1f);
            line.anchorMax = new Vector2(1f, 1f);
            line.pivot = new Vector2(0.5f, 1f);
            line.sizeDelta = new Vector2(0f, 1f);
            Img(line, null, T.line);
        }
        if (title != null) Caption(rect, title);
        return rect;
    }

    /// <summary>Title + caption on the left, a switch on the right.</summary>
    public Toggle SwitchRow(Transform parent, string title, string caption)
    {
        RectTransform row = Rect("Switch Row", parent);
        H(row.gameObject, 12f, null, TextAnchor.MiddleLeft);
        Fixed(row.gameObject, -1f, 40f);

        RectTransform text = Rect("Text", row);
        V(text.gameObject, 2f, null, TextAnchor.MiddleLeft);
        Flex(text.gameObject);
        Text(text, title, T.sansMedium, 13f, T.text);
        if (caption != null) Text(text, caption, T.sans, 12f, T.muted);
        return Switch(row, "Switch");
    }

    /// <summary>Equal-width columns in one row.</summary>
    public RectTransform Columns(Transform parent, float spacing = 10f, string name = "Columns")
    {
        RectTransform row = Rect(name, parent);
        H(row.gameObject, spacing, null, TextAnchor.UpperLeft, true, false);
        return row;
    }

    /// <summary>A labeled number field that fills one column of a Columns row.</summary>
    public TMP_InputField NumberField(Transform columns, string label, string suffix, bool integer = false)
    {
        RectTransform column = Labeled(columns, label, label + " Field");
        Equal(column.gameObject);
        return Input(column, "Input", integer ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.DecimalNumber, 32f, suffix);
    }

    public SB_Panel MakePanel(GameObject go, Vector2 slideFrom, float scaleFrom = 1f, RectTransform animated = null)
    {
        if (!go.TryGetComponent(out CanvasGroup _)) go.AddComponent<CanvasGroup>();
        var panel = go.AddComponent<SB_Panel>();
        panel.slideFrom = slideFrom;
        panel.scaleFrom = scaleFrom;
        panel.animated = animated;
        return panel;
    }
}
