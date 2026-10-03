using UnityEngine;

public enum BuilderTool { Select, Shelf, Fridge, SelfCheckout, AisleMarker, AgentSpawn, EmergencyExit }

public class SB_InteractionController : MonoBehaviour
{
    static readonly (KeyCode key, BuilderTool tool)[] ToolKeys =
    {
        (KeyCode.V, BuilderTool.Select), (KeyCode.S, BuilderTool.Shelf), (KeyCode.F, BuilderTool.Fridge),
        (KeyCode.C, BuilderTool.SelfCheckout), (KeyCode.A, BuilderTool.AisleMarker),
        (KeyCode.G, BuilderTool.AgentSpawn), (KeyCode.E, BuilderTool.EmergencyExit)
    };

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
    private RoomStructure _exitRoom;   // set while the emergency-exit tool is active
    private (WallSide side, int cell)? _exitHover;
    private bool _exitHoverClicked;    // no preview until the pointer moves to another cell
    private Camera _cam;
    private LayerMask _floorMask;
    private LayerMask _shelfMask;
    private LayerMask _interactableMask;

    /// <summary>The tool whose palette row is highlighted; Select while moving, duplicating or idle.</summary>
    public BuilderTool ActiveTool { get; private set; }

    /// <summary>True while an object is being placed, moved or duplicated, or the exit tool is active.</summary>
    public bool IsPlacing => _placementMode || _exitRoom != null;

    /// <summary>Banner text of the current placement, or null when idle.</summary>
    public string PlacementTitle
    {
        get
        {
            if (_exitRoom != null) return "Emergency exit";
            if (!_placementMode) return null;
            if (_moving != null) return "Moving " + Describe(_moving.Target);
            if (_isDuplicatePlacement) return "Duplicating " + Describe(_previewObject);
            return "Placing " + ToolLabel(ActiveTool);
        }
    }

    public string PlacementHint =>
        _exitRoom != null ? "Click a wall cell to add or remove an exit"
        : _moving != null ? "Click the floor to drop it"
        : ActiveTool == BuilderTool.AgentSpawn ? "Click the floor. Replaces the current spawn"
        : "Click the floor to place";

    public string CancelLabel => _exitRoom != null ? "Done" : _moving != null ? "Drop here" : "Cancel";

    public static string ToolLabel(BuilderTool tool) => tool switch
    {
        BuilderTool.Select => "Select",
        BuilderTool.Shelf => "Shelf",
        BuilderTool.Fridge => "Fridge",
        BuilderTool.SelfCheckout => "Self-checkout",
        BuilderTool.AisleMarker => "Aisle marker",
        BuilderTool.AgentSpawn => "Agent spawn",
        _ => "Emergency exit"
    };

    /// <summary>Display name of a store object, e.g. "Fridge".</summary>
    public static string Describe(GameObject target)
    {
        if (target == null) return "object";
        if (target.TryGetComponent(out ShelfBuilder shelf)) return shelf.isFridge ? "Fridge" : "Shelf";
        if (target.TryGetComponent(out AisleMarker _)) return "Aisle marker";
        if (target.TryGetComponent(out AgentSpawnMarker _)) return "Agent spawn";
        if (target.TryGetComponent(out SelfCheckoutMarker _)) return "Self-checkout";
        return target.name;
    }

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
        HandleHotkeys();

        if (_placementMode)
            HandlePlacement();
        else if (_exitRoom != null)
            HandleExitTool();
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

    // ── Hotkeys ───────────────────────────────────────────────────────────────

