using UnityEngine;

// Tool palette, placing banner, which inspector is visible, and the status bar.
public partial class SB_UIHandler
{
    enum InspectorKind { None, Store, Shelf, SubShelf, Aisle, Prop }

    private BuilderTool _shownTool = (BuilderTool)(-1);
    private string _shownTitle;

    // Polled because the controller changes tool and placement state from many places (hotkeys, clicks, Esc).
    void UpdateToolState()
    {
        BuilderTool tool = interactionController.ActiveTool;
        string title = interactionController.PlacementTitle;
        if (tool == _shownTool && title == _shownTitle) return;
        _shownTool = tool;
        _shownTitle = title;

        foreach (ToolButton entry in toolButtons)
            entry.fx.SetSelected(entry.tool == tool);

        if (title != null)
        {
            placingTitleText.text = title;
            placingHintText.text = interactionController.PlacementHint;
            placingRotateHint.SetActive(tool != BuilderTool.EmergencyExit);
            placingCancelText.text = interactionController.CancelLabel;
            placingBanner.Show();
        }
        else
        {
            placingBanner.Hide();
        }
        RefreshInspector();
    }

    InspectorKind CurrentInspector()
    {
        if (interactionController.IsPlacing) return InspectorKind.None;
        if (_selectedAisleMarker != null) return InspectorKind.Aisle;
        if (_activeShelfSelector != null) return InspectorKind.Shelf;
        if (_activeSubShelf != null) return InspectorKind.SubShelf;
        if (_activePropSelector != null) return InspectorKind.Prop;
        return InspectorKind.Store;
    }

    // Exactly one inspector is open at a time; the others fade out.
    void RefreshInspector()
    {
        if (storeInspector == null) return;
        InspectorKind kind = CurrentInspector();
        if (kind == InspectorKind.Store && !storeInspector.IsOpen) RefreshStoreInspector();

        storeInspector.Set(kind == InspectorKind.Store);
        shelfInspector.Set(kind == InspectorKind.Shelf);
        subShelfInspector.Set(kind == InspectorKind.SubShelf);
        aisleInspector.Set(kind == InspectorKind.Aisle);
        propInspector.Set(kind == InspectorKind.Prop);

        statusHintText.text = StatusHint(kind);
    }

    string StatusHint(InspectorKind kind)
    {
        if (interactionController.IsPlacing)
        {
            string rotate = interactionController.ActiveTool == BuilderTool.EmergencyExit ? "" : "R rotate · ";
            return $"{interactionController.PlacementHint} · {rotate}Esc {interactionController.CancelLabel.ToLowerInvariant()}";
        }

        return kind == InspectorKind.Store || kind == InspectorKind.SubShelf
            ? "Click a shelf, fixture or marker to select it"
            : "M move · R rotate · D duplicate · Del delete";
    }

    // -- Status bar and "ready to play" list ----------------------------------

    public void MarkDirty() => unsavedChip.SetActive(true);

    void ClearDirty() => unsavedChip.SetActive(false);

    void RefreshStatus()
    {
        int shelves = 0, fridges = 0;
        foreach (ShelfBuilder shelf in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
        {
            if (shelf.isFridge) fridges++;
            else shelves++;
        }
        int checkouts = FindObjectsByType<SelfCheckoutMarker>(FindObjectsSortMode.None).Length;
        bool hasSpawn = FindFirstObjectByType<AgentSpawnMarker>() != null;

        GameObject floor = DataHandler.Instance.floor;
        string size = floor != null
            ? $"{floor.transform.localScale.x * FloorMeters:0.##} × {floor.transform.localScale.z * FloorMeters:0.##} m"
            : "No floor";
        statusInfoText.text = $"{size} · {Plural(shelves, "shelf", "shelves")} · {Plural(fridges, "fridge", "fridges")} · v{Application.version}";

        readyDoorRow.Set(true, "Entrance door");
        readyFixturesRow.Set(shelves + fridges > 0,
            shelves + fridges > 0
                ? $"{Plural(shelves, "shelf", "shelves")}, {Plural(fridges, "fridge", "fridges")}, {Plural(checkouts, "self-checkout", "self-checkouts")}"
                : "No shelves or fridges yet");
        readyAgentRow.Set(hasSpawn, hasSpawn ? "Agent spawn placed" : "Agent spawn not placed");
        placeAgentSpawnButton.gameObject.SetActive(!hasSpawn);
    }

    static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
