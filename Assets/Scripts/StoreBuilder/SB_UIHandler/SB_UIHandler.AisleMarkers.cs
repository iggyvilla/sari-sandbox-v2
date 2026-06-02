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

        aisleCategory1Input?.SetTextWithoutNotify(_aisleCategory1);
        aisleCategory2Input?.SetTextWithoutNotify(_aisleCategory2);
        aisleCategory3Input?.SetTextWithoutNotify(_aisleCategory3);
        aisleNumberInput?.SetTextWithoutNotify(_aisleNumber.ToString());
        aisleCableLengthInput?.SetTextWithoutNotify(_aisleCableLength.ToString());

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
        _selectedAisleMarker.BuildAisleMarker(_aisleCategory1, _aisleCategory2,
            _aisleCategory3, _aisleNumber, _aisleCableLength);
        if (_activePropSelector != null)
            _activePropSelector.EncapsulateProp(_selectedAisleMarker.gameObject);
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
        if (!int.TryParse(value, out int result))
            result = _aisleNumber;
        _aisleNumber = result;
        ApplyToSelectedAisleMarker();
    }

    public void OnAisleCableLengthChanged(string value)
    {
        if (!float.TryParse(value, out float result))
            result = _aisleCableLength;
        _aisleCableLength = Mathf.Max(0f, result);
        ApplyToSelectedAisleMarker();
    }
}
