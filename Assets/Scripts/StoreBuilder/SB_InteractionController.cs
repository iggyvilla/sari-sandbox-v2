using UnityEngine;

public class SB_InteractionController : MonoBehaviour
{
    [Header("Camera Rotation")]
    public float rotationSpeed = 90f; // degrees per second
    public float zoomSpeed = 5f;
    public float minOrthoSize = 2f;
    public float maxOrthoSize = 30f;

    [Header("Shelf Placement")]
    public GameObject woodShelfPrefab;
    public GameObject fridgePrefab;
    public GameObject selfCheckoutPrefab;
    public GameObject agentSpawnPrefab;
    public GameObject aisleMarkerPrefab;
    public float builderGridSize = 1f;

    [Header("References")]
    public SB_UIHandler uiHandler;
    public DataHandler dataHandler;
    public Material airMaterial;

    private enum PropKind { SelfCheckout, AgentSpawn, AisleMarker }

    private GameObject shelfPrefab;
    private bool _placementMode = false;
    public bool IsInPlacementMode => _placementMode;
    private bool _isPropPlacement = false;
    private PropKind _propKind = PropKind.SelfCheckout;
    private GameObject _activePropPrefab = null;
    private GameObject _previewObject = null;
    private ShelfSelector _movingSelector = null;
    private PropSelector _movingPropSelector = null;
    private bool _isDuplicatePlacement = false;
    private Camera _cam;
    private LayerMask _sariFloorLayerMask;
    private LayerMask _sariShelfLayerMask;

    void Awake()
    {
        _cam = GetComponentInChildren<Camera>();
        if (_cam == null)
            _cam = Camera.main;

        _sariFloorLayerMask = LayerMask.GetMask(StoreBuilderLayers.Floor);
        _sariShelfLayerMask = LayerMask.GetMask(StoreBuilderLayers.Shelf);
    }

    void Update()
    {
        if (uiHandler != null && !uiHandler.interactionControlsEnabled) return;

        HandleCameraRotation();

        if (_placementMode)
            HandleShelfPlacement();
        else
            HandleSubShelfSelection();
    }

    // ── Camera rotation ───────────────────────────────────────────────────────

    void HandleCameraRotation()
    {
        float rotInput = 0f;
        if (Input.GetKey(KeyCode.RightArrow)) rotInput =  -1f;
        if (Input.GetKey(KeyCode.LeftArrow))  rotInput = 1f;
        if (rotInput != 0f)
            transform.RotateAround(transform.parent.position, Vector3.up, rotInput * rotationSpeed * Time.deltaTime);

        float zoomInput = 0f;
        if (Input.GetKey(KeyCode.UpArrow))   zoomInput = -1f;
        if (Input.GetKey(KeyCode.DownArrow)) zoomInput =  1f;
        if (zoomInput != 0f)
            _cam.orthographicSize = Mathf.Clamp(_cam.orthographicSize + zoomInput * zoomSpeed * Time.deltaTime, minOrthoSize, maxOrthoSize);
    }

    // ── Sub-shelf selection ───────────────────────────────────────────────────

