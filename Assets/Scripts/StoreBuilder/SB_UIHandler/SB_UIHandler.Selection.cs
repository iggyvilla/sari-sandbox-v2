using UnityEngine;

public partial class SB_UIHandler
{
    // -- Shelf / prop selection -------------------------------------------------

    /// <summary>The selected shelf or prop box, if any.</summary>
    public OutlineSelector ActiveSelector => _activeShelfSelector != null ? _activeShelfSelector : _activePropSelector;

    public ShelfBuilder SelectedShelf => _activeShelfSelector != null ? _activeShelfSelector.Shelf : null;

    public void ToggleSelection(OutlineSelector selector)
    {
        if (selector.IsSelected)
            ClearSelection();
        else if (selector.Shelf != null)
            SelectShelf(selector);
        else
            SelectProp(selector);
    }

    public void ClearSelection()
    {
        DeselectShelf();
        DeselectProp();
    }

    void SelectProp(OutlineSelector selector)
    {
        DeselectShelf();
        if (_activePropSelector != null && _activePropSelector != selector)
            _activePropSelector.Deselect();
        _activePropSelector = selector;
        selector.Select();

        // An aisle marker opens its own inspector, pre-filled with the marker's current values.
        _selectedAisleMarker = null;
        if (selector.Target.TryGetComponent(out AisleMarker marker))
            FillAisleInputs(marker);
        else
            ShowPropInspector(selector.Target);
        RefreshInspector();
    }

    void DeselectProp()
    {
        if (_activePropSelector != null)
            _activePropSelector.Deselect();
        _activePropSelector = null;
        _selectedAisleMarker = null;
        RefreshInspector();
    }

    void SelectShelf(OutlineSelector selector)
    {
        DeselectSubShelf();
        DeselectProp();
        if (_activeShelfSelector != null && _activeShelfSelector != selector)
            _activeShelfSelector.Deselect();

        _activeShelfSelector = selector;
        selector.Select();
        _userWantsSpawnItems = DataHandler.Instance.shouldShelfSpawnItems.TryGetValue(SelectedShelf.shelfId, out bool saved) && saved;
        shelfEditGroupHandler.UpdateFromShelf(SelectedShelf, _userWantsSpawnItems);
        priceTagToggle.interactable = _userWantsSpawnItems;

        RefreshShelfHeader();
        RefreshInspector();
        ShelfBuilder.DespawnAllItemsInScene();
    }

    void DeselectShelf()
    {
        DeselectSubShelf();
        if (_activeShelfSelector != null)
            _activeShelfSelector.Deselect();
        _activeShelfSelector = null;
        RefreshInspector();
    }

    void RefreshShelfHeader()
    {
        ShelfBuilder shelf = SelectedShelf;
        if (shelf == null) return;

        string kind = shelf.isFridge ? "Fridge" : "Shelf";
        shelfTitleText.text = $"{kind} {shelf.shelfId}";
        shelfSubtitleText.text = $"{kind} · {shelf.rotationY:0}°";
    }

    void ShowPropInspector(GameObject target)
    {
        propTitleText.text = SB_InteractionController.Describe(target);
        // The store has a single agent spawn, so it cannot be copied.
        propActions.duplicate.gameObject.SetActive(!target.TryGetComponent(out AgentSpawnMarker _));
    }

    // -- Sub-shelf selection ---------------------------------------------------

    public void ToggleSubShelf(SubShelfMarker marker)
    {
        if (_activeSubShelf == marker)
        {
            DeselectSubShelf();
            return;
        }

        // Also clears the previous sub-shelf; the new one is selected below.
        ClearSelection();

        _activeSubShelf = marker;
        marker.EnableOutline(true);
        PopulateSubShelfCategoryDropdown(marker);
        subShelfSubtitleText.text = $"Shelf {marker.shelfInfo.shelfId}";
        RefreshInspector();
    }

    void DeselectSubShelf()
    {
        if (_activeSubShelf == null) return;
        _activeSubShelf.EnableOutline(false);
        _activeSubShelf = null;
        RefreshInspector();
    }

    void PopulateSubShelfCategoryDropdown(SubShelfMarker marker)
    {
        string key = ShelfBuilder.CategoryKey(marker.shelfInfo);
        int value = marker.parentShelf.subShelfCategories.TryGetValue(key, out ItemCategory cat)
            ? (int)cat
            : 0;
        itemCategoryDropdown.SetValueWithoutNotify(value);
    }

    void OnSubShelfCategoryChanged(int index)
    {
        if (_activeSubShelf == null) return;
        _activeSubShelf.parentShelf.subShelfCategories[ShelfBuilder.CategoryKey(_activeSubShelf.shelfInfo)] = (ItemCategory)index;
        MarkDirty();
    }

    void OnApplyShelfCategoryPressed()
    {
        if (SelectedShelf == null) return;
        ItemCategory category = (ItemCategory)shelfCategoryDropdown.value;
        foreach (SubShelfMarker marker in SelectedShelf.GetComponentsInChildren<SubShelfMarker>())
            SelectedShelf.subShelfCategories[ShelfBuilder.CategoryKey(marker.shelfInfo)] = category;
        MarkDirty();
    }
}
