using UnityEngine;

public partial class ShelfBuilder
{
    void InitializeItemSpawner(GameObject shelf, int subShelfId, int subSubShelfId)
    {
        ItemSpawner spawner = shelf.GetComponent<ItemSpawner>();
        ShelfInfo shelfInfo = new ShelfInfo
        {
            shelfId = shelfId,
            subShelfId = subShelfId,
            subSubShelfId = subSubShelfId,
        };

        ItemCategory category = debugForceItemCategory
            ? forcedCategory
            : subShelfCategories.TryGetValue(CategoryKey(shelfInfo), out var configuredCategory) ? configuredCategory : default;

        if (spawner == null) return;

        spawner.Init(
            distanceBetweenLevels,
            itemSpawnOption,
            spawnPriceTags,
            category,
            airMaterial,
            priceTagPrefab,
            shelfInfo,
            isFridge
        );

        _itemSpawners.Add(spawner);
    }

    /// <summary>Key into <see cref="subShelfCategories"/> for a sub-shelf.</summary>
    public static string CategoryKey(ShelfInfo info) => $"{info.subShelfId}_{info.subSubShelfId}";

    public void SpawnItemsOnAllShelves()
    {
        if (!spawnItems) return;

        foreach (ItemSpawner spawner in _itemSpawners)
            spawner.SpawnProducts();
    }

    // Shelf bboxes live under NearbyItemBBoxManager, so clearing each spawner's records is enough.
    public void DespawnShelfItems()
    {
        NearbyItemBBoxManager manager = NearbyItemBBoxManager.TryGetInstance();
        if (manager == null) return;

        foreach (ItemSpawner spawner in GetComponentsInChildren<ItemSpawner>())
            manager.ClearOwner(spawner, removeGpuInstances: true);
    }

    public static void DeleteAllPriceTags()
    {
        PriceTag[] instances = FindObjectsByType<PriceTag>(FindObjectsSortMode.None);
        foreach (PriceTag instance in instances)
        {
            Destroy(instance.gameObject);
        }

        BakedPriceTag[] bakedInstances = FindObjectsByType<BakedPriceTag>(FindObjectsSortMode.None);
        foreach (BakedPriceTag instance in bakedInstances)
        {
            Destroy(instance.gameObject);
        }

        BakedPriceTag.ReleaseCachedSprites();
    }

    public static void DespawnAllItemsInScene()
    {
        NearbyItemBBoxManager.TryGetInstance()?.ClearAllVirtualBBoxes(removeGpuInstances: false);
        GPUInstanceTracker.Instance.DespawnAllItems();
        DeleteAllPriceTags();
    }
}