    void HandleSubShelfSelection()
    {
        if (!Input.GetMouseButtonDown(0)) return;

        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, StoreBuilderLayers.RayLength, _sariShelfLayerMask, QueryTriggerInteraction.Ignore)) return;
        
        SubShelfMarker marker = hit.collider.GetComponent<SubShelfMarker>();
        if (marker != null)
        {
            uiHandler.ToggleSubShelf(marker);
            return;
        }

        if (hit.collider.CompareTag("Wall"))
        {
            ShelfBuilder builder = hit.collider.GetComponentInParent<ShelfBuilder>();
            if (builder != null)
            {
                if (uiHandler.selectedShelf == builder)
                {
                    uiHandler.DeselectShelf();
                }
                else
                {
                    if (builder.Selector != null)
                        uiHandler.SelectShelf(builder.Selector);
                }
            }
        }
    }

    // ── Shelf placement ───────────────────────────────────────────────────────

    // Called by the "Spawn Shelf" UI button
    public void OnSpawnShelfPressed() => BeginShelfPlacement(woodShelfPrefab);

    // Called by the "Spawn Fridge" UI button
    public void OnSpawnFridgePressed() => BeginShelfPlacement(fridgePrefab);

    // Called by the "Spawn Self Checkout" UI button
    public void OnSpawnSelfCheckout() => BeginPropPlacement(PropKind.SelfCheckout, selfCheckoutPrefab);

    // Called by the "Spawn Aisle Marker" UI button
    public void OnSpawnAisleMarker() => BeginPropPlacement(PropKind.AisleMarker, aisleMarkerPrefab);

    // Called by the "Place Agent Spawn" UI button; the old marker is replaced on confirm
    public void OnPlaceAgentSpawn() => BeginPropPlacement(PropKind.AgentSpawn, agentSpawnPrefab);

    void BeginShelfPlacement(GameObject prefab)
    {
        AbortPlacement();
        _isPropPlacement = false;
        shelfPrefab = prefab;
        _placementMode = true;
    }

    void BeginPropPlacement(PropKind kind, GameObject prefab)
    {
        AbortPlacement();
        _isPropPlacement = true;
        _propKind = kind;
        _activePropPrefab = prefab;
        _placementMode = true;
    }

    // Ends any placement in progress: a moved object is dropped where it is, a preview is discarded.
    void AbortPlacement()
    {
        if (!_placementMode) return;

        if ((_movingSelector != null || _movingPropSelector != null) && _previewObject != null)
        {
            _previewObject.SetActive(true);
            ConfirmPlacement();
            return;
        }

        DestroyPreview();
        ExitPlacementMode();
    }

    // Called by ShelfSelector when the user presses M on a selected shelf
    public void EnterMoveMode(ShelfSelector selector)
    {
        AbortPlacement();
        UndoSpawnedItems();

        _isPropPlacement = false;
        _movingSelector = selector;
        _previewObject = selector.assignedShelf.gameObject;
        _placementMode = true;
        uiHandler.DeselectShelf();
    }

    // Called by ShelfSelector when the user presses D on a selected shelf
    public void DuplicateShelf(ShelfBuilder source)
    {
        AbortPlacement();
        UndoSpawnedItems();

        _isPropPlacement = false;
        _isDuplicatePlacement = true;
        _previewObject = Instantiate(
            source.gameObject,
            source.transform.position,
            Quaternion.identity
        );
        // Dictionaries are not serialized, so Instantiate does not copy the category map.
        _previewObject.GetComponent<ShelfBuilder>().subShelfCategories = new(source.subShelfCategories);

        _movingSelector = null;
        _placementMode = true;
        uiHandler.DeselectShelf();
    }

    // Called by PropSelector when the user presses M on a selected prop
    public void EnterMoveModeForProp(PropSelector selector)
    {
        AbortPlacement();
        _isPropPlacement = true;
        _movingPropSelector = selector;
        _previewObject = selector.assignedProp;
        _placementMode = true;
        uiHandler.DeselectProp();
    }

    // Called by PropSelector when the user presses D on a selected prop
    public void DuplicateProp(GameObject source)
    {
        AbortPlacement();
        _isPropPlacement = true;
        _isDuplicatePlacement = true;
        // Keep _propKind consistent with the prop being duplicated so the placement
        // logic doesn't add a mismatched marker component to the clone.
        _propKind = InferPropKind(source);
        _previewObject = Instantiate(source, source.transform.position, source.transform.rotation);
        _movingPropSelector = null;
        _placementMode = true;
        uiHandler.DeselectProp();
    }

    static PropKind InferPropKind(GameObject prop)
    {
        if (prop.GetComponent<AgentSpawnMarker>() != null) return PropKind.AgentSpawn;
        if (prop.GetComponent<AisleMarker>() != null)      return PropKind.AisleMarker;
        return PropKind.SelfCheckout;
    }

    void UndoSpawnedItems()
    {
        ShelfBuilder.DespawnAllItemsInScene();
        
        // Reverts shelves to not spawn items again
        foreach (ShelfBuilder shelf in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
        {
            shelf.spawnItems = false;
        }
    }

    void HandleShelfPlacement()
    {
        if (Input.GetKeyDown(KeyCode.R) && _previewObject != null)
            RotatePreview();

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
                EncapsulateMovingSelector();
            }

            if (Input.GetMouseButtonDown(0))
                ConfirmPlacement();
            return;
        }

        // Off the floor: hide the object at its last valid position until the cursor returns.
        if (_previewObject != null && _previewObject.activeSelf)
            _previewObject.SetActive(false);

        if (Input.GetMouseButtonDown(0))
            CancelOffFloor();
    }

    void RotatePreview()
    {
        if (_isPropPlacement)
        {
            _previewObject.transform.Rotate(Vector3.up, 90f);
        }
        else
        {
            ShelfBuilder builder = _previewObject.GetComponent<ShelfBuilder>();
            if (builder == null) return;
            builder.rotationY = (builder.rotationY + 90f) % 360f;
            builder.Rebuild();
        }
        EncapsulateMovingSelector();
    }

    // Keeps the selector box of an object being moved wrapped around it.
    void EncapsulateMovingSelector()
    {
        if (_isPropPlacement)
        {
            if (_movingPropSelector != null) _movingPropSelector.EncapsulateProp(_previewObject);
        }
        else if (_movingSelector != null)
        {
            _movingSelector.EncapsulateShelf(_movingSelector.assignedShelf);
        }
    }

    // Left-click off the floor: a moved object is deleted along with its selector, anything else is discarded.
    void CancelOffFloor()
    {
        if (_isPropPlacement && _movingPropSelector != null)
        {
            Destroy(_movingPropSelector.assignedProp);
            Destroy(_movingPropSelector.gameObject);
            _previewObject = null;
        }
        else if (!_isPropPlacement && _movingSelector != null)
        {
            Destroy(_movingSelector.assignedShelf.gameObject);
            Destroy(_movingSelector.gameObject);
            _previewObject = null;
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
        
        if (Physics.Raycast(ray, out RaycastHit hit, StoreBuilderLayers.RayLength, _sariFloorLayerMask))
        {
            if (hit.collider.CompareTag("Floor"))
            {
                hitPoint = hit.point;
                return true;
            }
        }
        return false;
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
        GameObject prefab = _isPropPlacement ? _activePropPrefab : shelfPrefab;
        _previewObject = Instantiate(prefab, position, Quaternion.identity);

        // Set on the instance (not the prefab asset); Build reads it in Start.
        if (!_isPropPlacement && _previewObject.TryGetComponent(out ShelfBuilder builder))
            builder.floor = dataHandler.floor;
    }

    void ConfirmPlacement()
    {
        if (_isPropPlacement)
        {
            if (_movingPropSelector != null)
            {
                AisleMarker marker = _previewObject.GetComponent<AisleMarker>();
                if (marker != null)
                    marker.RefreshHeight();

                _movingPropSelector.EncapsulateProp(_previewObject);
            }
            else if (_previewObject != null)
            {
                switch (_propKind)
                {
                    case PropKind.AgentSpawn:
                        if (!_isDuplicatePlacement)
                            DestroyOtherAgentSpawns(_previewObject);
                        if (_previewObject.GetComponent<AgentSpawnMarker>() == null)
                            _previewObject.AddComponent<AgentSpawnMarker>();
                        break;
                    case PropKind.AisleMarker:
                        AisleMarker marker = _previewObject.GetComponent<AisleMarker>();
                        // For a fresh placement apply the current UI field values; for a
                        // duplicate keep the clone's own values intact.
                        if (marker != null)
                        {
                            if (_isDuplicatePlacement)
                                marker.RefreshHeight();
                            else
                                uiHandler.ApplyAisleMarkerSettings(marker);
                        }
                        break;
                    default:
                        if (_previewObject.GetComponent<SelfCheckoutMarker>() == null)
                            _previewObject.AddComponent<SelfCheckoutMarker>();
                        break;
                }
                SummonPropSelectorBox(_previewObject);
            }
        }
        else
        {
            if (_movingSelector != null)
            {
                _movingSelector.EncapsulateShelf(_movingSelector.assignedShelf);
                _movingSelector.assignedShelf.Rebuild();
            }
            else if (_previewObject != null)
            {
                ShelfBuilder builder = _previewObject.GetComponent<ShelfBuilder>();
                builder.shelfId = dataHandler.GetUniqueShelfId();
                builder.SummonOutlineBox(uiHandler, this);
            }
        }

        _previewObject = null; // relinquish ownership — the object stays in the scene
        ExitPlacementMode();
    }

    // The store has one agent spawn: remove any other marker together with its selector box.
    static void DestroyOtherAgentSpawns(GameObject keep)
    {
        PropSelector[] selectors = FindObjectsByType<PropSelector>(FindObjectsSortMode.None);
        foreach (AgentSpawnMarker marker in FindObjectsByType<AgentSpawnMarker>(FindObjectsSortMode.None))
        {
            if (marker.gameObject == keep) continue;
            foreach (PropSelector ps in selectors)
                if (ps.assignedProp == marker.gameObject) Destroy(ps.gameObject);
            Destroy(marker.gameObject);
        }
    }

    public void SummonPropSelectorBox(GameObject prop)
    {
        PropSelector selector = OutlineSelector.CreateBox<PropSelector>(airMaterial, uiHandler, this);
        selector.assignedProp = prop;
        selector.EncapsulateProp(prop);
    }

    void DestroyPreview()
    {
        if (_previewObject != null)
        {
            Destroy(_previewObject);
            _previewObject = null;
        }
    }

    void ExitPlacementMode()
    {
        _placementMode = false;
        _isDuplicatePlacement = false;
        _movingSelector = null;
        _movingPropSelector = null;
        _previewObject = null;
    }
}
