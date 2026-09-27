using UnityEngine;

public class SB_InteractionController : MonoBehaviour
{
    [Header("Camera Rotation")]
    public float rotationSpeed = 90f; // degrees per second
    public float zoomSpeed = 5f;
    public float minOrthoSize = 2f;
    public float maxOrthoSize = 30f;

    [Header("Placement")]
    public float builderGridSize = 1f;

    [Header("References")]
    public SB_UIHandler uiHandler;
    public DataHandler dataHandler;
    public Material airMaterial;

    private bool _placementMode;
    private GameObject _placingPrefab; // spawned on the first floor hit; null when moving or duplicating
    private GameObject _previewObject;
    private OutlineSelector _moving;   // selector of an existing object being moved
    private bool _isDuplicatePlacement;
    private Camera _cam;
    private LayerMask _floorMask;
    private LayerMask _shelfMask;
    private LayerMask _interactableMask;

    void Awake()
    {
        _cam = GetComponentInChildren<Camera>();
        if (_cam == null)
            _cam = Camera.main;

        _floorMask = LayerMask.GetMask(StoreBuilderLayers.Floor);
        _shelfMask = LayerMask.GetMask(StoreBuilderLayers.Shelf);
        _interactableMask = LayerMask.GetMask(StoreBuilderLayers.Interactable);
    }

    void Update()
    {
        HandleCamera();

        if (_placementMode)
            HandlePlacement();
        else
            HandleSelection();
    }

    // ── Camera ────────────────────────────────────────────────────────────────

    void HandleCamera()
    {
        float rotInput = Axis(KeyCode.LeftArrow, KeyCode.RightArrow);
        if (rotInput != 0f)
            transform.RotateAround(transform.parent.position, Vector3.up, rotInput * rotationSpeed * Time.deltaTime);

        float zoomInput = Axis(KeyCode.DownArrow, KeyCode.UpArrow);
        if (zoomInput != 0f)
            _cam.orthographicSize = Mathf.Clamp(_cam.orthographicSize + zoomInput * zoomSpeed * Time.deltaTime, minOrthoSize, maxOrthoSize);
    }

    static float Axis(KeyCode positive, KeyCode negative) =>
        (StoreBuilderInput.Key(positive) ? 1f : 0f) - (StoreBuilderInput.Key(negative) ? 1f : 0f);

    // ── Selection ─────────────────────────────────────────────────────────────

    void HandleSelection()
    {
        OutlineSelector selected = uiHandler.ActiveSelector;
        if (selected != null)
        {
            if (StoreBuilderInput.KeyDown(KeyCode.R)) { RotateSelected(selected); return; }
            if (StoreBuilderInput.KeyDown(KeyCode.M)) { BeginMove(selected); return; }
            if (StoreBuilderInput.KeyDown(KeyCode.D)) { BeginDuplicate(selected.Target); return; }
        }

        if (StoreBuilderInput.Clicked)
            HandleClick(_cam.ScreenPointToRay(Input.mousePosition));
    }

    void HandleClick(Ray ray)
    {
        bool hitShelf = Physics.Raycast(ray, out RaycastHit shelfHit, StoreBuilderLayers.RayLength, _shelfMask, QueryTriggerInteraction.Ignore);
        OutlineSelector box = Physics.Raycast(ray, out RaycastHit boxHit, StoreBuilderLayers.RayLength, _interactableMask, QueryTriggerInteraction.Collide)
            ? boxHit.collider.GetComponent<OutlineSelector>()
            : null;

        // Shelf geometry sits inside its own box, so it beats shelf boxes; a prop box wins only when nearer.
        if (hitShelf && (box == null || box.Shelf != null || shelfHit.distance < boxHit.distance))
            ClickShelfGeometry(shelfHit.collider);
        else if (box != null)
            uiHandler.ToggleSelection(box);
    }

    void ClickShelfGeometry(Collider collider)
    {
        if (collider.TryGetComponent(out SubShelfMarker marker))
        {
            uiHandler.ToggleSubShelf(marker);
            return;
        }

        if (!collider.CompareTag("Wall")) return;
        ShelfBuilder builder = collider.GetComponentInParent<ShelfBuilder>();
        if (builder != null && builder.Selector != null)
            uiHandler.ToggleSelection(builder.Selector);
    }

    void RotateSelected(OutlineSelector selected)
    {
        // Shelves go through the UI so the rotation dropdown and item checks stay in sync.
        if (selected.Shelf != null)
        {
            uiHandler.RotateSelectedShelf();
            return;
        }

        RotateQuarterTurn(selected.Target);
        selected.Refit();
    }

    static void RotateQuarterTurn(GameObject obj)
    {
        if (obj.TryGetComponent(out ShelfBuilder shelf))
        {
            shelf.RotateQuarterTurn();
            shelf.Rebuild();
        }
        else
        {
            obj.transform.Rotate(Vector3.up, 90f);
        }
    }

    public void SummonSelectorBox(GameObject target) => OutlineSelector.Create(airMaterial, target);

    // ── Placement ─────────────────────────────────────────────────────────────

    // Called by the matching UI buttons; the agent spawn replaces the old one on confirm.
    public void OnSpawnShelfPressed()  => BeginPlacement(dataHandler.shelfPrefab);
    public void OnSpawnFridgePressed() => BeginPlacement(dataHandler.fridgePrefab);
    public void OnSpawnSelfCheckout()  => BeginPlacement(dataHandler.selfCheckoutCounter);
    public void OnSpawnAisleMarker()   => BeginPlacement(dataHandler.aisleMarkerPrefab);
    public void OnPlaceAgentSpawn()    => BeginPlacement(dataHandler.agentSpawnMarkerPrefab);

