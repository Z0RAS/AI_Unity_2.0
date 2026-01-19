using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.InputSystem;

public class BuildingPlacementController : MonoBehaviour
{
    public static BuildingPlacementController Instance;

    public Camera mainCamera;
    public LayerMask buildingMask;
    public Tilemap tilemap;

    private BuildingData buildingToPlace;
    private GameObject ghostInstance;
    private SpriteRenderer ghostRenderer;

    private bool isPlacing = false;

    [Header("Placement Grid Visualization")]
    public bool showPlacementGrid = true;
    public Color validCellColor = new Color(0f, 1f, 0f, 0.45f);
    public Color invalidCellColor = new Color(1f, 0f, 0f, 0.45f);
    public float cellPadding = 0.05f; // small gap so cells don't touch visually

    // Runtime helpers for drawing footprint cells
    private List<GameObject> footprintCells = new List<GameObject>();
    private Sprite placementSprite;
    private Transform footprintRoot;

    private void Awake()
    {
        Instance = this;

        if (tilemap == null)
            tilemap = FindFirstObjectByType<Tilemap>();

        // Create a 1x1 white sprite for placement cell visualization (created once)
        Texture2D tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        placementSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);

        // Root transform for footprint cells to keep hierarchy clean
        var go = new GameObject("PlacementGridRoot");
        go.transform.SetParent(this.transform, false);
        footprintRoot = go.transform;
    }

    private void Start()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;
    }

    private void Update()
    {
        if (!isPlacing)
            return;

        UpdateGhostPosition();
        HandlePlacementInput();
    }

    public void StartPlacing(BuildingData data)
    {
        buildingToPlace = data;
        isPlacing = true;

        ghostInstance = Instantiate(data.prefab);
        ghostRenderer = ghostInstance.GetComponentInChildren<SpriteRenderer>();

        // Disable collider on ghost
        var col = ghostInstance.GetComponent<Collider2D>();
        if (col != null)
            col.enabled = false;

        // Disable construction script on ghost
        var bcGhost = ghostInstance.GetComponent<BuildingConstruction>();
        if (bcGhost != null)
            bcGhost.enabled = false;
    }

    public void CancelPlacement()
    {
        isPlacing = false;
        if (ghostInstance != null)
            Destroy(ghostInstance);

        ClearPlacementGrid();
    }

    // ---------------------------------------------------------
    // GHOST LOGIC
    // ---------------------------------------------------------

    void UpdateGhostPosition()
    {
        Vector2 mouseScreen = Mouse.current.position.ReadValue();
        Vector3 world = mainCamera.ScreenToWorldPoint(mouseScreen);
        world.z = 0f;

        if (GridManager.Instance == null)
        {
            ClearPlacementGrid();
            return;
        }

        // Determine footprint size (prefer BuildingConstruction on prefab, fallback to BuildingData.size)
        Vector2Int footprintSize = Vector2Int.one;
        var bcGhost = ghostInstance != null ? ghostInstance.GetComponent<BuildingConstruction>() : null;
        if (bcGhost != null)
            footprintSize = bcGhost.size;
        else if (buildingToPlace != null)
            footprintSize = buildingToPlace.size;

        // Compute the canonical bottom-left grid indices for the footprint centered on the mouse world
        Vector2Int start = GridManager.Instance.GetStartIndicesForCenteredFootprint(world, footprintSize);

        // Compute the exact centered world position of the footprint by averaging the first and last occupied node centers.
        Node firstNode = GridManager.Instance.GetNode(start.x, start.y);
        Node lastNode = GridManager.Instance.GetNode(start.x + footprintSize.x - 1, start.y + footprintSize.y - 1);

        // If nodes are missing, bail out.
        if (firstNode == null || lastNode == null)
        {
            ClearPlacementGrid();
            return;
        }

        Vector3 footprintCenter = (firstNode.centerPosition + lastNode.centerPosition) * 0.5f;

        // Snap the ghost to the computed footprint center — this uses the exact node centers and eliminates rounding drift.
        Vector3 snapped = footprintCenter;
        ghostInstance.transform.position = snapped;

        // Update ghost tint based on whole-footprint validity
        bool overallCanPlace = CanPlaceHere(snapped);
        if (ghostRenderer != null)
            ghostRenderer.color = overallCanPlace
                ? new Color(0f, 1f, 0f, 0.5f)
                : new Color(1f, 0f, 0f, 0.5f);

        // Update per-node footprint grid visualization
        if (showPlacementGrid)
        {
            UpdatePlacementGrid(snapped, footprintSize);
        }
        else
        {
            ClearPlacementGrid();
        }
    }

    bool CanPlaceHere(Vector3 pos)
    {
        float cell = GridManager.Instance.nodeDiameter;

        Vector2Int footprint = Vector2Int.one;
        var bc = ghostInstance != null ? ghostInstance.GetComponent<BuildingConstruction>() : null;
        if (bc != null)
            footprint = bc.size;
        else if (buildingToPlace != null)
            footprint = buildingToPlace.size;

        // Compute bottom-left world and iterate footprint nodes using GridManager helper.
        Vector2Int start = GridManager.Instance.GetStartIndicesForCenteredFootprint(pos, footprint);

        for (int dx = 0; dx < footprint.x; dx++)
        {
            for (int dy = 0; dy < footprint.y; dy++)
            {
                Node n = GridManager.Instance.GetNode(start.x + dx, start.y + dy);
                if (n == null) return false;
                if (!n.walkable) return false;

                // check reserved
                if (NodeReservationSystem.Instance != null && NodeReservationSystem.Instance.IsReserved(n)) return false;

                // check physical colliders in the node
                Collider2D[] hits = Physics2D.OverlapCircleAll(n.centerPosition, cell * 0.45f);
                foreach (var h in hits)
                {
                    if (h == null) continue;
                    if (h.GetComponent<ResourceNode>() != null) return false;

                    // Treat UnitAgent as occupying the node containing its transform.position only.
                    var ua = h.GetComponent<UnitAgent>();
                    if (ua != null)
                    {
                        Node unitNode = GridManager.Instance.NodeFromWorldPoint(ua.transform.position);
                        if (unitNode != null && unitNode.gridX == n.gridX && unitNode.gridY == n.gridY)
                            return false; // unit's center occupies this node -> block placement
                        else
                            continue; // unit collider overlaps but unit's center is elsewhere -> ignore for single-node occupancy
                    }

                    if (((1 << h.gameObject.layer) & buildingMask) != 0) return false;
                }
            }
        }

        // Also perform the original overlapbox check as a conservative test (center-based)
        float epsilon = 0.05f;
        Vector2 size = new Vector2(
            footprint.x * cell - epsilon,
            footprint.y * cell - epsilon
        );

        Collider2D hit = Physics2D.OverlapBox(
            pos,
            size,
            0f,
            buildingMask
        );

        if (hit != null)
            return false;

        return true;
    }

    // Create/Update visual cells that show each footprint node and whether that node is buildable.
    void UpdatePlacementGrid(Vector3 centerWorld, Vector2Int footprint)
    {
        if (centerWorld == null)
        {
            ClearPlacementGrid();
            return;
        }

        int totalCells = footprint.x * footprint.y;

        // Ensure pool size
        while (footprintCells.Count < totalCells)
        {
            var go = new GameObject("PlacementCell");
            go.transform.SetParent(footprintRoot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = placementSprite;
            sr.material = new Material(Shader.Find("Sprites/Default"));
            sr.sortingOrder = 5000; // on top
            sr.color = validCellColor;
            footprintCells.Add(go);
        }

        int idx = 0;
        float cellDiameter = GridManager.Instance.nodeDiameter;
        Vector2 cellScale = new Vector2(cellDiameter - cellPadding, cellDiameter - cellPadding);

        // bottom-left world for footprint
        Vector2Int start = GridManager.Instance.GetStartIndicesForCenteredFootprint(centerWorld, footprint);
        Vector3 bottomLeftWorld = GridManager.Instance.GetNode(start.x, start.y).centerPosition
                                  - new Vector3(0f, 0f, 0f); // direct node center is bottom-left cell center

        // Iterate footprint and place overlay at node centers
        for (int y = 0; y < footprint.y; y++)
        {
            for (int x = 0; x < footprint.x; x++)
            {
                Node n = GridManager.Instance.GetNode(start.x + x, start.y + y);

                GameObject cellGO = footprintCells[idx++];
                cellGO.SetActive(true);
                cellGO.transform.localScale = new Vector3(cellScale.x, cellScale.y, 1f);

                SpriteRenderer sr = cellGO.GetComponent<SpriteRenderer>();

                if (n == null)
                {
                    // Out of bounds → treat as invalid
                    cellGO.transform.position = centerWorld + new Vector3((x - footprint.x/2f) * cellDiameter, (y - footprint.y/2f) * cellDiameter, 0f);
                    sr.color = invalidCellColor;
                    continue;
                }

                cellGO.transform.position = n.centerPosition;

                // Check per-node validity: walkable, not reserved, no blocking colliders
                bool ok = true;
                if (!n.walkable) ok = false;
                if (NodeReservationSystem.Instance != null && NodeReservationSystem.Instance.IsReserved(n)) ok = false;

                // Check physical collisions in that cell
                Collider2D[] hits = Physics2D.OverlapCircleAll(n.centerPosition, cellDiameter * 0.45f);
                foreach (var h in hits)
                {
                    if (h == null) continue;
                    if (h.GetComponent<ResourceNode>() != null) { ok = false; break; }

                    // Treat UnitAgent as occupying only the node containing its transform.position
                    var ua = h.GetComponent<UnitAgent>();
                    if (ua != null)
                    {
                        Node unitNode = GridManager.Instance.NodeFromWorldPoint(ua.transform.position);
                        if (unitNode == null || unitNode.gridX != n.gridX || unitNode.gridY != n.gridY)
                        {
                            // unit center is not this node -> ignore collider for placement blocking
                            continue;
                        }
                        ok = false;
                        break;
                    }

                    if (((1 << h.gameObject.layer) & buildingMask) != 0) { ok = false; break; }
                }

                sr.color = ok ? validCellColor : invalidCellColor;
            }
        }

        // Hide unused pooled cells
        for (int i = totalCells; i < footprintCells.Count; i++)
        {
            footprintCells[i].SetActive(false);
        }
    }

    void ClearPlacementGrid()
    {
        for (int i = 0; i < footprintCells.Count; i++)
        {
            if (footprintCells[i] != null)
                Destroy(footprintCells[i]);
        }
        footprintCells.Clear();
    }

    // ---------------------------------------------------------
    // INPUT (Input System)
    // ---------------------------------------------------------
    void HandlePlacementInput()
    {
        // Right click = cancel
        if (Mouse.current.rightButton.wasPressedThisFrame)
        {
            CancelPlacement();
            return;
        }

        // Left click = place building
        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            Vector2 mouseScreen = Mouse.current.position.ReadValue();
            Vector3 world = mainCamera.ScreenToWorldPoint(mouseScreen);
            world.z = 0f;

            Node node = GridManager.Instance.NodeFromWorldPoint(world);
            if (node == null)
                return;

            Vector3 snapped = node.centerPosition;

            if (CanPlaceHere(snapped))
                PlaceBuilding(snapped);
        }
    }

    void PlaceBuilding(Vector3 pos)
    {
        // If a ghost is present use its exact snapped position (handles even footprints)
        Vector3 spawnPos = (ghostInstance != null) ? ghostInstance.transform.position : pos;
        GameObject building = Instantiate(buildingToPlace.prefab, spawnPos, Quaternion.identity);

        BuildingConstruction bc = building.GetComponent<BuildingConstruction>();

        // Resolve canonical player economy (GameSystems preferred)
        PlayerEconomy canonical = null;
        var gs = GameObject.Find("GameSystems");
        if (gs != null)
            canonical = gs.GetComponent<PlayerEconomy>();
        if (canonical == null)
            canonical = Object.FindObjectOfType<PlayerEconomy>();

        if (bc != null)
        {
            // Assign data and owner hint so nearby villagers will respond
            bc.data = buildingToPlace;
            bc.isGhost = false;
            bc.isUnderConstruction = true;
            bc.isFinished = false;

            // assign owner hint from GameSystems (so villagers respond to this construction)
            bc.assignedOwner = canonical;

            // Begin construction immediately
            bc.BeginConstruction();

            // Delay the recruitment call one frame — allows villagers to finish OnEnable/registration
            // and ensures BuildingNode/snapping has settled for accurate distance checks.
            StartCoroutine(DelayedAlertFor(bc));
        }
        else
        {
            // Finished building prefab case: initialize Building and DropOff owner
            Building buildingComp = building.GetComponent<Building>();
            if (buildingComp != null)
                buildingComp.Init(buildingToPlace, canonical);

            DropOffBuilding drop = building.GetComponent<DropOffBuilding>();
            if (drop != null)
                drop.owner = canonical;
        }

        CancelPlacement();
    }

    IEnumerator DelayedAlertFor(BuildingConstruction bc)
    {
        yield return null; // one frame
        try { bc.AlertNearbyIdleVillagers(); } catch { }
    }
}