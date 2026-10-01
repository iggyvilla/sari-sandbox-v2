using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public partial class SB_UIHandler : MonoBehaviour
{
    [Header("References")]
    public SB_InteractionController interactionController;

    [Header("Selection UI")]
    public GameObject shelfEditCanvas;
    public GameObject tooltipText;
    public GameObject itemCategorySelection;
    public TMP_Dropdown itemCategoryDropdown;

    [Header("Shelf Category UI")]
    public TMP_Dropdown shelfCategoryDropdown;

    // Default fallback values used when the user enters an invalid input
    private const int   DefaultShelfWidth            = 2;
    private const int   DefaultNumberOfLevels        = 3;
    private const float DefaultDistanceBetweenLevels = 0.4f;
    private const float DefaultShelfRoofHeight       = 0.4f;
    private const float DefaultBootHeight            = 0.4f;

    public TextMeshProUGUI selectedShelfText;

    private OutlineSelector _activeShelfSelector;
    private OutlineSelector _activePropSelector;
    private SubShelfMarker _activeSubShelf;

    public Toggle priceTagToggle;

    // Mirrors the spawn-items toggle so overflow suppression can be undone cleanly
    private bool _userWantsSpawnItems;

    public ShelfEditGroupHandler shelfEditGroupHandler;

    [Header("Load Store UI")]
    public GameObject loadStoreCanvas;
    public TMP_Dropdown loadStoreDropdown;
    public TextMeshProUGUI loadPersistentDataPathText;

    [Header("Save Store UI")]
    public GameObject saveStoreCanvas;
    public TMP_InputField saveStoreTextField;
    public TextMeshProUGUI savePersistentDataPathText;

    [Header("Store Dimensions UI")]
    public GameObject storeDimensionsMenu;
    public TMP_InputField storeWidthInput;
    public TMP_InputField storeDepthInput;
    public TMP_InputField wallHeightInput;
    public TMP_Dropdown streetWallDropdown;
    public TMP_Dropdown exitDoorTypeDropdown;
    public TMP_Dropdown exitDoorPositionDropdown;

    [Header("Agent Settings UI")]
    public GameObject agentSettingsMenu;
    public TMP_Dropdown agentAvatarSettingDropdown;
    public TMP_Dropdown agentInteractionStyleDropdown;
    public TMP_Dropdown agentBasketStyleDropdown;
    public TMP_Dropdown scanningDifficultyDropdown;

    [Header("Aisle Marker UI")]
    // Panel shown when an aisle marker is selected for editing
    public GameObject aisleMarkerMenu;
    public TMP_InputField aisleCategory1Input;
    public TMP_InputField aisleCategory2Input;
    public TMP_InputField aisleCategory3Input;
    public TMP_InputField aisleNumberInput;
    public TMP_InputField aisleCableLengthInput;

    private const float DefaultAisleCableLength = 0.5f;

    // Values applied to the next-spawned aisle marker; also mirror the selected
    // marker while one is being edited.
    private string _aisleCategory1 = "";
    private string _aisleCategory2 = "";
    private string _aisleCategory3 = "";
    private int    _aisleNumber = 1;
    private float  _aisleCableLength = DefaultAisleCableLength;
    private AisleMarker _selectedAisleMarker;

    private List<string> _validStoreFiles = new();

    void Start()
    {
        UpdateSelectedShelfText();
        FillEnumDropdown<ItemCategory>(shelfCategoryDropdown);
        FillEnumDropdown<ItemCategory>(itemCategoryDropdown);
        FillEnumDropdown<WallSide>(streetWallDropdown);
        FillEnumDropdown<ExitDoorType>(exitDoorTypeDropdown);
        FillEnumDropdown<ExitDoorPosition>(exitDoorPositionDropdown);
        PopulateAgentSettingsDropdowns();
    }

    // Replaces a dropdown's options with the names of enum T.
    static void FillEnumDropdown<T>(TMP_Dropdown dropdown) where T : Enum
    {
        if (dropdown == null) return;
        dropdown.ClearOptions();
        dropdown.AddOptions(new List<string>(Enum.GetNames(typeof(T))));
    }

    // Flips a menu's visibility and returns whether it is now open.
    static bool ToggleMenu(GameObject menu)
    {
        bool open = !menu.activeSelf;
        menu.SetActive(open);
        return open;
    }
}
