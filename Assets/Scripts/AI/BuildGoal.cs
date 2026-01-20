using System;
using UnityEngine;

/// <summary>
/// Build a given building prefab near the owner's castle and wait until construction finishes (or timeout).
/// Calls back to the owner controller to register barracks when ready.
/// </summary>
public class BuildGoal : IGoal
{
    public string Name { get; private set; }

    private readonly EnemyKingController owner;
    private readonly GameObject buildingPrefab;
    private readonly bool isHouse;
    private readonly float timeoutSeconds;

    private GameObject instance;
    private BuildingConstruction bc;
    private float startTime;
    private bool started;

    public BuildGoal(EnemyKingController owner, GameObject prefab, bool isHouse = false, float timeoutSeconds = 30f)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.buildingPrefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
        this.isHouse = isHouse;
        this.timeoutSeconds = timeoutSeconds;
        Name = $"Build:{(buildingPrefab != null ? buildingPrefab.name : "Unknown")}";
    }

    // Constructor for completing existing building instances (like castle)
    public BuildGoal(EnemyKingController owner, GameObject existingInstance, float timeoutSeconds = 120f)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.instance = existingInstance ?? throw new ArgumentNullException(nameof(existingInstance));
        this.buildingPrefab = null; // Not used for existing instances
        this.isHouse = false;
        this.timeoutSeconds = timeoutSeconds;
        Name = $"Complete:{(existingInstance != null ? existingInstance.name : "Unknown")}";
    }

    public void Start()
    {
        startTime = Time.time;
        started = true;

        // If we don't have an instance yet, place building via local helper
        if (instance == null)
        {
            instance = TryPlaceBuilding(buildingPrefab, isHouse);
        }

        if (instance != null)
        {
            bc = instance.GetComponent<BuildingConstruction>();
            // Ensure owner/under-construction state so recruiters and villagers respond
            if (bc != null)
            {
                try
                {
                    if (owner.EnemyEconomy != null)
                        bc.assignedOwner = owner.EnemyEconomy;

                    if (!bc.isUnderConstruction)
                    {
                        bc.BeginConstruction();

                    }
                }
                catch { }
            }
            // If this is a barracks, register it when ready
            if (!isHouse && buildingPrefab == owner.barracksPrefab)
            {
                owner.StartCoroutine(RegisterBarracksWhenReady(instance));
            }
        }
    }

    public void Tick()
    {
        // polling done via IsComplete
    }

    public bool IsComplete
    {
        get
        {
            if (!started) return false;

            // no construction component -> consider done
            if (bc == null)
                return true;

            if (bc.isFinished)
                return true;

            if (timeoutSeconds > 0f && Time.time - startTime > timeoutSeconds)
                return true;

            return false;
        }
    }

    public void Abort()
    {
        // nothing special to abort; optional: stop construction or destroy placed prefab if desired
    }

    private GameObject TryPlaceBuilding(GameObject prefab, bool isHouse)
    {
        if (prefab == null) return null;

        var castle = owner.CastleInstance;
        if (castle == null) return null;

        var gm = GridManager.Instance;
        if (gm == null) return null;

        // Determine footprint size for the prefab (prefer BuildingConstruction on prefab, fallback to BuildingData.size).
        Vector2Int footprint = Vector2Int.one;
        try
        {
            var bcPrefab = prefab.GetComponent<BuildingConstruction>();
            if (bcPrefab != null)
                footprint = bcPrefab.Size;
            else
            {
                var bcomp = prefab.GetComponent<Building>();
                if (bcomp != null && bcomp.data != null)
                {
                    footprint = bcomp.data.size;
                }
            }
        }
        catch { footprint = Vector2Int.one; }

        // Find a suitable location near the castle
        Node center = gm.NodeFromWorldPoint(castle.transform.position);
        if (center == null) return null;

        // Search for a suitable building location (expand rings around castle)
        for (int radius = 2; radius <= 8; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    if (Mathf.Abs(x) + Mathf.Abs(y) != radius) continue; // Only check perimeter

                    Node candidateCenterNode = gm.GetNode(center.gridX + x, center.gridY + y);
                    if (candidateCenterNode == null || !candidateCenterNode.walkable) continue;

                    // Compute start indices for footprint centered at this candidate node's world pos
                    Vector3 candidateWorld = candidateCenterNode.centerPosition;
                    Vector2Int start = gm.GetStartIndicesForCenteredFootprint(candidateWorld, footprint);

                    // Validate all nodes within footprint exist & are walkable
                    bool canPlace = true;
                    for (int dx = 0; dx < footprint.x && canPlace; dx++)
                    {
                        for (int dy = 0; dy < footprint.y; dy++)
                        {
                            Node n = gm.GetNode(start.x + dx, start.y + dy);
                            if (n == null || !n.walkable)
                            {
                                canPlace = false;
                                break;
                            }

                            // optional: skip if reserved (prevent placing on reserved nodes)
                            if (NodeReservationSystem.Instance != null && NodeReservationSystem.Instance.IsReserved(n))
                            {
                                canPlace = false;
                                break;
                            }

                            // optional: avoid placing on resource nodes / units overlapping footprint
                            Collider2D[] hits = null;
                            try
                            {
                                hits = Physics2D.OverlapCircleAll(n.centerPosition, gm.nodeDiameter * 0.45f);
                            }
                            catch { hits = null; }

                            if (hits != null)
                            {
                                foreach (var h in hits)
                                {
                                    if (h == null) continue;
                                    if (h.GetComponent<ResourceNode>() != null) { canPlace = false; break; }

                                    var ua = h.GetComponent<UnitAgent>();
                                    if (ua != null)
                                    {
                                        Node unitNode = gm.NodeFromWorldPoint(ua.transform.position);
                                        if (unitNode != null && unitNode.gridX == n.gridX && unitNode.gridY == n.gridY)
                                        {
                                            canPlace = false;
                                            break;
                                        }
                                    }

                                    // building mask collisions are expensive to determine here; skip strict mask checks
                                }

                                if (!canPlace) break;
                            }
                        }
                    }

                    if (!canPlace) continue;

                    // Make sure no other buildings are too close (conservative check using centers)
                    Vector3 firstNodeWorld = gm.GetNode(start.x, start.y).centerPosition;
                    Vector3 lastNodeWorld = gm.GetNode(start.x + footprint.x - 1, start.y + footprint.y - 1).centerPosition;
                    Vector3 footprintCenter = (firstNodeWorld + lastNodeWorld) * 0.5f;

                    bool tooClose = false;
                    var buildings = UnityEngine.Object.FindObjectsOfType<Building>();
                    foreach (var building in buildings)
                    {
                        if (building == null) continue;
                        if (Vector3.Distance(footprintCenter, building.transform.position) < 1.8f)
                        {
                            tooClose = true;
                            break;
                        }
                    }

                    if (tooClose) continue;

                    // Place the building at the precise footprint center
                    GameObject buildingInstance = UnityEngine.Object.Instantiate(prefab, footprintCenter, Quaternion.identity);
                    buildingInstance.name = prefab.name;

                    // Set ownership
                    var buildingComponent = buildingInstance.GetComponent<Building>();
                    if (buildingComponent != null)
                    {
                        buildingComponent.owner = owner.EnemyEconomy;
                    }

                    // If it's a BuildingConstruction, ensure BuildingNode/position is aligned (BuildingConstruction.Awake tries to set buildingNode)
                    try
                    {
                        var bcInst = buildingInstance.GetComponent<BuildingConstruction>();
                        if (bcInst != null)
                        {
                            // Snap transform to the canonical center to avoid rounding/placement drift
                            if (gm != null && bcInst.buildingNode == null)
                                bcInst.buildingNode = gm.NodeFromWorldPoint(footprintCenter);

                            // ensure transform is precisely set to footprint center
                            buildingInstance.transform.position = footprintCenter;
                        }
                    }
                    catch { }

                    return buildingInstance;
                }
            }
        }

        return null;
    }

    private System.Collections.IEnumerator RegisterBarracksWhenReady(GameObject barracksInstance)
    {
        var bc = barracksInstance.GetComponent<BuildingConstruction>();
        if (bc != null)
        {
            // Wait for construction to finish
            while (!bc.isFinished)
            {
                yield return null;
            }
        }

        // Register the barracks with the owner
        if (!owner.Barracks.Contains(barracksInstance))
        {
            owner.Barracks.Add(barracksInstance);
        }
    }
}