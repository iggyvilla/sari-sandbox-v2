using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Holds references to all shelf-editing UI elements and provides a single
/// helper to sync them all from a ShelfBuilder's current values — without
/// firing any OnValueChanged callbacks (uses SetWithoutNotify variants).
/// </summary>
public class ShelfEditGroupHandler : MonoBehaviour
{
    [Header("Input Fields")]
    public TMP_InputField shelfWidth;
    public TMP_InputField shelfLevels;
    public TMP_InputField distanceBetweenLevels;
    public TMP_InputField bootHeight;
    public TMP_InputField roofHeight;

    [Header("Options")]
    public SB_Segmented rotationY;
    public TMP_Dropdown itemSpawnOption;
    public SB_Segmented fridgeDoorStyle;

    [Tooltip("Door style row; only shown for fridges.")]
    public GameObject fridgeSection;

    [Header("Toggles - Spawn Shelves")]
    public Toggle spawnFrontShelf;
    public Toggle spawnBackShelf;
    public Toggle spawnLShelf;
    public Toggle spawnRShelf;
    
    [Header("Toggles - Fridge Options")]
    public Toggle spawnHingeDoors;
    
    [Header("Toggles - Items")]
    public Toggle spawnItems;
    public Toggle spawnPriceTags;
    
    [Header("Toggles - Fridge Roof Options")]
    public Toggle spawnLShelfRoof;
    public Toggle spawnRShelfRoof;
    public Toggle spawnBShelfRoof;
    public Toggle spawnFShelfRoof;

    [Header("Toggles - Spawn Walls")]
    public Toggle spawnFrontShelfWall;
    public Toggle spawnBackShelfWall;
    public Toggle spawnLShelfWall;
    public Toggle spawnRShelfWall;

    /// <summary>
    /// Pushes all values from <paramref name="shelf"/> into the UI elements
    /// without triggering their OnValueChanged callbacks.
    /// </summary>
    public void UpdateFromShelf(ShelfBuilder shelf, bool selectedShelfSpawnItem)
    {
        if (shelf == null) return;

        SetText(shelfWidth, shelf.shelfWidth.ToString());
        SetText(shelfLevels, shelf.shelfLevels.ToString());
        SetText(distanceBetweenLevels, shelf.distanceBetweenLevels.ToString());
        SetText(bootHeight, shelf.shelfBootHeight.ToString());
        SetText(roofHeight, shelf.shelfRoofHeight.ToString());

        SetValue(rotationY, RotationIndex(shelf.rotationY));
        SetValue(itemSpawnOption, (int)shelf.itemSpawnOption);
        SetValue(fridgeDoorStyle, (int)shelf.fridgeDoorStyle);
        if (fridgeSection != null) fridgeSection.SetActive(shelf.isFridge);

        SetOn(spawnItems, selectedShelfSpawnItem);
        SetOn(spawnPriceTags, shelf.spawnPriceTags);

        // Shelf face toggles
        SetOn(spawnFrontShelf, shelf.frontShelfConfig.buildShelves);
        SetOn(spawnBackShelf, shelf.backShelfConfig.buildShelves);
        SetOn(spawnLShelf, shelf.leftShelfConfig.buildShelves);
        SetOn(spawnRShelf, shelf.rightShelfConfig.buildShelves);

        SetOn(spawnHingeDoors, shelf.isFridge);

        // Roof configuration
        SetOn(spawnLShelfRoof, shelf.leftShelfConfig.buildShelfRoof);
        SetOn(spawnRShelfRoof, shelf.rightShelfConfig.buildShelfRoof);
        SetOn(spawnBShelfRoof, shelf.backShelfConfig.buildShelfRoof);
        SetOn(spawnFShelfRoof, shelf.frontShelfConfig.buildShelfRoof);

        // Wall toggles
        SetOn(spawnFrontShelfWall, shelf.frontShelfConfig.buildBackWall);
        SetOn(spawnBackShelfWall, shelf.backShelfConfig.buildBackWall);
        SetOn(spawnLShelfWall, shelf.leftShelfConfig.buildBackWall);
        SetOn(spawnRShelfWall, shelf.rightShelfConfig.buildBackWall);
    }

    /// <summary>rotationY dropdown index: 0°→0, 90°→1, 180°→2, 270°→3.</summary>
    public static int RotationIndex(float rotationY) => Mathf.RoundToInt(rotationY / 90f) % 4;

    // Unity-null-safe setters (`?.` skips Unity's destroyed/unassigned check).
    public static void SetText(TMP_InputField field, string text)
    {
        if (field != null) field.SetTextWithoutNotify(text);
    }

    public static void SetValue(TMP_Dropdown dropdown, int value)
    {
        if (dropdown != null) dropdown.SetValueWithoutNotify(value);
    }

    public static void SetValue(SB_Segmented segmented, int value)
    {
        if (segmented != null) segmented.SetValueWithoutNotify(value);
    }

    public static void SetOn(Toggle toggle, bool isOn)
    {
        if (toggle != null) toggle.SetIsOnWithoutNotify(isOn);
    }
}
