using JetBrains.Annotations;
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
    public float builderGridSize = 1f;

    [Header("References")]
    public SB_UIHandler uiHandler;
    public DataHandler dataHandler;
    public Material airMaterial;

    private GameObject shelfPrefab;
    private bool _placementMode = false;
    public bool IsInPlacementMode => _placementMode;
    private bool _isPropPlacement = false;
    private bool _isAgentSpawnPlacement = false;
    [CanBeNull] private GameObject _previewShelf = null;
    [CanBeNull] private ShelfSelector _movingSelector = null;
    [CanBeNull] private PropSelector _movingPropSelector = null;
    private bool _isDuplicatePlacement = false;
    private Camera _cam;
    private LayerMask _sariFloorLayerMask;
    private LayerMask _sariInteractableLayerMask;
    private LayerMask _sariShelfLayerMask;

    void Awake()
    {
        _cam = GetComponentInChildren<Camera>();
        if (_cam == null)
            _cam = Camera.main;

        _sariFloorLayerMask = LayerMask.GetMask("SariFloor");
        _sariInteractableLayerMask = LayerMask.GetMask("SariInteractable");
        _sariShelfLayerMask = LayerMask.GetMask("SariShelf");
    }

    void Update()
    {
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
        if (!Physics.Raycast(ray, out RaycastHit hit, 100f, _sariShelfLayerMask, QueryTriggerInteraction.Ignore)) return;
        
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
                    ShelfSelector selector = FindShelfSelector(builder);
                    if (selector != null)
                        uiHandler.SelectShelf(selector);
                }
            }
        }
    }

    ShelfSelector FindShelfSelector(ShelfBuilder builder)
    {
        foreach (ShelfSelector s in FindObjectsByType<ShelfSelector>(FindObjectsSortMode.None))
            if (s.assignedShelf == builder) return s;
        return null;
    }

    // ── Shelf placement ───────────────────────────────────────────────────────

    // Called by the "Spawn Shelf" UI button
    public void OnSpawnShelfPressed()
    {
        _isPropPlacement = false;
        shelfPrefab = woodShelfPrefab;
        ShelfBuilder builder = shelfPrefab.GetComponent<ShelfBuilder>();
        builder.floor = dataHandler.floor;
        _placementMode = true;
    }

    // Called by the "Spawn Fridge" UI button
    public void OnSpawnFridgePressed()
    {
        _isPropPlacement = false;
        shelfPrefab = fridgePrefab;
        ShelfBuilder builder = shelfPrefab.GetComponent<ShelfBuilder>();
        builder.floor = dataHandler.floor;
        _placementMode = true;
    }

    // Called by the "Spawn Self Checkout" UI button
    public void OnSpawnSelfCheckout()
    {
        _isAgentSpawnPlacement = false;
        _isPropPlacement = true;
        _placementMode = true;
    }

    // Called by the "Place Agent Spawn" UI button
    public void OnPlaceAgentSpawn()
    {
        // Destroy any existing agent spawn marker and its selector box
        AgentSpawnMarker existing = FindAnyObjectByType<AgentSpawnMarker>();
        if (existing != null)
        {
            foreach (PropSelector ps in FindObjectsByType<PropSelector>(FindObjectsSortMode.None))
            {
                if (ps.assignedProp == existing.gameObject)
                {
                    Destroy(ps.gameObject);
                    break;
                }
            }
            Destroy(existing.gameObject);
        }

        _isAgentSpawnPlacement = true;
        _isPropPlacement = true;
        _placementMode = true;
    }

    // Called by ShelfSelector when the user presses M on a selected shelf
    public void EnterMoveMode(ShelfSelector selector)
    {
        UndoSpawnedItems();

        _isPropPlacement = false;
        _movingSelector = selector;
        _previewShelf = selector.assignedShelf.gameObject;
        _placementMode = true;
        uiHandler.DeselectShelf();
    }

    // Called by ShelfSelector when the user presses D on a selected shelf
    public void DuplicateShelf(ShelfBuilder source)
    {
        UndoSpawnedItems();

        _isPropPlacement = false;
        _isDuplicatePlacement = true;
        _previewShelf = Instantiate(
            source.gameObject,
            source.transform.position,
            Quaternion.identity
        );

        _movingSelector = null;
        _placementMode = true;
        uiHandler.DeselectShelf();
    }

    // Called by PropSelector when the user presses M on a selected prop
    public void EnterMoveModeForProp(PropSelector selector)
    {
        _isPropPlacement = true;
        _movingPropSelector = selector;
        _previewShelf = selector.assignedProp;
        _placementMode = true;
        uiHandler.DeselectProp();
    }

    // Called by PropSelector when the user presses D on a selected prop
    public void DuplicateProp(GameObject source)
    {
        _isPropPlacement = true;
        _isDuplicatePlacement = true;
        _previewShelf = Instantiate(source, source.transform.position, source.transform.rotation);
        _movingPropSelector = null;
        _placementMode = true;
        uiHandler.DeselectProp();
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
        if (Input.GetKeyDown(KeyCode.R) && _previewShelf != null)
        {
            if (_isPropPlacement)
            {
                _previewShelf.transform.Rotate(Vector3.up, 90f);
                if (_movingPropSelector != null)
                    _movingPropSelector.EncapsulateProp(_previewShelf);
            }
            else
            {
                ShelfBuilder builder = _previewShelf.GetComponent<ShelfBuilder>();
                if (builder != null)
                {
                    builder.rotationY = (builder.rotationY + 90f) % 360f;
                    builder.Rebuild();
                    if (_movingSelector != null)
                        _movingSelector.EncapsulateShelf(_movingSelector.assignedShelf);
                }
            }
        }

        bool hitFloor = RaycastFloor(out Vector3 worldPos);

        if (hitFloor)
        {
            Vector3 snapped = SnapToGrid(worldPos);

            if (_previewShelf == null)
            {
                SpawnPreview(snapped);
            }
            else
            {
                if (!_previewShelf.activeSelf)
                    _previewShelf.SetActive(true);
                _previewShelf.transform.position = snapped;
                if (_isPropPlacement)
                {
                    if (_movingPropSelector != null)
                        _movingPropSelector.EncapsulateProp(_previewShelf);
                }
                else
                {
                    if (_movingSelector != null)
                        _movingSelector.EncapsulateShelf(_movingSelector.assignedShelf);
                }
            }

            if (Input.GetMouseButtonDown(0))
            {
                ConfirmPlacement();
            }
        }
        else
        {
            bool isMoving = _isPropPlacement ? _movingPropSelector != null : _movingSelector != null;

            if (!isMoving && !_isDuplicatePlacement)
            {
                // Normal new placement: destroy the preview
                DestroyPreview();
            }
            else if (_previewShelf != null && _previewShelf.activeSelf)
            {
                _previewShelf.SetActive(false);
            }
            // Move/duplicate mode: keep the object at its last valid position until the user clicks

            // Left-click off the floor: treat as a cancellation
            if (Input.GetMouseButtonDown(0))
            {
                if (_isPropPlacement && _movingPropSelector != null)
                {
                    Destroy(_movingPropSelector.assignedProp);
                    Destroy(_movingPropSelector.gameObject);
                    _movingPropSelector = null;
                    _previewShelf = null;
                }
                else if (_movingSelector != null)
                {
                    Destroy(_movingSelector.assignedShelf.gameObject);
                    Destroy(_movingSelector.gameObject);
                    _movingSelector = null;
                    _previewShelf = null;
                }
                else if (_isDuplicatePlacement)
                {
                    DestroyPreview();
                }
                ExitPlacementMode();
            }
        }
    }

    bool RaycastFloor(out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;
        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        
        if (Physics.Raycast(ray, out RaycastHit hit, 100f, _sariFloorLayerMask))
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
        GameObject prefab = _isAgentSpawnPlacement ? agentSpawnPrefab
            : _isPropPlacement ? selfCheckoutPrefab
            : shelfPrefab;
        _previewShelf = Instantiate(prefab, position, Quaternion.identity);
    }

    void ConfirmPlacement()
    {
        if (_isPropPlacement)
        {
            if (_movingPropSelector != null)
            {
                _movingPropSelector.EncapsulateProp(_previewShelf);
                _movingPropSelector = null;
            }
            else if (_previewShelf != null)
            {
                if (_isAgentSpawnPlacement)
                {
                    if (_previewShelf.GetComponent<AgentSpawnMarker>() == null)
                        _previewShelf.AddComponent<AgentSpawnMarker>();
                }
                else
                {
                    if (_previewShelf.GetComponent<SelfCheckoutMarker>() == null)
                        _previewShelf.AddComponent<SelfCheckoutMarker>();
                }
                SummonPropSelectorBox(_previewShelf);
            }
        }
        else
        {
            if (_movingSelector != null)
            {
                _movingSelector.EncapsulateShelf(_movingSelector.assignedShelf);
                _movingSelector.assignedShelf.Rebuild();
                _movingSelector = null;
            }
            else if (_previewShelf != null)
            {
                ShelfBuilder builder = _previewShelf.GetComponent<ShelfBuilder>();
                builder.shelfId = dataHandler.GetUniqueShelfId();
                builder.SummonOutlineBox(uiHandler, this);
            }
        }

        _previewShelf = null; // relinquish ownership — the object stays in the scene
        ExitPlacementMode();
    }

    public void SummonPropSelectorBox(GameObject prop)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.layer = LayerMask.NameToLayer("SariInteractable");
        cube.GetComponent<Renderer>().material = airMaterial;
        cube.GetComponent<BoxCollider>().isTrigger = true;
        cube.AddComponent<OutlineFx.OutlineFx>();

        PropSelector selector = cube.AddComponent<PropSelector>();
        selector.assignedProp = prop;
        selector.uiHandler = uiHandler;
        selector.interactionController = this;

        selector.EncapsulateProp(prop);
    }

    void DestroyPreview()
    {
        if (_previewShelf != null)
        {
            Destroy(_previewShelf);
            _previewShelf = null;
        }
    }

    void ExitPlacementMode()
    {
        _placementMode = false;
        _isAgentSpawnPlacement = false;
        _isDuplicatePlacement = false;
    }
}