    void BeginPlacement(GameObject prefab)
    {
        AbortPlacement();
        uiHandler.ClearSelection();
        _placingPrefab = prefab;
        _placementMode = true;
    }

    void BeginMove(OutlineSelector selector)
    {
        if (selector.Shelf != null) UndoSpawnedItems();
        BeginPlacement(null);
        _moving = selector;
        _previewObject = selector.Target;
    }

    void BeginDuplicate(GameObject source)
    {
        // The store has a single agent spawn.
        if (source.TryGetComponent(out AgentSpawnMarker _)) return;

        bool isShelf = source.TryGetComponent(out ShelfBuilder sourceShelf);
        if (isShelf) UndoSpawnedItems();
        BeginPlacement(null);
        _isDuplicatePlacement = true;
        _previewObject = Instantiate(source, source.transform.position, source.transform.rotation);

        // Dictionaries are not serialized, so Instantiate does not copy the category map.
        if (isShelf)
            _previewObject.GetComponent<ShelfBuilder>().subShelfCategories = new(sourceShelf.subShelfCategories);
    }

    // Ends any placement in progress: a moved object is dropped where it is, a preview is discarded.
    public void AbortPlacement()
    {
        if (!_placementMode) return;

        if (_moving != null && _previewObject != null)
        {
            _previewObject.SetActive(true);
            ConfirmPlacement();
            return;
        }

        DestroyPreview();
        ExitPlacementMode();
    }

    void UndoSpawnedItems()
    {
        ShelfBuilder.DespawnAllItemsInScene();

        // Reverts shelves to not spawn items again
        foreach (ShelfBuilder shelf in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
            shelf.spawnItems = false;
    }

    void HandlePlacement()
    {
        if (StoreBuilderInput.KeyDown(KeyCode.R) && _previewObject != null)
        {
            RotateQuarterTurn(_previewObject);
            RefitMoving();
        }

        if (RaycastFloor(out Vector3 worldPos))
        {
            Vector3 snapped = SnapToGrid(worldPos);

            if (_previewObject == null)
            {
                SpawnPreview(snapped);
            }
            else if (!_previewObject.activeSelf || _previewObject.transform.position != snapped)
            {
                _previewObject.SetActive(true);
                _previewObject.transform.position = snapped;
                RefitMoving();
            }

            if (StoreBuilderInput.Clicked)
                ConfirmPlacement();
            return;
        }

        // Off the floor: hide the object at its last valid position until the cursor returns.
        if (_previewObject != null && _previewObject.activeSelf)
            _previewObject.SetActive(false);

        if (StoreBuilderInput.Clicked)
            CancelOffFloor();
    }

    void RefitMoving()
    {
        if (_moving != null) _moving.Refit();
    }

    // Left-click off the floor: a moved object is deleted along with its selector, anything else is discarded.
    void CancelOffFloor()
    {
        if (_moving != null)
            _moving.DestroyWithTarget();
        else
            DestroyPreview();
        ExitPlacementMode();
    }

    bool RaycastFloor(out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;
        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, StoreBuilderLayers.RayLength, _floorMask) || !hit.collider.CompareTag("Floor"))
            return false;

        hitPoint = hit.point;
        return true;
    }

    Vector3 SnapToGrid(Vector3 worldPos)
    {
        if (builderGridSize <= 0f) return worldPos;
        return new Vector3(
            Mathf.Round(worldPos.x / builderGridSize) * builderGridSize,
            worldPos.y,
            Mathf.Round(worldPos.z / builderGridSize) * builderGridSize
        );
    }

    void SpawnPreview(Vector3 position)
    {
        if (_placingPrefab == null) return;
        _previewObject = Instantiate(_placingPrefab, position, Quaternion.identity);

        // Set on the instance (not the prefab asset); Build reads it in Start.
        if (_previewObject.TryGetComponent(out ShelfBuilder builder))
            builder.floor = dataHandler.floor;
    }

    void ConfirmPlacement()
    {
        GameObject placed = _previewObject;
        bool isNew = _moving == null;

        if (placed != null)
        {
            if (placed.TryGetComponent(out ShelfBuilder shelf))
            {
                if (isNew) AssignShelfId(shelf);
                else shelf.Rebuild();
            }

            if (placed.TryGetComponent(out AisleMarker marker))
            {
                // A fresh marker takes the current UI values; moved or duplicated ones keep their own.
                if (isNew && !_isDuplicatePlacement) uiHandler.ApplyAisleMarkerSettings(marker);
                else marker.RefreshHeight();
            }

            if (!isNew)
            {
                _moving.Refit();
            }
            else
            {
                if (placed.TryGetComponent(out AgentSpawnMarker _)) DestroyOtherAgentSpawns(placed);
                SummonSelectorBox(placed);
            }
        }

        ExitPlacementMode();
    }

    void AssignShelfId(ShelfBuilder shelf)
    {
        int sourceId = shelf.shelfId;
        shelf.shelfId = dataHandler.GetUniqueShelfId();
        if (_isDuplicatePlacement)
            dataHandler.CopyShelfData(sourceId, shelf.shelfId);
    }

    // The store has one agent spawn: remove any other marker together with its selector box.
    static void DestroyOtherAgentSpawns(GameObject keep)
    {
        foreach (OutlineSelector selector in FindObjectsByType<OutlineSelector>(FindObjectsSortMode.None))
        {
            if (selector.Target != null && selector.Target != keep && selector.Target.TryGetComponent(out AgentSpawnMarker _))
                selector.DestroyWithTarget();
        }
    }

    void DestroyPreview()
    {
        if (_previewObject != null)
            Destroy(_previewObject);
    }

    void ExitPlacementMode()
    {
        _placementMode = false;
        _isDuplicatePlacement = false;
        _placingPrefab = null;
        _moving = null;
        _previewObject = null;
    }
}
