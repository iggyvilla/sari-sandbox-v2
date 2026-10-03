using UnityEngine;

public partial class SB_UIHandler
{
    private const float DefaultAisleCableLength = 0.5f;

    // Values applied to the next-spawned aisle marker; also mirror the selected
    // marker while one is being edited.
    private string _aisleCategory1 = "";
    private string _aisleCategory2 = "";
    private string _aisleCategory3 = "";
    private int    _aisleNumber = 1;
    private float  _aisleCableLength = DefaultAisleCableLength;
    private AisleMarker _selectedAisleMarker;

    // Applies the current text-field values to a marker. Called by
    // SB_InteractionController when a new aisle marker is placed.
    public void ApplyAisleMarkerSettings(AisleMarker marker)
    {
        if (marker == null) return;
        marker.BuildAisleMarker(_aisleCategory1, _aisleCategory2, _aisleCategory3,
            _aisleNumber, _aisleCableLength);
    }

    // Edits apply live, so the fields use OnValueChanged rather than OnEndEdit.
    void BindAisleInspector()
    {
        aisleCategory1Input.onValueChanged.AddListener(value => EditAisle(() => _aisleCategory1 = value));
        aisleCategory2Input.onValueChanged.AddListener(value => EditAisle(() => _aisleCategory2 = value));
        aisleCategory3Input.onValueChanged.AddListener(value => EditAisle(() => _aisleCategory3 = value));
        aisleNumberInput.onValueChanged.AddListener(value =>
            EditAisle(() => { if (int.TryParse(value, out int number)) _aisleNumber = number; }));
        aisleCableLengthInput.onValueChanged.AddListener(value =>
            EditAisle(() => { if (float.TryParse(value, out float length)) _aisleCableLength = Mathf.Max(0f, length); }));
    }

    // Pre-fills the inspector with the selected marker's current values.
    private void FillAisleInputs(AisleMarker marker)
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
    }

    // Updates the stored value, then re-applies all values to the selected marker and refreshes its selector box.
    private void EditAisle(System.Action setValue)
    {
        setValue();
        if (_selectedAisleMarker == null) return;

        ApplyAisleMarkerSettings(_selectedAisleMarker);
        if (_activePropSelector != null)
            _activePropSelector.Refit();
        MarkDirty();
    }
}
