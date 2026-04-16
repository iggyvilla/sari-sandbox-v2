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

    [Header("Dropdowns")]
    public TMP_Dropdown rotationY;
    public TMP_Dropdown itemSpawnOption;

    [Header("Toggles - Spawn Shelves")]
    public Toggle spawnFrontShelf;
    public Toggle spawnBackShelf;
    public Toggle spawnLShelf;
    public Toggle spawnRShelf;

    [Header("Toggles - Spawn Walls")]
    public Toggle spawnFrontShelfWall;
    public Toggle spawnBackShelfWall;
    public Toggle spawnLShelfWall;
    public Toggle spawnRShelfWall;

    /// <summary>
    /// Pushes all values from <paramref name="shelf"/> into the UI elements
    /// without triggering their OnValueChanged callbacks.
    /// </summary>
    public void UpdateFromShelf(ShelfBuilder shelf)
    {
        if (shelf == null) return;

        // Input fields
        shelfWidth?.SetTextWithoutNotify(shelf.shelfWidth.ToString());
        shelfLevels?.SetTextWithoutNotify(shelf.shelfLevels.ToString());
        distanceBetweenLevels?.SetTextWithoutNotify(shelf.distanceBetweenLevels.ToString());
        bootHeight?.SetTextWithoutNotify(shelf.shelfBootHeight.ToString());

        // Dropdowns
        // rotationY: 0°→0, 90°→1, 180°→2, 270°→3
        rotationY?.SetValueWithoutNotify(Mathf.RoundToInt(shelf.rotationY / 90f) % 4);
        itemSpawnOption?.SetValueWithoutNotify((int)shelf.itemSpawnOption);

        // Shelf face toggles
        spawnFrontShelf?.SetIsOnWithoutNotify(shelf.frontShelfConfig.buildShelves);
        spawnBackShelf?.SetIsOnWithoutNotify(shelf.backShelfConfig.buildShelves);
        spawnLShelf?.SetIsOnWithoutNotify(shelf.leftShelfConfig.buildShelves);
        spawnRShelf?.SetIsOnWithoutNotify(shelf.rightShelfConfig.buildShelves);

        // Wall toggles
        spawnFrontShelfWall?.SetIsOnWithoutNotify(shelf.frontShelfConfig.buildBackWall);
        spawnBackShelfWall?.SetIsOnWithoutNotify(shelf.backShelfConfig.buildBackWall);
        spawnLShelfWall?.SetIsOnWithoutNotify(shelf.leftShelfConfig.buildBackWall);
        spawnRShelfWall?.SetIsOnWithoutNotify(shelf.rightShelfConfig.buildBackWall);
    }
}
