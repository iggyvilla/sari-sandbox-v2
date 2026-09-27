using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public partial class SB_UIHandler
{
    // -- Int / Float input fields ---------------------------------------------
    // Called by the matching InputField's OnValueChanged event; invalid input falls back to the default.

    public void OnShelfWidthChanged(string value) =>
        EditShelf(s => s.shelfWidth = ParsePositive(value, DefaultShelfWidth));

    public void OnNumberOfLevelsChanged(string value) =>
        EditShelf(s => s.shelfLevels = int.TryParse(value, out int v) && v > 0 ? v : DefaultNumberOfLevels);

    public void OnDistanceBetweenLevelsChanged(string value) =>
        EditShelf(s => s.distanceBetweenLevels = ParsePositive(value, DefaultDistanceBetweenLevels));

    public void OnShelfRoofHeightChanged(string value) =>
        EditShelf(s => s.shelfRoofHeight = ParsePositive(value, DefaultShelfRoofHeight));

    public void OnBootHeightChanged(string value) =>
        EditShelf(s => s.shelfBootHeight = ParsePositive(value, DefaultBootHeight));

    static float ParsePositive(string value, float fallback) =>
        float.TryParse(value, out float v) && v > 0f ? v : fallback;

    // -- Dropdowns ------------------------------------------------------------

    // rotY dropdown: 0 -> 0 degrees, 1 -> 90 degrees, 2 -> 180 degrees, 3 -> 270 degrees
    public void OnRotYChanged(int index) => EditShelf(s => s.rotationY = index * 90f);

    // itemSpawnOption dropdown: 0 -> GenerateRandom, 1 -> GenerateRandomThenSave, 2 -> ReadFromSave
    public void OnItemSpawnOptionChanged(int index) => EditShelf(s => s.itemSpawnOption = (ItemSpawnOption)index);

    public void OnFridgeDoorStyleChanged(int index) => EditShelf(s => s.fridgeDoorStyle = (FridgeDoorStyle)index);

    // -- Shelf face / wall / roof toggles ---------------------------------------

    public void OnSpawnFrontShelfChanged(Toggle t) => EditFace(ShelfFace.Front, c => { c.buildShelves = t.isOn; return c; });
    public void OnSpawnBackShelfChanged(Toggle t)  => EditFace(ShelfFace.Back,  c => { c.buildShelves = t.isOn; return c; });
    public void OnSpawnLeftShelfChanged(Toggle t)  => EditFace(ShelfFace.Left,  c => { c.buildShelves = t.isOn; return c; });
    public void OnSpawnRightShelfChanged(Toggle t) => EditFace(ShelfFace.Right, c => { c.buildShelves = t.isOn; return c; });

    public void OnSpawnFrontWallChanged(Toggle t) => EditFace(ShelfFace.Front, c => { c.buildBackWall = t.isOn; return c; });
    public void OnSpawnBackWallChanged(Toggle t)  => EditFace(ShelfFace.Back,  c => { c.buildBackWall = t.isOn; return c; });
    public void OnSpawnLeftWallChanged(Toggle t)  => EditFace(ShelfFace.Left,  c => { c.buildBackWall = t.isOn; return c; });
    public void OnSpawnRightWallChanged(Toggle t) => EditFace(ShelfFace.Right, c => { c.buildBackWall = t.isOn; return c; });

    public void OnSpawnFrontRoofChanged(Toggle t) => EditFace(ShelfFace.Front, c => { c.buildShelfRoof = t.isOn; return c; });
    public void OnSpawnBackRoofChanged(Toggle t)  => EditFace(ShelfFace.Back,  c => { c.buildShelfRoof = t.isOn; return c; });
    public void OnSpawnLeftRoofChanged(Toggle t)  => EditFace(ShelfFace.Left,  c => { c.buildShelfRoof = t.isOn; return c; });
    public void OnSpawnRightRoofChanged(Toggle t) => EditFace(ShelfFace.Right, c => { c.buildShelfRoof = t.isOn; return c; });

    enum ShelfFace { Front, Back, Left, Right }

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

    public void OnSpawnItemsChanged(Toggle toggle)
    {
        if (SelectedShelf == null) return;
        _userWantsSpawnItems = toggle.isOn;
        DataHandler.Instance.shouldShelfSpawnItems[SelectedShelf.shelfId] = _userWantsSpawnItems;

        priceTagToggle.interactable = _userWantsSpawnItems;
        if (!_userWantsSpawnItems)
            priceTagToggle.isOn = false;
    }

    public void OnSpawnItemsOnAllShelvesButtonPressed()
    {
        ShelfBuilder.DeleteAllPriceTags();

        // Makes shelves spawn items only if indicated in DataHandler.Instance.shelfSpawnItems
        foreach (ShelfBuilder shelf in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
        {
            shelf.spawnItems = DataHandler.Instance.shouldShelfSpawnItems.TryGetValue(shelf.shelfId, out bool wantsSpawn) && wantsSpawn;
            if (shelf.spawnItems) shelf.Rebuild();
        }
    }

    public void OnSpawnPriceTagsChanged(Toggle toggle)
    {
        if (SelectedShelf == null) return;
        SelectedShelf.spawnPriceTags = toggle.isOn;

        if (!SelectedShelf.spawnPriceTags)
        {
            ShelfBuilder.DeleteAllPriceTags();
        }
    }

    public void OnSpawnHingeDoorsChange(Toggle toggle) => EditShelf(s => s.isFridge = toggle.isOn);

    // -- Shelf rotation --------------------------------------------------------

    public void RotateSelectedShelf()
    {
        if (SelectedShelf == null) return;
        SelectedShelf.RotateQuarterTurn();
        ShelfEditGroupHandler.SetValue(
            shelfEditGroupHandler.rotationY,
            ShelfEditGroupHandler.RotationIndex(SelectedShelf.rotationY));
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
