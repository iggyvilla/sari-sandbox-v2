using UnityEngine;

[System.Serializable]
public class ItemCategories
{
    public ItemCategory[] Categories;
}

[System.Serializable]
public class ItemCategory
{
    public string Category;
    public string[] Items;
}


// TODO: getCategoryIndexFromName, itemTags.json

public class DataHandler : MonoBehaviour
{

    public ItemCategories itemCategories;
    
    void Awake()
    {
        Debug.Log("Loading item categories...");
        TextAsset categoriesJson = Resources.Load<TextAsset>("Data/Categories");
        itemCategories = JsonUtility.FromJson<ItemCategories>(categoriesJson.text);
        Debug.Log("Done.");
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
