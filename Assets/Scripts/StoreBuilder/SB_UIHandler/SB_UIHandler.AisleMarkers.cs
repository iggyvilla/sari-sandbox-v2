using UnityEngine;

public partial class SB_UIHandler
{
    // Applies the current text-field values to a marker. Called by
    // SB_InteractionController when a new aisle marker is placed.
    public void ApplyAisleMarkerSettings(AisleMarker marker)
    {
        if (marker == null) return;
        marker.BuildAisleMarker(_aisleCategory1, _aisleCategory2, _aisleCategory3,
            _aisleNumber, _aisleCableLength);
    }

    private void ShowAisleMarkerMenu(AisleMarker marker)
    {
        _selectedAisleMarker = marker;

        _aisleCategory1   = marker.Category1;
        _aisleCategory2   = marker.Category2;
        _aisleCategory3   = marker.Category3;
        _aisleNumber      = marker.AisleNumber;
        _aisleCableLength = marker.CableLength;

        ShelfEditGroupHandler.SetText(aisleCategory1Input, _aisleCategory1);
        ShelfEditGroupHandler.SetText(aisleCategory2Input, _aisleCategory2);
        ShelfEditGroupHandler.SetText(aisleCategory3Input, _aisleCategory3);
        ShelfEditGroupHandler.SetText(aisleNumberInput, _aisleNumber.ToString());
        ShelfEditGroupHandler.SetText(aisleCableLengthInput, _aisleCableLength.ToString());

        if (aisleMarkerMenu != null)
            aisleMarkerMenu.SetActive(true);
    }

    private void HideAisleMarkerMenu()
    {
        _selectedAisleMarker = null;
        if (aisleMarkerMenu != null)
            aisleMarkerMenu.SetActive(false);
    }

    // Re-applies the current values to the selected marker (live editing) and
    // refreshes its selector box.
    private void ApplyToSelectedAisleMarker()
    {
        if (_selectedAisleMarker == null) return;
        ApplyAisleMarkerSettings(_selectedAisleMarker);
        if (_activePropSelector != null)
            _activePropSelector.Refit();
    }

    // Wired to each aisle marker InputField's OnValueChanged event in the Inspector.
    public void OnAisleCategory1Changed(string value)
    {
        _aisleCategory1 = value;
        ApplyToSelectedAisleMarker();
    }

    public void OnAisleCategory2Changed(string value)
    {
        _aisleCategory2 = value;
        ApplyToSelectedAisleMarker();
    }

    public void OnAisleCategory3Changed(string value)
    {
        _aisleCategory3 = value;
        ApplyToSelectedAisleMarker();
    }

    public void OnAisleNumberChanged(string value)
    {
        if (int.TryParse(value, out int number))
            _aisleNumber = number;
        ApplyToSelectedAisleMarker();
    }

    public void OnAisleCableLengthChanged(string value)
    {
        if (float.TryParse(value, out float length))
            _aisleCableLength = Mathf.Max(0f, length);
        ApplyToSelectedAisleMarker();
    }
}
