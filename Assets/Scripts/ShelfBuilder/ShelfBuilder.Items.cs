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

        string categoryKey = $"{subShelfId}_{subSubShelfId}";
        ItemCategory category = debugForceItemCategory
            ? forcedCategory
            : subShelfCategories.TryGetValue(categoryKey, out var configuredCategory) ? configuredCategory : default;

        spawner.Init(
            distanceBetweenLevels,
            itemSpawnOption,
            spawnPriceTags,
            category,
            airMaterial,
            priceTagPrefab,
            shelfInfo
        );

        shelfObjects.Add(shelf);
    }

    public void SpawnItemsOnAllShelves()
    {
        if (!spawnItems) return;

        foreach (GameObject shelf in shelfObjects)
        {
            ItemSpawner spawner = shelf.GetComponent<ItemSpawner>();
            spawner.SpawnProducts();
        }
    }

    public void DespawnShelfItems()
    {
        foreach (ItemBBoxInfo bboxInfo in GetComponentsInChildren<ItemBBoxInfo>())
        {
            bboxInfo.DeleteItem();
        }
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
    }

    public static void DespawnAllItemsInScene()
    {
        GPUInstanceTracker.Instance.DespawnAllItems();
        DeleteAllPriceTags();
    }
}
