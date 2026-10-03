using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

public partial class SB_UIHandler
{
    // The floor is a 10 m plane scaled per axis (RoomStructure halves it as `scale * 5`), so 0.9 is a 9 m wall.
    private const float FloorMeters = 10f;

    private string _selectedStore;
    private readonly List<SB_StoreRow> _storeRows = new();

    // Updates the store name used for save/load file paths
    public void SetStoreName(string name)
    {
        DataHandler.Instance.storeName = name;
    }

    void BindDialogs()
    {
        storesCloseButton.onClick.AddListener(CloseDialogs);
        storesScrimButton.onClick.AddListener(CloseDialogs);
        settingsCloseButton.onClick.AddListener(CloseDialogs);
        settingsScrimButton.onClick.AddListener(CloseDialogs);

        storeSearchInput.onValueChanged.AddListener(_ => RefreshStoreList());
        openStoreButton.onClick.AddListener(LoadStoreConfirm);
        saveStoreTextField.onValueChanged.AddListener(_ => RefreshSaveControls());
        saveStoreButton.onClick.AddListener(OnSaveStoreConfirmPressed);
        showFolderButton.onClick.AddListener(OnShowSavesFolderPressed);

        for (int i = 0; i < settingsTabButtons.Length; i++)
        {
            int tab = i;
            settingsTabButtons[i].onClick.AddListener(() => OnSettingsTabChanged(tab));
        }
        OnSettingsTabChanged(0);
        BindAgentSettings();
        BindRendering();
    }

    void CloseDialogs()
    {
        storesDialog.Hide();
        settingsDialog.Hide();
    }

    // -- Top bar ---------------------------------------------------------------

    void OnStoreNameEdited(string value)
    {
        string name = value.Trim();
        if (IsValidStoreName(name) && name != DataHandler.Instance.storeName)
        {
            SetStoreName(name);
            MarkDirty();
        }
        storeNameInput.SetTextWithoutNotify(DataHandler.Instance.storeName);
    }

    void OnQuickSavePressed()
    {
        DataHandler.Instance.SaveStore();
        ClearDirty();
    }

    // -- Stores dialog (open + save) -------------------------------------------

    void OpenStoresDialog()
    {
        settingsDialog.Hide();
        saveStoreTextField.SetTextWithoutNotify(DataHandler.Instance.storeName);
        storeSearchInput.SetTextWithoutNotify("");
        RefreshStoreList();
        RefreshSaveControls();
        storesDialog.Show();
    }

    static string StorePathOf(string name) => Path.Combine(Application.persistentDataPath, name + ".json");

    static bool IsValidStoreName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    // Newest first; only files that look like a store are listed.
    void RefreshStoreList()
    {
        foreach (SB_StoreRow row in _storeRows) Destroy(row.gameObject);
        _storeRows.Clear();

        string filter = storeSearchInput.text.Trim();
        List<(string name, DateTime modified)> stores = Directory.GetFiles(Application.persistentDataPath, "*.json")
            .Where(IsStoreFile)
            .Select(file => (Path.GetFileNameWithoutExtension(file), File.GetLastWriteTime(file)))
            .Where(store => store.Item1.Length > 0 && store.Item1.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(store => store.Item2)
            .ToList();

        foreach ((string name, DateTime modified) in stores)
        {
            SB_StoreRow row = Instantiate(storeRowTemplate, storeListRoot);
            row.gameObject.SetActive(true);
            row.nameText.text = name;
            row.whenText.text = FormatAge(modified);
            row.button.onClick.AddListener(() => SelectStore(name));
            _storeRows.Add(row);
        }

        if (!stores.Any(store => store.name == _selectedStore))
            _selectedStore = stores.Count > 0 ? stores[0].name : null;
        SelectStore(_selectedStore);

        storeListEmptyText.gameObject.SetActive(stores.Count == 0);
        storeListEmptyText.text = filter.Length > 0 ? "No stores match your search." : "No saved stores yet. Name this one and press Save.";
    }

    void SelectStore(string name)
    {
        _selectedStore = name;
        foreach (SB_StoreRow row in _storeRows)
            row.fx.SetSelected(row.nameText.text == name);
        openStoreButton.interactable = name != null;
        openStoreLabel.text = name != null ? $"Open {name}" : "Open";
    }

    static string FormatAge(DateTime when)
    {
        double days = (DateTime.Now - when).TotalDays;
        if (days < 1) return "Today";
        if (days < 2) return "Yesterday";
        return days < 7 ? $"{(int)days} days ago" : when.ToString("d MMM yyyy");
    }

    // Overwriting an existing store turns the Save button into an amber "Overwrite" button.
    void RefreshSaveControls()
    {
        string name = saveStoreTextField.text.Trim();
        bool valid = IsValidStoreName(name);
        bool exists = valid && File.Exists(StorePathOf(name));

        overwriteWarning.SetActive(exists);
        overwriteWarningText.text = $"“{name}” already exists. Saving will overwrite it.";
        saveStoreLabel.text = exists ? $"Overwrite {name}" : "Save";
        saveStoreButton.interactable = valid;

        SB_Theme theme = saveStoreFx.theme;
        Color baseColor = exists ? theme.warn : theme.accent;
        saveStoreFx.SetColors(baseColor, Color.Lerp(baseColor, Color.white, 0.2f), Color.Lerp(baseColor, Color.black, 0.15f));
    }

    void OnSaveStoreConfirmPressed()
    {
        string name = saveStoreTextField.text.Trim();
        if (!IsValidStoreName(name)) return;

        SetStoreName(name);
        storeNameInput.SetTextWithoutNotify(name);
        DataHandler.Instance.SaveStore();
        ClearDirty();
        CloseDialogs();
    }

    void OnShowSavesFolderPressed() => Application.OpenURL(new Uri(Application.persistentDataPath).AbsoluteUri);

    // Streams only up to a top-level "shelves" key, so large shelfItems blocks are never materialized.
    static bool IsStoreFile(string path)
    {
        try
        {
            using JsonTextReader reader = new(File.OpenText(path));
            if (!reader.Read() || reader.TokenType != JsonToken.StartObject) return false;

            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                if ((string)reader.Value == "shelves") return true;
                reader.Skip();
            }
        }
        catch (System.Exception error)
        {
            Debug.LogWarning($"Skipping unreadable store file {path}: {error.Message}");
        }

        return false;
    }

