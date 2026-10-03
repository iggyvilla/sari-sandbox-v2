using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using static StoreBuilderUIKit;

/// <summary>
/// Generates the Store Builder UI under the scene's "UI Canvas" and wires it to SB_UIHandler.
/// Rerunnable: it replaces the generated root each time. The previous UI is parked under a disabled "Legacy UI".
/// </summary>
public static class StoreBuilderUIBuilder
{
    [MenuItem("Sari/Store Builder UI/3. Rebuild UI")]
    public static void Rebuild()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("Stop play mode first: changes made while playing are lost.");
            return;
        }

        SB_Theme theme = StoreBuilderUIAssets.CreateTheme();
        var handler = Object.FindFirstObjectByType<SB_UIHandler>();
        GameObject canvas = GameObject.Find("UI Canvas");
        if (handler == null || canvas == null)
        {
            Debug.LogError("Open the StoreBuilder scene first (needs 'UI Canvas' and an SB_UIHandler).");
            return;
        }

        new Generator(theme, canvas, handler).Build();
        EditorSceneManager.MarkSceneDirty(canvas.scene);
        EditorSceneManager.SaveScene(canvas.scene);
        Debug.Log("Store Builder UI rebuilt.");
    }

    sealed class Generator
    {
        const string RootName = "Store Builder UI";
        const string LegacyName = "Legacy UI";
        const float InspectorWidth = 316f;

        readonly StoreBuilderUIKit k;
        readonly SB_Theme T;
        readonly GameObject _canvas;
        readonly SB_UIHandler h;
        readonly List<Button> _closeButtons = new();

        public Generator(SB_Theme theme, GameObject canvas, SB_UIHandler handler)
        {
            k = new StoreBuilderUIKit(theme);
            T = theme;
            _canvas = canvas;
            h = handler;
        }

        public void Build()
        {
            // The old UI kept its own copy of this component on the handler object; the new one lives in the shelf inspector.
            foreach (ShelfEditGroupHandler old in h.GetComponents<ShelfEditGroupHandler>())
                Object.DestroyImmediate(old);

            ParkLegacyUI();
            ConfigureCanvas();

            RectTransform root = Rect(RootName, _canvas.transform);
            Stretch(root);

            BuildStoreInspector(root);
            BuildShelfInspector(root);
            BuildSubShelfInspector(root);
            BuildPropInspector(root);
            BuildAisleInspector(root);
            BuildPalette(root);
            BuildTopBar(root);
            BuildStatusBar(root);
            BuildBanner(root);
            BuildStoresDialog(root);
            BuildSettingsDialog(root);

            // Arrow keys orbit and zoom the camera, so they must not move UI focus (a focused input field eats hotkeys).
            foreach (Selectable selectable in root.GetComponentsInChildren<Selectable>(true))
                selectable.navigation = new Navigation { mode = Navigation.Mode.None };

            h.inspectorCloseButtons = _closeButtons.ToArray();
            if (h.interactionController == null)
                h.interactionController = Object.FindFirstObjectByType<SB_InteractionController>();
            EditorUtility.SetDirty(h);
        }

        // ── Scene plumbing ────────────────────────────────────────────────────

        void ParkLegacyUI()
        {
            Transform canvas = _canvas.transform;
            Transform generated = canvas.Find(RootName);
            if (generated != null) Object.DestroyImmediate(generated.gameObject);

            Transform legacy = canvas.Find(LegacyName);
            var children = new List<Transform>();
            foreach (Transform child in canvas)
                if (child != legacy) children.Add(child);
            if (children.Count == 0) return;

            if (legacy == null)
            {
                RectTransform rect = Rect(LegacyName, canvas);
                Stretch(rect);
                rect.gameObject.SetActive(false);
                legacy = rect;
            }
            foreach (Transform child in children) child.SetParent(legacy, false);
        }

        void ConfigureCanvas()
        {
            var scaler = _canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
        }

        // ── Top bar, palette, banner, status bar ──────────────────────────────

        void BuildTopBar(RectTransform root)
        {
            RectTransform bar = Rect("Top Bar", root);
            Place(bar, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 52f));
            bar.anchorMin = new Vector2(0f, 1f);
            bar.anchorMax = new Vector2(1f, 1f);
            Img(bar, null, T.panel, true);
            H(bar.gameObject, 10f, Pad(12f, 0f), TextAnchor.MiddleLeft);

            k.Text(bar, "Sari Sandbox", T.sansSemiBold, 14f, T.text);
            k.Text(bar, "Builder", T.sans, 12f, T.muted);
            k.Divider(bar, vertical: true);

            h.storeNameInput = k.Input(bar, "Store Name", TMP_InputField.ContentType.Standard, 34f, null, "Store name", ghost: true, font: T.sansSemiBold, size: 14f);
            Fixed(h.storeNameInput.gameObject, 180f, 34f);
            k.Icon(bar, "edit", 14f, T.muted);
            h.unsavedChip = k.Chip(bar, "Unsaved changes", ChipKind.Warn).root.gameObject;
            h.unsavedChip.SetActive(false);

            k.Spacer(bar);
            h.fillShelvesButton = k.Btn(bar, "Fill Shelves", "Fill shelves", "fill").button;
            k.Divider(bar, vertical: true);
            h.quickSaveButton = k.Btn(bar, "Save", null, "save").button;
            h.storesButton = k.Btn(bar, "Stores", "Stores", "folder").button;
            h.settingsButton = k.Btn(bar, "Settings", null, "sliders").button;
            h.playButton = k.Btn(bar, "Play", "Play", "play", ButtonStyle.Primary, 36f, 16f, 16f).button;
        }

        void BuildPalette(RectTransform root)
        {
            RectTransform palette = Rect("Tool Palette", root);
            Place(palette, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(12f, -64f), new Vector2(204f, 0f));
            Img(palette, T.fillR12, T.panel, true);
            StoreBuilderUIKit.Ring(palette, T.ringR12, T.line);
            V(palette.gameObject, 0f, Pad(8f));
            FitToContent(palette.gameObject);

            var tools = new List<SB_UIHandler.ToolButton>();
            void Tool(BuilderTool tool, string icon, string key)
            {
                ButtonParts row = k.ToolRow(palette, SB_InteractionController.ToolLabel(tool), icon, SB_InteractionController.ToolLabel(tool), key);
                tools.Add(new SB_UIHandler.ToolButton { tool = tool, button = row.button, fx = row.fx });
            }

            Tool(BuilderTool.Select, "select", "V");
            PaletteCaption(palette, "Fixtures");
            Tool(BuilderTool.Shelf, "shelf", "S");
            Tool(BuilderTool.Fridge, "fridge", "F");
            Tool(BuilderTool.SelfCheckout, "checkout", "C");
            PaletteCaption(palette, "Markers");
            Tool(BuilderTool.AisleMarker, "aisle", "A");
            Tool(BuilderTool.AgentSpawn, "agent", "G");
            Tool(BuilderTool.EmergencyExit, "exit", "E");
            h.toolButtons = tools.ToArray();
        }

        void PaletteCaption(Transform palette, string text)
        {
            RectTransform wrapper = Rect(text + " Caption", palette);
            V(wrapper.gameObject, 0f, Pad(10f, 10f, 12f, 6f));
            k.Caption(wrapper, text);
        }

        void BuildBanner(RectTransform root)
        {
            RectTransform banner = Rect("Placing Banner", root);
            Place(banner, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -64f), Vector2.zero);
            Img(banner, T.fillR12, T.panel);
            StoreBuilderUIKit.Ring(banner, T.ringR12, new Color(T.accent.r, T.accent.g, T.accent.b, 0.6f));
            H(banner.gameObject, 14f, Pad(16f, 9f), TextAnchor.MiddleCenter);
            FitToContent(banner.gameObject, horizontal: true, vertical: true);

            RectTransform dot = Rect("Dot", banner);
            Img(dot, T.circle, T.accent);
            Fixed(dot.gameObject, 8f, 8f);
            h.placingTitleText = k.Text(banner, "", T.sansSemiBold, 13f, T.text);
            h.placingHintText = k.Text(banner, "", T.sans, 13f, T.text);
            h.placingRotateHint = KeyHint(banner, "R", "Rotate").row;
            h.placingCancelText = KeyHint(banner, "Esc", "Cancel").caption;

            h.placingBanner = k.MakePanel(banner.gameObject, new Vector2(0f, 10f));
            banner.gameObject.SetActive(false);
        }

        // A keycap followed by its caption; returns both so the hint can be hidden or reworded.
        (GameObject row, TMP_Text caption) KeyHint(Transform parent, string key, string caption)
        {
            RectTransform row = Rect(key + " Hint", parent);
            H(row.gameObject, 6f);
            k.Keycap(row, key);
            return (row.gameObject, k.Text(row, caption, T.sans, 12f, T.muted));
        }

        void BuildStatusBar(RectTransform root)
        {
            RectTransform bar = Rect("Status Bar", root);
            Place(bar, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 34f));
            bar.anchorMin = new Vector2(0f, 0f);
            bar.anchorMax = new Vector2(1f, 0f);
            Img(bar, null, T.panel, true);
            H(bar.gameObject, 18f, Pad(14f, 0f), TextAnchor.MiddleLeft);

            RectTransform orbit = Rect("Orbit", bar);
            H(orbit.gameObject, 6f);
            k.Keycap(orbit, "←");
            k.Keycap(orbit, "→");
            k.Text(orbit, "Orbit", T.sans, 12f, T.muted);

            RectTransform zoom = Rect("Zoom", bar);
            H(zoom.gameObject, 6f);
            k.Keycap(zoom, "↑");
            k.Keycap(zoom, "↓");
            k.Text(zoom, "Zoom", T.sans, 12f, T.muted);

            h.statusHintText = k.Text(bar, "", T.sans, 12f, T.text);
            Flex(h.statusHintText.gameObject);
            h.statusInfoText = k.Text(bar, "", T.sans, 12f, T.muted, TextAlignmentOptions.MidlineRight);
        }

        // ── Inspectors ────────────────────────────────────────────────────────

        (RectTransform panel, RectTransform content) Inspector(RectTransform root, string name, out SB_Panel animated, bool open)
        {
            RectTransform rect = Rect(name, root);
            Place(rect, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -64f), new Vector2(InspectorWidth, 200f));
            Img(rect, T.fillR12, T.panel, true);
            StoreBuilderUIKit.Ring(rect, T.ringR12, T.line);

            RectTransform viewport = Rect("Viewport", rect);
            Stretch(viewport, 0f, 1f, 0f, 1f);
            viewport.gameObject.AddComponent<RectMask2D>();
            RectTransform content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;
            V(content.gameObject);
            FitToContent(content.gameObject);

            var scroll = rect.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            k.StyleScroll(scroll, rect);
            rect.gameObject.AddComponent<SB_FitHeight>().content = content;

            animated = k.MakePanel(rect.gameObject, new Vector2(16f, 0f));
            rect.gameObject.SetActive(open);
            return (rect, content);
        }

        void InspectorHeader(Transform content, string icon, string title, string subtitle, out TMP_Text titleText, out TMP_Text subtitleText)
        {
            RectTransform header = Rect("Header", content);
            H(header.gameObject, 10f, Pad(14f, 12f, 12f, 12f), TextAnchor.MiddleLeft);

            RectTransform tile = Rect("Icon Tile", header);
            Img(tile, T.fillR8, T.AccentSoft);
            H(tile.gameObject, 0f, null, TextAnchor.MiddleCenter);
            Fixed(tile.gameObject, 32f, 32f);
            k.Icon(tile, icon, 18f, T.accent);

            RectTransform text = Rect("Text", header);
            V(text.gameObject, 2f, null, TextAnchor.MiddleLeft);
            Flex(text.gameObject);
            titleText = k.Text(text, title, T.sansSemiBold, 16f, T.text);
            subtitleText = k.Text(text, subtitle, T.sans, 12f, T.muted);

            _closeButtons.Add(k.Btn(header, "Close", null, "close", ButtonStyle.Ghost).button);
        }

        SB_ActionBar ActionBar(Transform content)
        {
            RectTransform row = Rect("Actions", content);
            H(row.gameObject, 6f, Pad(14f, 14f, 0f, 12f), TextAnchor.MiddleLeft, true);
            var bar = row.gameObject.AddComponent<SB_ActionBar>();

            Button Action(string label, string icon, ButtonStyle style)
            {
                ButtonParts parts = k.Btn(row, label, label, icon, style, 30f, 15f, 6f, 12f, 4f);
                Equal(parts.button.gameObject);
                return parts.button;
            }

            bar.move = Action("Move", "move", ButtonStyle.Secondary);
            bar.rotate = Action("Rotate", "rotate", ButtonStyle.Secondary);
            bar.duplicate = Action("Copy", "copy", ButtonStyle.Secondary);
            bar.delete = Action("Delete", "trash", ButtonStyle.Danger);
            return bar;
        }

        void BuildStoreInspector(RectTransform root)
        {
            (RectTransform panel, RectTransform content) = Inspector(root, "Store Inspector", out h.storeInspector, true);

            RectTransform intro = k.Section(content, null, true);
            k.Text(intro, "Store", T.sansSemiBold, 16f, T.text);
            k.Body(intro, "Nothing selected. Click a shelf, fixture or marker to edit it.", 12f, true, true);

            RectTransform size = k.Section(content, "Size");
            RectTransform row = k.Columns(size);
            h.storeWidthInput = k.NumberField(row, "Width", "m");
            h.storeDepthInput = k.NumberField(row, "Depth", "m");
            h.wallHeightInput = k.NumberField(row, "Wall height", "m");
            k.Body(size, "Press Enter to apply. Walls grow if the exit door needs more room.", 12f, true, true);

            RectTransform street = k.Section(content, "Street & entrance");
            h.streetWallSegment = Choice(street, "Street wall", new[] { "Front", "Back", "Left", "Right" },
                new[] { (int)WallSide.Front, (int)WallSide.Back, (int)WallSide.Left, (int)WallSide.Right });
            h.exitDoorTypeSegment = Choice(street, "Exit door", new[] { "Door G", "Door H", "Door I" },
                new[] { (int)ExitDoorType.DoorG, (int)ExitDoorType.DoorH, (int)ExitDoorType.DoorI });
            h.exitDoorPositionSegment = Choice(street, "Door position", new[] { "Left", "Middle", "Right" },
                new[] { (int)ExitDoorPosition.Left, (int)ExitDoorPosition.Middle, (int)ExitDoorPosition.Right });

            RectTransform ready = k.Section(content, "Ready to play");
            h.readyDoorRow = StatusRow(ready, "Entrance door");
            h.readyFixturesRow = StatusRow(ready, "Shelves");
            RectTransform agentRow = Rect("Agent Row", ready);
            H(agentRow.gameObject, 8f, null, TextAnchor.MiddleLeft);
            h.readyAgentRow = StatusRow(agentRow, "Agent spawn", flexible: true);
            h.placeAgentSpawnButton = k.Btn(agentRow, "Place", "Place", null, ButtonStyle.Secondary, 28f, 18f, 10f, 12f).button;
        }

        SB_StatusRow StatusRow(Transform parent, string text, bool flexible = false)
        {
            RectTransform row = Rect("Status Row", parent);
            H(row.gameObject, 8f, null, TextAnchor.MiddleLeft);
            if (flexible) Flex(row.gameObject);
            var status = row.gameObject.AddComponent<SB_StatusRow>();
            status.theme = T;
            status.icon = k.Icon(row, "check", 16f, T.accent);
            status.label = k.Body(row, text);
            return status;
        }

        // `experimental` flags options (by index) and `experimentalField` the whole field with a warning icon.
        SB_Segmented Choice(Transform parent, string label, string[] labels, int[] values,
            bool[] experimental = null, bool experimentalField = false)
        {
            RectTransform column = k.Labeled(parent, label, "Field", experimentalField);
            return k.Segmented(column, label + " Choice", labels, values, experimental: experimental);
        }

        void BuildShelfInspector(RectTransform root)
        {
            (RectTransform panel, RectTransform content) = Inspector(root, "Shelf Inspector", out h.shelfInspector, false);
            var g = panel.gameObject.AddComponent<ShelfEditGroupHandler>();
            h.shelfEditGroupHandler = g;

            InspectorHeader(content, "shelf", "Shelf", "Shelf", out h.shelfTitleText, out h.shelfSubtitleText);
            h.shelfActions = ActionBar(content);
            h.shelfTabs = k.Segmented(content, "Tabs", new[] { "Layout", "Items" }, new[] { 0, 1 }, underline: true, height: 36f);
            k.Divider(content);

            // Layout tab
            RectTransform layout = Rect("Layout Tab", content);
            V(layout.gameObject);
            h.shelfLayoutTab = layout.gameObject;

            RectTransform size = k.Section(layout, "Size", true);
            RectTransform row1 = k.Columns(size);
            g.shelfWidth = k.NumberField(row1, "Width", "m");
            g.shelfLevels = k.NumberField(row1, "Levels", null, true);
            RectTransform row2 = k.Columns(size);
            g.distanceBetweenLevels = k.NumberField(row2, "Level gap", "m");
            g.bootHeight = k.NumberField(row2, "Boot height", "m");
            RectTransform row3 = k.Columns(size);
            g.roofHeight = k.NumberField(row3, "Roof height", "m");
            Equal(k.Spacer(row3).gameObject);

            RectTransform orientation = k.Section(layout, "Orientation");
            g.rotationY = k.Segmented(orientation, "Rotation", new[] { "0°", "90°", "180°", "270°" }, new[] { 0, 1, 2, 3 });

            RectTransform faces = k.Section(layout, "Faces");
            BuildFacesGrid(faces, g);

            RectTransform fridge = k.Section(layout, "Fridge");
            g.spawnHingeDoors = k.SwitchRow(fridge, "Hinged glass doors", null);
            RectTransform doorStyle = k.Labeled(fridge, "Door style", "Door Style");
            g.fridgeDoorStyle = k.Segmented(doorStyle, "Door Style Choice", new[] { "Single", "Double" },
                new[] { (int)FridgeDoorStyle.Single, (int)FridgeDoorStyle.Double });
            g.fridgeSection = doorStyle.gameObject;

            // Items tab
            RectTransform items = Rect("Items Tab", content);
            V(items.gameObject);
            h.shelfItemsTab = items.gameObject;
            items.gameObject.SetActive(false);

            RectTransform stock = k.Section(items, null, true);
            g.spawnItems = k.SwitchRow(stock, "Stock this shelf", "Included when you press Fill shelves");
            g.spawnPriceTags = k.SwitchRow(stock, "Price tags", "Show prices on the shelf edge");
            h.priceTagToggle = g.spawnPriceTags;

            RectTransform source = k.Section(items);
            g.itemSpawnOption = k.Dropdown(k.Labeled(source, "Item source"), "Item Source");

            RectTransform category = k.Section(items, "Category for the whole shelf");
            RectTransform pick = Rect("Category Row", category);
            H(pick.gameObject, 8f, null, TextAnchor.MiddleLeft);
            h.shelfCategoryDropdown = k.Dropdown(pick, "Shelf Category");
            Flex(h.shelfCategoryDropdown.gameObject);
            h.applyShelfCategoryButton = k.Btn(pick, "Apply", "Apply", null, ButtonStyle.Secondary, 32f).button;
            RectTransform warning = Rect("Warning", category);
            H(warning.gameObject, 6f);
            k.Icon(warning, "warn", 14f, T.warn);
            k.Text(warning, "Replaces every level's category.", T.sans, 12f, T.muted);
        }

        // Rows are Front/Back/Left/Right, columns Shelves/Wall/Roof.
        void BuildFacesGrid(Transform parent, ShelfEditGroupHandler g)
        {
            RectTransform grid = Rect("Faces Grid", parent);
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(72f, 26f);
            layout.spacing = new Vector2(0f, 6f);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = 4;

            Cell(grid);
            foreach (string header in new[] { "Shelves", "Wall", "Roof" })
            {
                TMP_Text caption = k.Text(Cell(grid), header.ToUpperInvariant(), T.sansSemiBold, 11f, T.muted, TextAlignmentOptions.Center);
                Stretch(caption.rectTransform);
            }

            g.spawnFrontShelf = Face(grid, "Front", out g.spawnFrontShelfWall, out g.spawnFShelfRoof);
            g.spawnBackShelf = Face(grid, "Back", out g.spawnBackShelfWall, out g.spawnBShelfRoof);
            g.spawnLShelf = Face(grid, "Left", out g.spawnLShelfWall, out g.spawnLShelfRoof);
            g.spawnRShelf = Face(grid, "Right", out g.spawnRShelfWall, out g.spawnRShelfRoof);
        }

        Toggle Face(Transform grid, string name, out Toggle wall, out Toggle roof)
        {
            TMP_Text label = k.Body(Cell(grid), name);
            Stretch(label.rectTransform);
            Toggle shelves = CellCheckbox(grid, name + " Shelves");
            wall = CellCheckbox(grid, name + " Wall");
            roof = CellCheckbox(grid, name + " Roof");
            return shelves;
        }

        static RectTransform Cell(Transform grid) => Rect("Cell", grid);

        Toggle CellCheckbox(Transform grid, string name)
        {
            Toggle checkbox = k.Checkbox(Cell(grid), name);
            Place((RectTransform)checkbox.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 22f));
            return checkbox;
        }

        void BuildSubShelfInspector(RectTransform root)
        {
            (RectTransform panel, RectTransform content) = Inspector(root, "Level Category Inspector", out h.subShelfInspector, false);
            InspectorHeader(content, "shelf", "Level category", "Shelf", out _, out h.subShelfSubtitleText);

            RectTransform section = k.Section(content);
            h.itemCategoryDropdown = k.Dropdown(k.Labeled(section, "Items on this level"), "Level Category");
            k.Body(section, "Click the level again to deselect it.", 12f, true, true);
        }

        void BuildPropInspector(RectTransform root)
        {
            (RectTransform panel, RectTransform content) = Inspector(root, "Prop Inspector", out h.propInspector, false);
            InspectorHeader(content, "select", "Object", "Selected", out h.propTitleText, out _);
            h.propActions = ActionBar(content);
        }

        void BuildAisleInspector(RectTransform root)
        {
            (RectTransform panel, RectTransform content) = Inspector(root, "Aisle Marker Inspector", out h.aisleInspector, false);
            InspectorHeader(content, "aisle", "Aisle marker", "Edits apply live", out _, out _);
            h.aisleActions = ActionBar(content);

            RectTransform text = k.Section(content, "Sign text");
            h.aisleCategory1Input = TextField(text, "Category 1");
            h.aisleCategory2Input = TextField(text, "Category 2");
            h.aisleCategory3Input = TextField(text, "Category 3");

            RectTransform placement = k.Section(content, "Placement");
            RectTransform row = k.Columns(placement);
            h.aisleNumberInput = k.NumberField(row, "Aisle number", null, true);
            h.aisleCableLengthInput = k.NumberField(row, "Cable length", "m");
        }

        TMP_InputField TextField(Transform parent, string label) =>
            k.Input(k.Labeled(parent, label), label, TMP_InputField.ContentType.Standard);

        // ── Dialogs ───────────────────────────────────────────────────────────

        (RectTransform scrim, RectTransform window, Button close, Button scrimButton, SB_Panel panel) Dialog(
            RectTransform root, string name, string title, Vector2 size)
        {
            RectTransform scrim = Rect(name, root);
            Stretch(scrim);
            Image scrimImage = Img(scrim, null, T.scrim, true);
            var scrimButton = scrim.gameObject.AddComponent<Button>();
            scrimButton.targetGraphic = scrimImage;
            scrimButton.transition = Selectable.Transition.None;

            RectTransform window = Rect("Window", scrim);
            Place(window, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
            Img(window, T.fillR12, T.panelSolid, true);
            StoreBuilderUIKit.Ring(window, T.ringR12, T.lineStrong);
            window.gameObject.AddComponent<SB_ClickSink>();
            V(window.gameObject, 0f, Pad(1f));

            RectTransform header = Rect("Header", window);
            H(header.gameObject, 8f, Pad(20f, 12f, 12f, 12f), TextAnchor.MiddleLeft);
            Fixed(header.gameObject, -1f, 56f);
            Flex(k.Text(header, title, T.sansSemiBold, 17f, T.text).gameObject);
            Button close = k.Btn(header, "Close", null, "close", ButtonStyle.Ghost).button;
            k.Divider(window);

            SB_Panel panel = k.MakePanel(scrim.gameObject, new Vector2(0f, -8f), 0.98f, window);
            scrim.gameObject.SetActive(false);
            return (scrim, window, close, scrimButton, panel);
        }

        void BuildStoresDialog(RectTransform root)
        {
            var dialog = Dialog(root, "Stores Dialog", "Stores", new Vector2(780f, 470f));
            h.storesDialog = dialog.panel;
            h.storesCloseButton = dialog.close;
            h.storesScrimButton = dialog.scrimButton;

            RectTransform body = Rect("Body", dialog.window);
            H(body.gameObject, 0f, null, TextAnchor.UpperLeft, false, true);
            Flex(body.gameObject, 0f, 1f);

            // Open
            RectTransform open = Rect("Open", body);
            V(open.gameObject, 10f, Pad(16f), TextAnchor.UpperLeft, true, false);
            Fixed(open.gameObject, 430f);
            Flex(open.gameObject, 0f);
            k.Caption(open, "Open a saved store");
            h.storeSearchInput = k.Input(open, "Search", TMP_InputField.ContentType.Standard, 32f, null, "Search stores");

            RectTransform list = Rect("List", open);
            Flex(list.gameObject, 0f, 1f);
            list.gameObject.AddComponent<RectMask2D>();
            // Scroll events go to whatever is under the pointer, so the gaps between rows need something to hit.
            Img(list, null, T.Clear, true);
            RectTransform listContent = Rect("Content", list);
            listContent.anchorMin = new Vector2(0f, 1f);
            listContent.anchorMax = new Vector2(1f, 1f);
            listContent.pivot = new Vector2(0.5f, 1f);
            listContent.sizeDelta = Vector2.zero;
            V(listContent.gameObject, 4f, Pad(0f, 14f, 0f, 0f)); // right gap keeps rows clear of the scrollbar
            FitToContent(listContent.gameObject);
            var scroll = list.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = list;
            scroll.content = listContent;
            k.StyleScroll(scroll, list);
            h.storeListRoot = listContent;

            ButtonParts template = k.Row(listContent, "Row Template", 40f);
            k.Icon(template.button.transform, "folder", 18f, T.muted);
            TMP_Text rowName = k.Text(template.button.transform, "store", T.sansMedium, 13f, T.text);
            Flex(rowName.gameObject);
            TMP_Text rowWhen = k.Text(template.button.transform, "Today", T.sans, 12f, T.muted, TextAlignmentOptions.MidlineRight);
            var storeRow = template.button.gameObject.AddComponent<SB_StoreRow>();
            storeRow.button = template.button;
            storeRow.fx = template.fx;
            storeRow.nameText = rowName;
            storeRow.whenText = rowWhen;
            template.button.gameObject.SetActive(false);
            h.storeRowTemplate = storeRow;

            h.storeListEmptyText = k.Text(list, "", T.sans, 13f, T.muted, TextAlignmentOptions.Center, "Empty");
            h.storeListEmptyText.textWrappingMode = TextWrappingModes.Normal;
            Stretch(h.storeListEmptyText.rectTransform, 20f, 0f, 20f, 0f);

            RectTransform footer = Rect("Footer", open);
            H(footer.gameObject, 8f, null, TextAnchor.MiddleLeft);
            k.Icon(footer, "warn", 14f, T.warn);
            Flex(k.Text(footer, "Replaces the current store.", T.sans, 12f, T.muted).gameObject);
            ButtonParts openButton = k.Btn(footer, "Open", "Open", null, ButtonStyle.Primary);
            h.openStoreButton = openButton.button;
            h.openStoreLabel = openButton.label;

            RectTransform split = k.Divider(body, true);
            Fixed(split.gameObject, 1f, -1f);

            // Save
            RectTransform save = Rect("Save", body);
            V(save.gameObject, 10f, Pad(18f), TextAnchor.UpperLeft, true, false);
            Flex(save.gameObject);
            k.Caption(save, "Save current store");
            h.saveStoreTextField = k.Input(k.Labeled(save, "Store name"), "Store Name", TMP_InputField.ContentType.Standard);

            RectTransform warn = Rect("Overwrite Warning", save);
            Img(warn, T.fillR8, new Color(T.warn.r, T.warn.g, T.warn.b, 0.12f));
            H(warn.gameObject, 8f, Pad(10f), TextAnchor.UpperLeft);
            k.Icon(warn, "warn", 15f, T.warn);
            h.overwriteWarningText = k.Text(warn, "", T.sans, 12f, T.warn);
            h.overwriteWarningText.textWrappingMode = TextWrappingModes.Normal;
            Flex(h.overwriteWarningText.gameObject);
            h.overwriteWarning = warn.gameObject;

            ButtonParts saveButton = k.Btn(save, "Save", "Save", null, ButtonStyle.Primary);
            h.saveStoreButton = saveButton.button;
            h.saveStoreLabel = saveButton.label;
            h.saveStoreFx = saveButton.fx;
            k.Spacer(save).gameObject.GetComponent<LayoutElement>().flexibleHeight = 1f;

            ButtonParts folder = k.Btn(save, "Show Folder", "Show saves folder", "folder", ButtonStyle.Ghost, 34f, 18f, 0f);
            folder.label.color = T.accent;
            folder.icon.color = T.accent;
            h.showFolderButton = folder.button;
        }

        void BuildSettingsDialog(RectTransform root)
        {
            var dialog = Dialog(root, "Settings Dialog", "Settings", new Vector2(700f, 470f));
            h.settingsDialog = dialog.panel;
            h.settingsCloseButton = dialog.close;
            h.settingsScrimButton = dialog.scrimButton;

            RectTransform body = Rect("Body", dialog.window);
            H(body.gameObject, 0f, null, TextAnchor.UpperLeft, false, true);
            Flex(body.gameObject, 0f, 1f);

            RectTransform nav = Rect("Tabs", body);
            V(nav.gameObject, 4f, Pad(12f));
            Fixed(nav.gameObject, 170f);
            Flex(nav.gameObject, 0f); // a vertical group would otherwise claim spare width
            var tabButtons = new List<Button>();
            var tabFx = new List<SB_ButtonFx>();
            foreach (string label in new[] { "Agent", "Rendering" })
            {
                ButtonParts tab = k.Row(nav, label + " Tab", 36f);
                k.Text(tab.button.transform, label, T.sansMedium, 13f, T.text);
                tabButtons.Add(tab.button);
                tabFx.Add(tab.fx);
            }
            h.settingsTabButtons = tabButtons.ToArray();
            h.settingsTabFx = tabFx.ToArray();
            k.Divider(body, true);

            // Pages take whatever width is left, whatever their content wants, so the sidebar never resizes between tabs.
            RectTransform pages = Rect("Pages", body);
            Equal(pages.gameObject);
            V(pages.gameObject, 0f, null, TextAnchor.UpperLeft, true, true);

            RectTransform agent = Rect("Agent Page", pages);
            V(agent.gameObject, 14f, Pad(20f), TextAnchor.UpperLeft);
            h.agentTab = agent.gameObject;
            k.Body(agent, "How the shopper agent looks and acts when you press Play.", 12f, true, true);
            h.agentAvatarSegment = Choice(agent, "Avatar", new[] { "VR", "IK humanoid" },
                new[] { (int)AgentAvatarSetting.VR, (int)AgentAvatarSetting.ExperimentalIKHumanoid },
                new[] { false, true });
            h.agentInteractionSegment = Choice(agent, "Interaction style", new[] { "Gaze", "Manual", "Manual, gaze door" },
                new[] { (int)AgentInteractionStyle.Gaze, (int)AgentInteractionStyle.Manual, (int)AgentInteractionStyle.ManualButGazeDoor },
                new[] { true, false, true });
            h.agentBasketSegment = Choice(agent, "Item basket", new[] { "None", "Left hand" },
                new[] { (int)AgentBasketStyle.None, (int)AgentBasketStyle.LeftHand }, experimentalField: true);
            h.scanningDifficultySegment = Choice(agent, "Barcode scanning difficulty", new[] { "Easy", "Medium", "Hard" },
                new[] { (int)ScanningDifficulty.Easy, (int)ScanningDifficulty.Medium, (int)ScanningDifficulty.Hard });

            // Legend for the warning icons, pinned to the bottom of the page.
            Flex(k.Spacer(agent).gameObject, 0f, 1f);
            RectTransform legend = Rect("Legend", agent);
            H(legend.gameObject, 6f);
            k.Icon(legend, "warn", 13f, T.warn);
            k.Text(legend, "Experimental option. May be unstable or change.", T.sans, 12f, T.muted);

            RectTransform rendering = Rect("Rendering Page", pages);
            V(rendering.gameObject, 14f, Pad(20f), TextAnchor.UpperLeft);
            h.renderingTab = rendering.gameObject;
            rendering.gameObject.SetActive(false);
            BuildRenderingPage(rendering);
        }

        void BuildRenderingPage(RectTransform page)
        {
            k.Body(page, "Saved per machine. These trade memory for image quality and don't change the store.", 12f, true, true);

            RectTransform preset = Rect("Preset", page);
            V(preset.gameObject, 5f);
            RectTransform caption = Rect("Caption", preset);
            H(caption.gameObject, 8f);
            k.Text(caption, "Preset", T.sans, 12f, T.muted);
            var custom = k.Chip(caption, "Custom", ChipKind.Warn);
            h.renderCustomChip = custom.root.gameObject;
            h.renderPresetSegment = k.Segmented(preset, "Preset Choice",
                new[] { RenderingSettings.Label(RenderingPreset.LowMemory), RenderingSettings.Label(RenderingPreset.Balanced), RenderingSettings.Label(RenderingPreset.HighQuality) },
                new[] { (int)RenderingPreset.LowMemory, (int)RenderingPreset.Balanced, (int)RenderingPreset.HighQuality });

            RectTransform box = Rect("Options", page);
            Img(box, null, T.Clear);
            StoreBuilderUIKit.Ring(box, T.ringR8, T.line);
            V(box.gameObject, 0f, Pad(12f, 4f));

            h.textureArraysToggle = OptionRow(box, "Texture arrays", "Pack item textures together", out h.textureArraysCostText);
            k.Divider(box);
            h.indirectDrawToggle = OptionRow(box, "Indirect draw", "Faster rendering", out TMP_Text indirectCost);
            indirectCost.text = "+0 MB";
            k.Divider(box);

            RectTransform resolution = Rect("Resolution", box);
            H(resolution.gameObject, 12f, Pad(0f, 8f), TextAnchor.MiddleLeft);
            Flex(k.Text(resolution, "Texture resolution", T.sansMedium, 13f, T.text).gameObject);
            var values = new List<int>();
            var labels = new List<string>();
            int index = 0;
            foreach (TextureArrayResolution res in System.Enum.GetValues(typeof(TextureArrayResolution)))
            {
                values.Add(index++);
                labels.Add(RenderingSettings.Label(res));
            }
            h.textureResolutionSegment = k.Segmented(resolution, "Resolution Choice", labels.ToArray(), values.ToArray());
            Fixed(h.textureResolutionSegment.gameObject, 300f, 32f);

            RectTransform memory = Rect("Memory", page);
            H(memory.gameObject, 8f);
            Flex(k.Text(memory, "Estimated extra memory", T.sans, 12f, T.muted).gameObject);
            h.memoryCostText = k.Text(memory, "+0 MB", T.monoMedium, 13f, T.text, TextAlignmentOptions.MidlineRight);
        }

        Toggle OptionRow(Transform parent, string title, string caption, out TMP_Text cost)
        {
            RectTransform row = Rect(title + " Row", parent);
            H(row.gameObject, 12f, Pad(0f, 6f), TextAnchor.MiddleLeft);
            RectTransform text = Rect("Text", row);
            V(text.gameObject, 2f, null, TextAnchor.MiddleLeft);
            Flex(text.gameObject);
            k.Text(text, title, T.sansMedium, 13f, T.text);
            k.Text(text, caption, T.sans, 12f, T.muted);
            cost = k.Text(row, "", T.mono, 12f, T.muted, TextAlignmentOptions.MidlineRight);
            return k.Switch(row, "Switch");
        }
    }
}
