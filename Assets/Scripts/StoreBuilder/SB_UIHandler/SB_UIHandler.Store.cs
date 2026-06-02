using System.Collections.Generic;
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
        /* If the menu is already open, just close it and do no processing */
        if (loadStoreCanvas.activeInHierarchy)
        {
            loadStoreCanvas.SetActive(false);
            return;
        }

        loadStoreCanvas.SetActive(true);
        _validStoreFiles.Clear();
        loadStoreDropdown.ClearOptions();

        if (loadPersistentDataPathText != null)
            loadPersistentDataPathText.text = Application.persistentDataPath;

        string[] files = Directory.GetFiles(Application.persistentDataPath, "*.json");
        List<string> options = new();

        foreach (string file in files)
        {
            try
            {
                StoreData data = JsonConvert.DeserializeObject<StoreData>(File.ReadAllText(file));
                if (data == null || data.shelves == null) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                _validStoreFiles.Add(name);
                options.Add(name);
            }
            catch { }
        }

        loadStoreDropdown.AddOptions(options);
        loadStoreDropdown.interactable = options.Count > 0;
    }

    public void LoadStoreConfirm()
    {
        if (_validStoreFiles.Count == 0) return;
        int index = loadStoreDropdown.value;
        if (index < 0 || index >= _validStoreFiles.Count) return;

        DataHandler.Instance.storeName = _validStoreFiles[index];
        DataHandler.Instance.readSave  = true;
        DataHandler.Instance.LoadStore();
    }

    // -- Save store UI ---------------------------------------------------------

    public void OnSaveStoreMenuPressed()
    {
        if (savePersistentDataPathText != null)
            savePersistentDataPathText.text = Application.persistentDataPath;

        if (saveStoreCanvas.activeInHierarchy)
        {
            saveStoreCanvas.SetActive(false);
            return;
        }

        saveStoreCanvas.SetActive(true);
    }

    public void OnSaveStoreConfirmPressed()
    {
        SetStoreName(saveStoreTextField.text);
        DataHandler.Instance.SaveStore();
    }

    // -- Store dimensions UI ---------------------------------------------------

    public void OnEditStoreDimensionsPressed()
    {
        bool open = !storeDimensionsMenu.activeSelf;
        storeDimensionsMenu.SetActive(open);

        if (open)
        {
            GameObject floor = DataHandler.Instance.floor;
            if (floor != null)
            {
                storeWidthInput.SetTextWithoutNotify(floor.transform.localScale.x.ToString());
                storeDepthInput.SetTextWithoutNotify(floor.transform.localScale.z.ToString());

                var roomStructure = floor.GetComponent<RoomStructure>();
                if (roomStructure != null)
                    wallHeightInput.SetTextWithoutNotify(roomStructure.wallHeight.ToString());
            }
        }
    }

    public void OnStoreDimensionsApplyPressed()
    {
        GameObject floor = DataHandler.Instance.floor;
        if (floor == null) return;

        float width  = float.TryParse(storeWidthInput.text, out float w) && w > 0f ? w : floor.transform.localScale.x;
        float depth  = float.TryParse(storeDepthInput.text, out float d) && d > 0f ? d : floor.transform.localScale.z;

        var roomStructure = floor.GetComponent<RoomStructure>();
        if (roomStructure != null)
        {
            float wallH = float.TryParse(wallHeightInput.text, out float wh) && wh > 0f ? wh : roomStructure.wallHeight;
            roomStructure.wallHeight = wallH;
            roomStructure.SetFloorDimensions(width, depth);
        }
        else
        {
            Vector3 scale = floor.transform.localScale;
            scale.x = width;
            scale.z = depth;
            floor.transform.localScale = scale;
        }

        // Wall height may have changed - re-glue every aisle marker to the new ceiling.
        foreach (AisleMarker marker in FindObjectsByType<AisleMarker>(FindObjectsSortMode.None))
            marker.RefreshHeight();
    }

    public void OnSpawnIntoStorePressed()
    {
        DataHandler.Instance.SpawnIntoStore();
    }
}