    void LoadStoreConfirm()
    {
        if (_selectedStore == null) return;

        // The load destroys every store object, so drop anything that still points at them.
        interactionController.AbortPlacement();
        ClearSelection();

        DataHandler.Instance.storeName = _selectedStore;
        DataHandler.Instance.readSave  = true;
        DataHandler.Instance.LoadStore();

        storeNameInput.SetTextWithoutNotify(_selectedStore);
        ClearDirty();
        CloseDialogs();
        RefreshStoreInspector();
        RefreshStatus();
    }

    // -- Store inspector -------------------------------------------------------

    void BindStoreInspector()
    {
        storeWidthInput.onEndEdit.AddListener(_ => OnStoreDimensionsChanged());
        storeDepthInput.onEndEdit.AddListener(_ => OnStoreDimensionsChanged());
        wallHeightInput.onEndEdit.AddListener(_ => OnStoreDimensionsChanged());

        // Street changes apply live, like everything else in the inspector.
        streetWallSegment.onValueChanged.AddListener(OnStreetWallChanged);
        exitDoorTypeSegment.onValueChanged.AddListener(OnExitDoorTypeChanged);
        exitDoorPositionSegment.onValueChanged.AddListener(OnExitDoorPositionChanged);
    }

    // Sizes are typed in meters; an invalid entry keeps the current value.
    void OnStoreDimensionsChanged()
    {
        DataHandler data = DataHandler.Instance;
        if (data.floor == null) return;

        Vector3 scale = data.floor.transform.localScale;
        float width = ParsePositive(storeWidthInput.text, scale.x * FloorMeters) / FloorMeters;
        float depth = ParsePositive(storeDepthInput.text, scale.z * FloorMeters) / FloorMeters;
        float wallHeight = ParsePositive(wallHeightInput.text, data.WallHeight);

        bool changed = !Mathf.Approximately(width, scale.x) || !Mathf.Approximately(depth, scale.z) ||
                       !Mathf.Approximately(wallHeight, data.WallHeight);
        if (changed)
        {
            data.ApplyStoreDimensions(width, depth, wallHeight);
            MarkDirty();
        }
        RefreshStoreInspector();
        RefreshStatus();
    }

    public void OnStreetWallChanged(int index) => EditStreet(street => street.wall = (WallSide)index);
    public void OnExitDoorTypeChanged(int index) => EditStreet(street => street.door = (ExitDoorType)index);
    public void OnExitDoorPositionChanged(int index) => EditStreet(street => street.position = (ExitDoorPosition)index);

    void EditStreet(System.Action<StreetSettings> edit)
    {
        DataHandler data = DataHandler.Instance;
        if (data.floor == null) return;

        StreetSettings street = data.Street;
        edit(street);
        data.ApplyStreetSettings(street);
        MarkDirty();
        RefreshStoreInspector();
    }

    // Shows what the store really uses: sizes are clamped to the wall minimums (a bigger exit door grows the store).
    void RefreshStoreInspector()
    {
        DataHandler data = DataHandler.Instance;
        if (data.floor == null) return;

        Vector3 scale = data.floor.transform.localScale;
        storeWidthInput.SetTextWithoutNotify((scale.x * FloorMeters).ToString("0.##"));
        storeDepthInput.SetTextWithoutNotify((scale.z * FloorMeters).ToString("0.##"));
        wallHeightInput.SetTextWithoutNotify(data.WallHeight.ToString("0.##"));

        StreetSettings street = data.Street;
        streetWallSegment.SetValueWithoutNotify((int)street.wall);
        exitDoorTypeSegment.SetValueWithoutNotify((int)street.door);
        exitDoorPositionSegment.SetValueWithoutNotify((int)street.position);
    }

    public void OnSpawnIntoStorePressed()
    {
        DataHandler.Instance.SpawnIntoStore();
    }
}
