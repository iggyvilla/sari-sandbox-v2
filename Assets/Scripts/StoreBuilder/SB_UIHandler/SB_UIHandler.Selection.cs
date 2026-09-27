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
        tooltipText.SetActive(true);

        // If the selected prop is an aisle marker, open its edit menu pre-filled
        // with the marker's current values.
        if (selector.Target.TryGetComponent(out AisleMarker marker))
            ShowAisleMarkerMenu(marker);
        else
            HideAisleMarkerMenu();
    }

    void DeselectProp()
    {
        if (_activePropSelector != null)
            _activePropSelector.Deselect();
        _activePropSelector = null;
        tooltipText.SetActive(false);
        HideAisleMarkerMenu();
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

        SetSelectionUIView(true);
        UpdateSelectedShelfText();
        ShelfBuilder.DespawnAllItemsInScene();
    }

    void DeselectShelf()
    {
        DeselectSubShelf();
        if (_activeShelfSelector != null)
            _activeShelfSelector.Deselect();
        _activeShelfSelector = null;
        SetSelectionUIView(false);
        UpdateSelectedShelfText();
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
        itemCategorySelection.SetActive(true);
    }

    void DeselectSubShelf()
    {
        if (_activeSubShelf == null) return;
        _activeSubShelf.EnableOutline(false);
        _activeSubShelf = null;
        if (itemCategorySelection != null)
            itemCategorySelection.SetActive(false);
    }

    void PopulateSubShelfCategoryDropdown(SubShelfMarker marker)
    {
        string key = ShelfBuilder.CategoryKey(marker.shelfInfo);
        int value = marker.parentShelf.subShelfCategories.TryGetValue(key, out ItemCategory cat)
            ? (int)cat
            : 0;
        itemCategoryDropdown.SetValueWithoutNotify(value);
    }

    // Wired to itemCategoryDropdown.OnValueChanged in the Inspector
    public void OnSubShelfCategoryChanged(int index)
    {
        if (_activeSubShelf == null) return;
        _activeSubShelf.parentShelf.subShelfCategories[ShelfBuilder.CategoryKey(_activeSubShelf.shelfInfo)] = (ItemCategory)index;
    }

    // Wired to ApplyShelfCategory button's OnClick in the Inspector
    public void OnApplyShelfCategoryPressed()
    {
        if (SelectedShelf == null) return;
        ItemCategory category = (ItemCategory)shelfCategoryDropdown.value;
        foreach (SubShelfMarker marker in SelectedShelf.GetComponentsInChildren<SubShelfMarker>())
            SelectedShelf.subShelfCategories[ShelfBuilder.CategoryKey(marker.shelfInfo)] = category;
    }

    void SetSelectionUIView(bool show)
    {
        shelfEditCanvas.SetActive(show);
        tooltipText.SetActive(show);
    }

    private void UpdateSelectedShelfText()
    {
        if (selectedShelfText == null) return;
        selectedShelfText.text = SelectedShelf != null
            ? $"Selected Shelf: Shelf {SelectedShelf.shelfId}"
            : "Selected Shelf: NONE";
    }
}
