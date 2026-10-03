using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public partial class SB_UIHandler
{
    enum ShelfFace { Front, Back, Left, Right }

    void BindShelfInspector()
    {
        ShelfEditGroupHandler g = shelfEditGroupHandler;
        shelfTabs.onValueChanged.AddListener(OnShelfTabChanged);
        shelfTabs.SetValueWithoutNotify(0);
        OnShelfTabChanged(0);
        applyShelfCategoryButton.onClick.AddListener(OnApplyShelfCategoryPressed);
        itemCategoryDropdown.onValueChanged.AddListener(OnSubShelfCategoryChanged);

        // Numbers apply when editing ends (Enter or click away); an invalid entry keeps the current value.
        g.shelfWidth.onEndEdit.AddListener(OnShelfWidthChanged);
        g.shelfLevels.onEndEdit.AddListener(OnNumberOfLevelsChanged);
        g.distanceBetweenLevels.onEndEdit.AddListener(OnDistanceBetweenLevelsChanged);
        g.roofHeight.onEndEdit.AddListener(OnShelfRoofHeightChanged);
        g.bootHeight.onEndEdit.AddListener(OnBootHeightChanged);

        g.rotationY.onValueChanged.AddListener(OnRotYChanged);
        g.itemSpawnOption.onValueChanged.AddListener(OnItemSpawnOptionChanged);
        g.fridgeDoorStyle.onValueChanged.AddListener(OnFridgeDoorStyleChanged);

        BindFace(g.spawnFrontShelf, ShelfFace.Front, WithShelves);
        BindFace(g.spawnBackShelf, ShelfFace.Back, WithShelves);
        BindFace(g.spawnLShelf, ShelfFace.Left, WithShelves);
        BindFace(g.spawnRShelf, ShelfFace.Right, WithShelves);
        BindFace(g.spawnFrontShelfWall, ShelfFace.Front, WithWall);
        BindFace(g.spawnBackShelfWall, ShelfFace.Back, WithWall);
        BindFace(g.spawnLShelfWall, ShelfFace.Left, WithWall);
        BindFace(g.spawnRShelfWall, ShelfFace.Right, WithWall);
        BindFace(g.spawnFShelfRoof, ShelfFace.Front, WithRoof);
        BindFace(g.spawnBShelfRoof, ShelfFace.Back, WithRoof);
        BindFace(g.spawnLShelfRoof, ShelfFace.Left, WithRoof);
        BindFace(g.spawnRShelfRoof, ShelfFace.Right, WithRoof);

        Bind(g.spawnItems, OnSpawnItemsChanged);
        Bind(g.spawnPriceTags, OnSpawnPriceTagsChanged);
        Bind(g.spawnHingeDoors, OnSpawnHingeDoorsChange);
    }

    void OnShelfTabChanged(int tab)
    {
        shelfLayoutTab.SetActive(tab == 0);
        shelfItemsTab.SetActive(tab == 1);
    }

    // -- Int / Float input fields ---------------------------------------------

    void OnShelfWidthChanged(string value) =>
        EditShelf(s => s.shelfWidth = ParsePositive(value, s.shelfWidth));

    void OnNumberOfLevelsChanged(string value) =>
        EditShelf(s => s.shelfLevels = int.TryParse(value, out int v) && v > 0 ? v : s.shelfLevels);

    void OnDistanceBetweenLevelsChanged(string value) =>
        EditShelf(s => s.distanceBetweenLevels = ParsePositive(value, s.distanceBetweenLevels));

    void OnShelfRoofHeightChanged(string value) =>
        EditShelf(s => s.shelfRoofHeight = ParsePositive(value, s.shelfRoofHeight));

    void OnBootHeightChanged(string value) =>
        EditShelf(s => s.shelfBootHeight = ParsePositive(value, s.shelfBootHeight));

    static float ParsePositive(string value, float fallback) =>
        float.TryParse(value, out float v) && v > 0f ? v : fallback;

    // -- Segments and dropdowns -------------------------------------------------

    // rotY segments: 0 -> 0 degrees, 1 -> 90 degrees, 2 -> 180 degrees, 3 -> 270 degrees
    void OnRotYChanged(int index) => EditShelf(s => s.rotationY = index * 90f);

    // itemSpawnOption dropdown: 0 -> GenerateRandom, 1 -> GenerateRandomThenSave, 2 -> ReadFromSave
    void OnItemSpawnOptionChanged(int index) => EditShelf(s => s.itemSpawnOption = (ItemSpawnOption)index);

    void OnFridgeDoorStyleChanged(int index) => EditShelf(s => s.fridgeDoorStyle = (FridgeDoorStyle)index);

    // -- Shelf face / wall / roof toggles ---------------------------------------

    static ShelfConfiguration WithShelves(ShelfConfiguration c, bool on) { c.buildShelves = on; return c; }
    static ShelfConfiguration WithWall(ShelfConfiguration c, bool on) { c.buildBackWall = on; return c; }
    static ShelfConfiguration WithRoof(ShelfConfiguration c, bool on) { c.buildShelfRoof = on; return c; }

    void BindFace(Toggle toggle, ShelfFace face, Func<ShelfConfiguration, bool, ShelfConfiguration> with) =>
        Bind(toggle, t => EditFace(face, c => with(c, t.isOn)));

    // Applies an edit to the selected shelf and rebuilds it.
    void EditShelf(Action<ShelfBuilder> edit)
    {
        if (SelectedShelf == null) return;
        edit(SelectedShelf);
        SafeRebuildShelf();
    }

    // ShelfConfiguration is a struct, so edits are copied back into the chosen face.
    void EditFace(ShelfFace face, Func<ShelfConfiguration, ShelfConfiguration> edit) => EditShelf(s =>
    {
        switch (face)
        {
            case ShelfFace.Front: s.frontShelfConfig = edit(s.frontShelfConfig); break;
            case ShelfFace.Back:  s.backShelfConfig  = edit(s.backShelfConfig);  break;
            case ShelfFace.Left:  s.leftShelfConfig  = edit(s.leftShelfConfig);  break;
            case ShelfFace.Right: s.rightShelfConfig = edit(s.rightShelfConfig); break;
        }
    });

    // -- Item spawn toggles ----------------------------------------------------

    void OnSpawnItemsChanged(Toggle toggle)
    {
        if (SelectedShelf == null) return;
        _userWantsSpawnItems = toggle.isOn;
        DataHandler.Instance.shouldShelfSpawnItems[SelectedShelf.shelfId] = _userWantsSpawnItems;

        priceTagToggle.interactable = _userWantsSpawnItems;
        if (!_userWantsSpawnItems)
            priceTagToggle.isOn = false;
        MarkDirty();
    }

    void OnSpawnItemsOnAllShelvesButtonPressed()
    {
        ShelfBuilder.DeleteAllPriceTags();

        // Makes shelves spawn items only if indicated in DataHandler.Instance.shelfSpawnItems
        foreach (ShelfBuilder shelf in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
        {
            shelf.spawnItems = DataHandler.Instance.shouldShelfSpawnItems.TryGetValue(shelf.shelfId, out bool wantsSpawn) && wantsSpawn;
            if (shelf.spawnItems) shelf.Rebuild();
        }
    }

    void OnSpawnPriceTagsChanged(Toggle toggle)
    {
        if (SelectedShelf == null) return;
        SelectedShelf.spawnPriceTags = toggle.isOn;

        if (!SelectedShelf.spawnPriceTags)
        {
            ShelfBuilder.DeleteAllPriceTags();
        }
        MarkDirty();
    }

    void OnSpawnHingeDoorsChange(Toggle toggle) => EditShelf(s => s.isFridge = toggle.isOn);

    // -- Shelf rotation --------------------------------------------------------

    public void RotateSelectedShelf()
    {
        if (SelectedShelf == null) return;
        SelectedShelf.RotateQuarterTurn();
        SafeRebuildShelf();
    }

    private void SafeRebuildShelf()
    {
        if (SelectedShelf == null) return;

        // If the user chose ReadFromSave and any saved sub-shelf's items are wider
        // than the current shelf width, disable spawning until the shelf is wide enough.
        bool overflow = CheckReadFromSaveOverflow();
        SelectedShelf.spawnItems = overflow ? false : _userWantsSpawnItems;

        if (overflow)
            Debug.LogWarning(
                $"[StoreBuilderUIHandler] Shelf {SelectedShelf.shelfId}: one or more saved " +
                $"sub-shelves have items wider than shelfWidth ({SelectedShelf.shelfWidth}). " +
                $"Item spawning disabled until width is sufficient."
            );

        // Despawn items (and price tags) in case of a rotation/translation etc.
        ShelfBuilder.DespawnAllItemsInScene();

        SelectedShelf.Rebuild();
        _activeShelfSelector.Refit();

        // Show the values the shelf really ended up with (invalid entries revert) and flag the unsaved change.
        shelfEditGroupHandler.UpdateFromShelf(SelectedShelf, _userWantsSpawnItems);
        RefreshShelfHeader();
        MarkDirty();
    }

    // Returns true when itemSpawnOption is ReadFromSave and at least one saved
    // sub-shelf for the selected shelf is wider than the current shelfWidth.
    private bool CheckReadFromSaveOverflow()
    {
        if (SelectedShelf.itemSpawnOption != ItemSpawnOption.ReadFromSave)
            return false;

        string prefix = ShelfItemData.KeyPrefix(SelectedShelf.shelfId);
        foreach (var kvp in DataHandler.Instance.currentStoreData.shelfItems)
        {
            if (!kvp.Key.StartsWith(prefix, StringComparison.Ordinal) || kvp.Value.items == null) continue;

            List<RetailItemData> items = ShelfItemData.Resolve(kvp.Value.items, kvp.Key);
            if (ShelfItemData.TotalWidth(items, ItemSpawner.InterItemPadding) > SelectedShelf.shelfWidth)
                return true;
        }

        return false;
    }
}
