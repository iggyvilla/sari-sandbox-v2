using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class StoreBuilderUIHandler : MonoBehaviour
{
    [Header("Selection UI")] 
    public GameObject shelfEditCanvas;
    public GameObject tooltipText;
    
    // Default fallback values used when the user enters an invalid input
    private const int   DefaultShelfWidth            = 2;
    private const int   DefaultNumberOfLevels        = 3;
    private const float DefaultDistanceBetweenLevels = 0.4f;
    private const float DefaultShelfRoofHeight       = 0.4f;
    private const float DefaultBootHeight            = 0.4f;

    // Set externally (e.g. by a shelf-selection script) before any UI interaction
    public ShelfBuilder selectedShelf;

    public TextMeshProUGUI selectedShelfText;

    private ShelfSelector _activeSelector;

    public Toggle spawnItemsToggle;
    public Toggle priceTagToggle;

    // Mirrors the spawn-items toggle so overflow suppression can be undone cleanly
    private bool _userWantsSpawnItems;
    public ShelfEditGroupHandler shelfEditGroupHandler;

    void Start()
    {
        spawnItemsToggle.isOn = false;
        UpdateSelectedShelfText();
    }

    // ── Shelf selection ───────────────────────────────────────────────────────

    public void SelectShelf(ShelfSelector selector)
    {
        if (_activeSelector != null && _activeSelector != selector)
            _activeSelector.Deselect();

        _activeSelector = selector;
        selector.Select();
        selectedShelf = selector.assignedShelf;
        shelfEditGroupHandler.UpdateFromShelf(selectedShelf);
        SetSelectionUIView(true);
        UpdateSelectedShelfText();
    }

    public void DeselectShelf()
    {
        if (_activeSelector != null)
            _activeSelector.Deselect();
        _activeSelector = null;
        selectedShelf = null;
        SetSelectionUIView(false);
        UpdateSelectedShelfText();
    }

    void SetSelectionUIView(bool show)
    {
        shelfEditCanvas.SetActive(show);
        tooltipText.SetActive(show);
    }

    private void UpdateSelectedShelfText()
    {
        if (selectedShelfText == null) return;
        selectedShelfText.text = selectedShelf != null
            ? $"Selected Shelf: Shelf {selectedShelf.shelfId}"
            : "Selected Shelf: NONE";
    }
    
    // ── Int / Float input fields ──────────────────────────────────────────────

    // Called by the shelfWidth InputField's OnValueChanged event
    public void OnShelfWidthChanged(string value)
    {
        if (selectedShelf == null) return;
        if (!int.TryParse(value, out int result) || result <= 0)
            result = DefaultShelfWidth;
        selectedShelf.shelfWidth = result;
        RebuildShelf();
    }

    // Called by the numberOfLevels InputField's OnValueChanged event
    public void OnNumberOfLevelsChanged(string value)
    {
        if (selectedShelf == null) return;
        if (!int.TryParse(value, out int result) || result <= 0)
            result = DefaultNumberOfLevels;
        selectedShelf.shelfLevels = result;
        RebuildShelf();
    }

    // Called by the distanceBetweenLevels InputField's OnValueChanged event
    public void OnDistanceBetweenLevelsChanged(string value)
    {
        if (selectedShelf == null) return;
        if (!float.TryParse(value, out float result) || result <= 0f)
            result = DefaultDistanceBetweenLevels;
        selectedShelf.distanceBetweenLevels = result;
        RebuildShelf();
    }
    
    // Called by the shelfRoofHeight InputField's OnValueChanged event
    public void OnShelfRoofHeightChanged(string value)
    {
        if (selectedShelf == null) return;
        if (!float.TryParse(value, out float result) || result <= 0f)
            result = DefaultShelfRoofHeight;
        selectedShelf.shelfRoofHeight = result;
        RebuildShelf();
    }

    // Called by the bootHeight InputField's OnValueChanged event
    public void OnBootHeightChanged(string value)
    {
        if (selectedShelf == null) return;
        if (!float.TryParse(value, out float result) || result <= 0f)
            result = DefaultBootHeight;
        selectedShelf.shelfBootHeight = result;
        RebuildShelf();
    }

    // ── Dropdowns ─────────────────────────────────────────────────────────────

    // rotY dropdown: 0 → 0°, 1 → 90°, 2 → 180°, 3 → 270°
    public void OnRotYChanged(int index)
    {
        if (selectedShelf == null) return;
        selectedShelf.rotationY = index * 90f;
        RebuildShelf();
    }

    // itemSpawnOption dropdown: 0 → GenerateRandom, 1 → GenerateRandomThenSave, 2 → ReadFromSave
    public void OnItemSpawnOptionChanged(int index)
    {
        if (selectedShelf == null) return;
        selectedShelf.itemSpawnOption = (ItemSpawnOption)index;
        RebuildShelf();
    }

    public void OnFridgeDoorStyleChanged(int index)
    {
        if (selectedShelf == null) return;
        selectedShelf.fridgeDoorStyle = (FridgeDoorStyle)index;
        RebuildShelf();
    }

    // ── Shelf face toggles ────────────────────────────────────────────────────

    public void OnSpawnFrontShelfChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.frontShelfConfig;
        cfg.buildShelves = toggle.isOn;
        selectedShelf.frontShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnBackShelfChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.backShelfConfig;
        cfg.buildShelves = toggle.isOn;
        selectedShelf.backShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnLeftShelfChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.leftShelfConfig;
        cfg.buildShelves = toggle.isOn;
        selectedShelf.leftShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnRightShelfChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.rightShelfConfig;
        cfg.buildShelves = toggle.isOn;
        selectedShelf.rightShelfConfig = cfg;
        RebuildShelf();
    }

    // ── Shelf wall toggles ────────────────────────────────────────────────────

    public void OnSpawnFrontWallChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.frontShelfConfig;
        cfg.buildBackWall = toggle.isOn;
        selectedShelf.frontShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnBackWallChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.backShelfConfig;
        cfg.buildBackWall = toggle.isOn;
        selectedShelf.backShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnLeftWallChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.leftShelfConfig;
        cfg.buildBackWall = toggle.isOn;
        selectedShelf.leftShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnRightWallChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.rightShelfConfig;
        cfg.buildBackWall = toggle.isOn;
        selectedShelf.rightShelfConfig = cfg;
        RebuildShelf();
    }
    
    // ── Shelf roof toggles ────────────────────────────────────────────────────

    public void OnSpawnFrontRoofChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.frontShelfConfig;
        cfg.buildShelfRoof = toggle.isOn;
        selectedShelf.frontShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnBackRoofChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.backShelfConfig;
        cfg.buildShelfRoof = toggle.isOn;
        selectedShelf.backShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnLeftRoofChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.leftShelfConfig;
        cfg.buildShelfRoof = toggle.isOn;
        selectedShelf.leftShelfConfig = cfg;
        RebuildShelf();
    }

    public void OnSpawnRightRoofChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        ShelfConfiguration cfg = selectedShelf.rightShelfConfig;
        cfg.buildShelfRoof = toggle.isOn;
        selectedShelf.rightShelfConfig = cfg;
        RebuildShelf();
    }

    // ── Item spawn toggles ────────────────────────────────────────────────────

    public void OnSpawnItemsChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        _userWantsSpawnItems = toggle.isOn;
        
        if (!_userWantsSpawnItems)
        {
            // Disables spawn price tags toggle
            // If there are no items, there are definitely no price tags
            priceTagToggle.isOn = false;
            priceTagToggle.interactable = false;
        }
        else
        {
            priceTagToggle.interactable = true;
        }

        RebuildShelf();
    }

    public void OnSpawnPriceTagsChanged(Toggle toggle)
    {
        if (selectedShelf == null) return;
        selectedShelf.spawnPriceTags = toggle.isOn;

        if (!selectedShelf.spawnPriceTags)
        {
            ShelfBuilder.DeleteAllPriceTags();
        }
        else
        {
            RebuildShelf();
        }
    }

    public void OnSpawnHingeDoorsChange(Toggle toggle)
    {
        if (selectedShelf == null) return;
        
        selectedShelf.spawnHingeDoors = toggle.isOn;
        
        RebuildShelf();
    }

    // ── Utility functions ─────────────────────────────────────────────────────

    // Removes the saved item list for a specific sub-shelf level from DataHandler
    // and re-saves the store file.
    public void ClearSubShelfItems(ShelfInfo shelfInfo)
    {
        string key = $"ID{shelfInfo.shelfId}_{shelfInfo.subShelfId}_{shelfInfo.subSubShelfId}";
        DataHandler.Instance.currentStoreData.shelfItems.Remove(key);
        DataHandler.Instance.SaveStore();
        Debug.Log($"[StoreBuilderUIHandler] Cleared items for sub-shelf {key}.");
    }

    // Updates the store name used for save/load file paths
    public void SetStoreName(string name)
    {
        DataHandler.Instance.storeName = name;
    }

    // ── Shelf rotation ────────────────────────────────────────────────────────

    public void RotateSelectedShelf()
    {
        if (selectedShelf == null) return;
        selectedShelf.rotationY = (selectedShelf.rotationY + 90f) % 360f;
        shelfEditGroupHandler.rotationY?.SetValueWithoutNotify(
            Mathf.RoundToInt(selectedShelf.rotationY / 90f) % 4);
        RebuildShelf();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void RebuildShelf()
    {
        if (selectedShelf == null) return;

        // If the user chose ReadFromSave and any saved sub-shelf's items are wider
        // than the current shelf width, disable spawning until the shelf is wide enough.
        bool overflow = CheckReadFromSaveOverflow();
        selectedShelf.spawnItems = overflow ? false : _userWantsSpawnItems;

        if (overflow)
            Debug.LogWarning(
                $"[StoreBuilderUIHandler] Shelf {selectedShelf.shelfId}: one or more saved " +
                $"sub-shelves have items wider than shelfWidth ({selectedShelf.shelfWidth}). " +
                $"Item spawning disabled until width is sufficient."
            );
        
        GPUInstanceTracker.Instance.DespawnAllItems();
        ShelfBuilder.DeleteAllPriceTags();
        
        selectedShelf.Rebuild();
        if (_activeSelector != null) _activeSelector.EncapsulateShelf(selectedShelf);
    }

    // Returns true when itemSpawnOption is ReadFromSave and at least one saved
    // sub-shelf for the selected shelf has itemsTotalWidth > current shelfWidth.
    private bool CheckReadFromSaveOverflow()
    {
        if (selectedShelf.itemSpawnOption != ItemSpawnOption.ReadFromSave)
            return false;

        string prefix = $"ID{selectedShelf.shelfId}_";
        foreach (var kvp in DataHandler.Instance.currentStoreData.shelfItems)
        {
            if (kvp.Key.StartsWith(prefix) && kvp.Value.itemsTotalWidth > selectedShelf.shelfWidth)
                return true;
        }

        return false;
    }
}
