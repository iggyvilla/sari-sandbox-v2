using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public partial class SB_UIHandler
{
    // Updates the store name used for save/load file paths
    public void SetStoreName(string name)
    {
        DataHandler.Instance.storeName = name;
    }

    // -- Load store UI ---------------------------------------------------------

    public void LoadStoreDropdownMenu()
    {
        if (!ToggleMenu(loadStoreCanvas)) return;

        _validStoreFiles.Clear();
        loadStoreDropdown.ClearOptions();

        if (loadPersistentDataPathText != null)
            loadPersistentDataPathText.text = Application.persistentDataPath;

        foreach (string file in Directory.GetFiles(Application.persistentDataPath, "*.json"))
        {
            if (IsStoreFile(file))
                _validStoreFiles.Add(Path.GetFileNameWithoutExtension(file));
        }
        _validStoreFiles.Sort();

        loadStoreDropdown.AddOptions(_validStoreFiles);
        loadStoreDropdown.interactable = _validStoreFiles.Count > 0;
    }

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

    public void LoadStoreConfirm()
    {
        int index = loadStoreDropdown.value;
        if (index < 0 || index >= _validStoreFiles.Count) return;

        // The load destroys every store object, so drop anything that still points at them.
        interactionController.AbortPlacement();
        ClearSelection();

        DataHandler.Instance.storeName = _validStoreFiles[index];
        DataHandler.Instance.readSave  = true;
        DataHandler.Instance.LoadStore();
    }

    // -- Save store UI ---------------------------------------------------------

    public void OnSaveStoreMenuPressed()
    {
        if (savePersistentDataPathText != null)
            savePersistentDataPathText.text = Application.persistentDataPath;

        ToggleMenu(saveStoreCanvas);
    }

    public void OnSaveStoreConfirmPressed()
    {
        SetStoreName(saveStoreTextField.text);
        DataHandler.Instance.SaveStore();
    }

    // -- Store dimensions UI ---------------------------------------------------

    public void OnEditStoreDimensionsPressed()
    {
        GameObject floor = DataHandler.Instance.floor;
        if (!ToggleMenu(storeDimensionsMenu) || floor == null) return;

        RefreshStoreDimensionsUI();
    }

    public void OnStoreDimensionsApplyPressed()
    {
        DataHandler data = DataHandler.Instance;
        if (data.floor == null) return;

        Vector3 scale = data.floor.transform.localScale;
        data.ApplyStoreDimensions(
            ParsePositive(storeWidthInput.text, scale.x),
            ParsePositive(storeDepthInput.text, scale.z),
            ParsePositive(wallHeightInput.text, data.WallHeight));
        RefreshStoreDimensionsUI();
    }

    // Street changes apply live and never need the Apply button.
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
        RefreshStoreDimensionsUI();
    }

    // Shows what the store really uses: sizes are clamped to the wall minimums (a bigger exit door grows the store).
    void RefreshStoreDimensionsUI()
    {
        DataHandler data = DataHandler.Instance;
        Vector3 scale = data.floor.transform.localScale;
        storeWidthInput.SetTextWithoutNotify(scale.x.ToString());
        storeDepthInput.SetTextWithoutNotify(scale.z.ToString());
        wallHeightInput.SetTextWithoutNotify(data.WallHeight.ToString());

        StreetSettings street = data.Street;
        ShelfEditGroupHandler.SetValue(streetWallDropdown, (int)street.wall);
        ShelfEditGroupHandler.SetValue(exitDoorTypeDropdown, (int)street.door);
        ShelfEditGroupHandler.SetValue(exitDoorPositionDropdown, (int)street.position);
    }

    public void OnSpawnIntoStorePressed()
    {
        DataHandler.Instance.SpawnIntoStore();
    }
}
