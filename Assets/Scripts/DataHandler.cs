using UnityEngine;

[System.Serializable]
public class ItemCategories
{
    public ItemCategoryData[] Categories;
}

[System.Serializable]
public class ItemCategoryData
{
    public string Category;
    public string[] Items;
}

public enum ItemCategoryType
{
    Water,
    Soda,
    Juice,
    Dairies,
    Liquor,
    Biscuit,
    Can,
    Chips,
    Nuts,
    Soup,
    Noodles
}

// TODO: getCategoryIndexFromName, itemTags.json

public class DataHandler : MonoBehaviour
{

    public ItemCategories itemCategories = null;
    public static DataHandler Instance { get; private set; }
    
    void Awake()
    {
        // Assemble Singleton class
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        
        Debug.Log("Loading item categories...");
        TextAsset categoriesJson = Resources.Load<TextAsset>("Data/Categories");
        itemCategories = JsonUtility.FromJson<ItemCategories>(categoriesJson.text);
        Debug.Log("Done.");
    }
}
