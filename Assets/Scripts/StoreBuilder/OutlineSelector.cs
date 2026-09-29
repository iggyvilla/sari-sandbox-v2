using UnityEngine;

/// <summary>Layer names and ray length shared by the store builder's raycasts.</summary>
public static class StoreBuilderLayers
{
    public const string Interactable = "SariInteractable";
    public const string Shelf = "SariShelf";
    public const string Floor = "SariFloor";
    public const float RayLength = 100f;
}

/// <summary>
/// Invisible outline box over a store-builder object (shelf or prop). SB_InteractionController
/// routes clicks and R/M/D to it; the outline shows while it is selected.
/// </summary>
public class OutlineSelector : MonoBehaviour
{
    /// <summary>The object this box wraps.</summary>
    public GameObject Target { get; private set; }

    /// <summary>The wrapped shelf, or null when the target is a prop.</summary>
    public ShelfBuilder Shelf { get; private set; }

    private OutlineFx.OutlineFx _outlineFx;

    public void Select() => _outlineFx.enabled = true;
    public void Deselect() => _outlineFx.enabled = false;
    public bool IsSelected => _outlineFx.enabled;

    /// <summary>Fits this box around every renderer under the target.</summary>
    public void Refit()
    {
        Bounds bounds = GetCombinedBounds(Target);
        bounds.Expand(0.01f);
        transform.position = bounds.center;
        transform.localScale = bounds.size;
    }

    public void DestroyWithTarget()
    {
        Destroy(Target);
        Destroy(gameObject);
    }

    /// <summary>Creates a trigger cube on the interactable layer wrapped around <paramref name="target"/>.</summary>
    public static OutlineSelector Create(Material material, GameObject target)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = $"{target.name} Selector";
        cube.layer = LayerMask.NameToLayer(StoreBuilderLayers.Interactable);
        cube.GetComponent<Renderer>().sharedMaterial = material;
        // Keep selection bounds raycastable without interfering with physics.
        cube.GetComponent<BoxCollider>().isTrigger = true;

        OutlineSelector selector = cube.AddComponent<OutlineSelector>();
        selector._outlineFx = cube.AddComponent<OutlineFx.OutlineFx>();
        selector._outlineFx.enabled = false;
        selector.Target = target;
        selector.Shelf = target.GetComponent<ShelfBuilder>();
        if (selector.Shelf != null) selector.Shelf.Selector = selector;
        selector.Refit();
        return selector;
    }

    private static Bounds GetCombinedBounds(GameObject parent)
    {
        Renderer[] renderers = parent.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(parent.transform.position, Vector3.zero);

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }
}