    void HandleHotkeys()
    {
        if (StoreBuilderInput.Modifier) return;

        foreach ((KeyCode key, BuilderTool tool) in ToolKeys)
        {
            if (!StoreBuilderInput.KeyDown(key)) continue;
            SelectTool(tool);
            return;
        }

        if (!StoreBuilderInput.KeyDown(KeyCode.Escape)) return;
        if (IsPlacing) AbortPlacement();
        else uiHandler.ClearSelection();
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    void HandleSelection()
    {
        if (uiHandler.ActiveSelector != null)
        {
            if (StoreBuilderInput.KeyDown(KeyCode.R)) { RotateSelected(); return; }
            if (StoreBuilderInput.KeyDown(KeyCode.M)) { MoveSelected(); return; }
            if (StoreBuilderInput.KeyDown(KeyCode.D)) { DuplicateSelected(); return; }
            if (StoreBuilderInput.KeyDown(KeyCode.Delete) || StoreBuilderInput.KeyDown(KeyCode.Backspace)) { DeleteSelected(); return; }
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

    // Selection actions: hotkeys and the inspector's action bar both call these.

    public void RotateSelected()
    {
        OutlineSelector selected = uiHandler.ActiveSelector;
        if (selected == null) return;

        // Shelves go through the UI so the rotation control and item checks stay in sync.
        if (selected.Shelf != null)
        {
            uiHandler.RotateSelectedShelf();
            return;
        }

        RotateQuarterTurn(selected.Target);
        selected.Refit();
        uiHandler.MarkDirty();
    }

    public void MoveSelected()
    {
        OutlineSelector selected = uiHandler.ActiveSelector;
        if (selected != null) BeginMove(selected);
    }

    public void DuplicateSelected()
    {
        OutlineSelector selected = uiHandler.ActiveSelector;
        if (selected != null) BeginDuplicate(selected.Target);
    }

    public void DeleteSelected()
    {
        OutlineSelector selected = uiHandler.ActiveSelector;
        if (selected == null) return;

        if (selected.Shelf != null)
        {
            UndoSpawnedItems();
            dataHandler.shouldShelfSpawnItems.Remove(selected.Shelf.shelfId);
        }

        uiHandler.ClearSelection();
        selected.DestroyWithTarget();
        uiHandler.MarkDirty();
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

    // Called by the palette and the tool hotkeys; the agent spawn replaces the old one on confirm.
    public void SelectTool(BuilderTool tool)
    {
        switch (tool)
        {
            case BuilderTool.Select: AbortPlacement(); break;
            case BuilderTool.EmergencyExit: BeginExitTool(); break;
            default: BeginPlacement(PrefabFor(tool), tool); break;
        }
    }

    GameObject PrefabFor(BuilderTool tool) => tool switch
    {
        BuilderTool.Shelf => dataHandler.shelfPrefab,
        BuilderTool.Fridge => dataHandler.fridgePrefab,
        BuilderTool.SelfCheckout => dataHandler.selfCheckoutCounter,
        BuilderTool.AisleMarker => dataHandler.aisleMarkerPrefab,
        _ => dataHandler.agentSpawnMarkerPrefab
    };

    // Not a prefab: the tool previews and toggles emergency exits on the walls until Escape / right-click.
    void BeginExitTool()
    {
        AbortPlacement();
        uiHandler.ClearSelection();
        _exitRoom = dataHandler.Room;
        _exitHover = null;
        if (_exitRoom != null) ActiveTool = BuilderTool.EmergencyExit;
    }

    void BeginPlacement(GameObject prefab, BuilderTool tool = BuilderTool.Select)
    {
        AbortPlacement();
        uiHandler.ClearSelection();
        _placingPrefab = prefab;
        _placementMode = true;
        ActiveTool = tool;
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
        EndExitTool();
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

    // ── Emergency exit tool ───────────────────────────────────────────────────

    void HandleExitTool()
    {
        if (Input.GetMouseButtonDown(1))
            EndExitTool();
        else
            UpdateExitTool(_cam.ScreenPointToRay(Input.mousePosition), StoreBuilderInput.Clicked);
    }

    // Hovering a free cell previews an exit there; a click places it, or removes the one already there.
    void UpdateExitTool(Ray ray, bool click)
    {
        WallSide side = default;
        int cell = 0;
        bool onWall = !StoreBuilderInput.PointerOverUI && _exitRoom.TryGetWallCell(ray, out side, out cell);
        var hover = onWall ? (side, cell) : ((WallSide, int)?)null;
        if (hover != _exitHover)
        {
            _exitHover = hover;
            _exitHoverClicked = false;
        }

        if (!onWall)
        {
            _exitRoom.ClearExitPreview();
        }
        else if (click)
        {
            _exitRoom.ToggleExit(side, cell);
            _exitHoverClicked = true;
        }
        else if (_exitHoverClicked || !_exitRoom.CanPlaceExit(side, cell))
        {
            _exitRoom.ClearExitPreview();
        }
        else
        {
            _exitRoom.PreviewExit(side, cell);
        }
    }

    void EndExitTool()
    {
        if (_exitRoom != null) _exitRoom.ClearExitPreview();
        _exitRoom = null;
        if (ActiveTool == BuilderTool.EmergencyExit) ActiveTool = BuilderTool.Select;
    }

    void RefitMoving()
    {
        if (_moving != null) _moving.Refit();
    }

    // Left-click off the floor: a moved object is deleted along with its selector, anything else is discarded.
    void CancelOffFloor()
    {
        if (_moving != null)
        {
            _moving.DestroyWithTarget();
            uiHandler.MarkDirty();
        }
        else
        {
            DestroyPreview();
        }
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
            uiHandler.MarkDirty();
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
        ActiveTool = BuilderTool.Select;
    }
}
