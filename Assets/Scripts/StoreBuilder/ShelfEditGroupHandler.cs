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

    [Header("Dropdowns")]
    public TMP_Dropdown rotationY;
    public TMP_Dropdown itemSpawnOption;
    public TMP_Dropdown fridgeDoorStyle;

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

        // Input fields
        shelfWidth?.SetTextWithoutNotify(shelf.shelfWidth.ToString());
        shelfLevels?.SetTextWithoutNotify(shelf.shelfLevels.ToString());
        distanceBetweenLevels?.SetTextWithoutNotify(shelf.distanceBetweenLevels.ToString());
        bootHeight?.SetTextWithoutNotify(shelf.shelfBootHeight.ToString());
        roofHeight?.SetTextWithoutNotify(shelf.shelfRoofHeight.ToString());

        // Dropdowns
        // rotationY: 0°→0, 90°→1, 180°→2, 270°→3
        rotationY?.SetValueWithoutNotify(Mathf.RoundToInt(shelf.rotationY / 90f) % 4);
        itemSpawnOption?.SetValueWithoutNotify((int)shelf.itemSpawnOption);
        fridgeDoorStyle?.SetValueWithoutNotify((int)shelf.fridgeDoorStyle);

        spawnItems?.SetIsOnWithoutNotify(selectedShelfSpawnItem);
        spawnPriceTags?.SetIsOnWithoutNotify(shelf.spawnPriceTags);
        
        // Shelf face toggles
        spawnFrontShelf?.SetIsOnWithoutNotify(shelf.frontShelfConfig.buildShelves);
        spawnBackShelf?.SetIsOnWithoutNotify(shelf.backShelfConfig.buildShelves);
        spawnLShelf?.SetIsOnWithoutNotify(shelf.leftShelfConfig.buildShelves);
        spawnRShelf?.SetIsOnWithoutNotify(shelf.rightShelfConfig.buildShelves);
        
        // Hinge door toggle
        spawnHingeDoors?.SetIsOnWithoutNotify(shelf.spawnHingeDoors);
        
        // Roof configuration
        spawnLShelfRoof?.SetIsOnWithoutNotify(shelf.leftShelfConfig.buildShelfRoof);
        spawnRShelfRoof?.SetIsOnWithoutNotify(shelf.rightShelfConfig.buildShelfRoof);
        spawnBShelfRoof?.SetIsOnWithoutNotify(shelf.backShelfConfig.buildShelfRoof);
        spawnFShelfRoof?.SetIsOnWithoutNotify(shelf.frontShelfConfig.buildShelfRoof);

        // Wall toggles
        spawnFrontShelfWall?.SetIsOnWithoutNotify(shelf.frontShelfConfig.buildBackWall);
        spawnBackShelfWall?.SetIsOnWithoutNotify(shelf.backShelfConfig.buildBackWall);
        spawnLShelfWall?.SetIsOnWithoutNotify(shelf.leftShelfConfig.buildBackWall);
        spawnRShelfWall?.SetIsOnWithoutNotify(shelf.rightShelfConfig.buildBackWall);
    }
}
