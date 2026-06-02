using System.Collections.Generic;
using UnityEngine;

public partial class SB_UIHandler
{
    // -- Shelf selection -------------------------------------------------------

    public void SelectProp(PropSelector selector)
    {
        DeselectShelf();
        if (_activePropSelector != null && _activePropSelector != selector)
            _activePropSelector.Deselect();
        _activePropSelector = selector;
        selector.Select();
        tooltipText.SetActive(true);

        // If the selected prop is an aisle marker, open its edit menu pre-filled
        // with the marker's current values.
        AisleMarker marker = selector.assignedProp != null
            ? selector.assignedProp.GetComponent<AisleMarker>()
            : null;
        if (marker != null)
            ShowAisleMarkerMenu(marker);
        else
            HideAisleMarkerMenu();
    }

    public void DeselectProp()
    {
        if (_activePropSelector != null)
            _activePropSelector.Deselect();
        _activePropSelector = null;
        tooltipText.SetActive(false);
        HideAisleMarkerMenu();
    }

    public bool IsActivePropSelector(PropSelector selector) => _activePropSelector == selector;

    public void SelectShelf(ShelfSelector selector)
    {
        DeselectSubShelf();
        DeselectProp();
        if (_activeSelector != null && _activeSelector != selector)
            _activeSelector.Deselect();

        _activeSelector = selector;
        selector.Select();
        selectedShelf = selector.assignedShelf;
        _userWantsSpawnItems = DataHandler.Instance.shouldShelfSpawnItems.TryGetValue(selectedShelf.shelfId, out bool saved) && saved;
        shelfEditGroupHandler.UpdateFromShelf(selectedShelf, _userWantsSpawnItems);

        SetSelectionUIView(true);
        UpdateSelectedShelfText();
        ShelfBuilder.DespawnAllItemsInScene();
    }

    public void DeselectShelf()
    {
        DeselectSubShelf();
        if (_activeSelector != null)
            _activeSelector.Deselect();
        _activeSelector = null;
        selectedShelf = null;
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

        if (_activeSubShelf != null)
            _activeSubShelf.EnableOutline(false);

        // Deselect the main shelf without recursing into DeselectSubShelf
        // (_activeSubShelf is null at this point so the call is a no-op there)
        DeselectShelf();

        _activeSubShelf = marker;
        marker.EnableOutline(true);
        PopulateSubShelfCategoryDropdown(marker);
        itemCategorySelection.SetActive(true);
    }

    public void DeselectSubShelf()
    {
        if (_activeSubShelf == null) return;
        _activeSubShelf.EnableOutline(false);
        _activeSubShelf = null;
        if (itemCategorySelection != null)
            itemCategorySelection.SetActive(false);
    }

    public bool IsSubShelfSelected() => _activeSubShelf != null;

    void PopulateSubShelfCategoryDropdown(SubShelfMarker marker)
    {
        itemCategoryDropdown.ClearOptions();
        itemCategoryDropdown.AddOptions(new List<string>(System.Enum.GetNames(typeof(ItemCategory))));

        string key = $"{marker.shelfInfo.subShelfId}_{marker.shelfInfo.subSubShelfId}";
        int value = marker.parentShelf.subShelfCategories.TryGetValue(key, out ItemCategory cat)
            ? (int)cat
            : 0;
        itemCategoryDropdown.SetValueWithoutNotify(value);
    }

    // Wired to itemCategoryDropdown.OnValueChanged in the Inspector
    public void OnSubShelfCategoryChanged(int index)
    {
        if (_activeSubShelf == null) return;
        string key = $"{_activeSubShelf.shelfInfo.subShelfId}_{_activeSubShelf.shelfInfo.subSubShelfId}";
        _activeSubShelf.parentShelf.subShelfCategories[key] = (ItemCategory)index;
    }

    void PopulateShelfCategoryDropdown()
    {
        if (shelfCategoryDropdown == null) return;
        shelfCategoryDropdown.ClearOptions();
        shelfCategoryDropdown.AddOptions(new List<string>(System.Enum.GetNames(typeof(ItemCategory))));
    }

    // Wired to ApplyShelfCategory button's OnClick in the Inspector
    public void OnApplyShelfCategoryPressed()
    {
        if (selectedShelf == null) return;
        ItemCategory category = (ItemCategory)shelfCategoryDropdown.value;
        foreach (SubShelfMarker marker in selectedShelf.GetComponentsInChildren<SubShelfMarker>())
        {
            string key = $"{marker.shelfInfo.subShelfId}_{marker.shelfInfo.subSubShelfId}";
            selectedShelf.subShelfCategories[key] = category;
        }
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
}
