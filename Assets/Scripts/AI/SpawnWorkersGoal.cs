using System;
using UnityEngine;

/// <summary>
/// Very small, robust goal that ensures the enemy keeps producing villagers (and will place a house
/// when at population cap). Does NOT spawn new villagers while the castle is still being built.
/// </summary>
public class SpawnWorkersGoal : IGoal
{
    public string Name { get; private set; } = "SpawnWorkers";

    private readonly EnemyKingController owner;
    private readonly float interval;
    private float lastRun;
    private bool started;
    private bool aborted;

    public SpawnWorkersGoal(EnemyKingController owner, float intervalSeconds = 10f)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.interval = Math.Max(0.1f, intervalSeconds);
    }

    public void Start()
    {
        started = true;
        lastRun = Time.time - interval;
    }

    public void Tick()
    {
        if (aborted || !started || owner == null) return;
        if (Time.time - lastRun < interval) return;
        lastRun = Time.time;

        var econ = owner.EnemyEconomy;
        if (econ == null) return;

        // IMPORTANT: do not spawn additional villagers while the castle (the base) is still under construction.
        var castle = owner.CastleInstance;
        if (castle != null)
        {
            var bc = castle.GetComponent<BuildingConstruction>();
                if (bc != null && !bc.isFinished)
                {
                    return;
                }
        }

        // If we're below population cap, spawn a worker (use WorkerUnitData costs)
        if (econ.population < econ.populationCap)
        {
            var spawnNode = FindSpawnNodeNearCastle();
            if (spawnNode != null)
            {
                // Use WorkerUnitData costs if available so workers are not free
                var ud = owner.WorkerUnitData;
                int woodCost = ud != null ? ud.woodCost : 0;
                int ironCost = ud != null ? ud.stoneCost : 0;
                int goldCost = ud != null ? ud.goldCost : 0;
                int foodCost = ud != null ? ud.foodCost : 0;

                // Try to actually spend resources before spawning (best-effort)
                if (econ.TrySpend(woodCost, ironCost, goldCost, foodCost, 1))
                {
                    SpawnWorkerAtNode(spawnNode, woodCost, ironCost, goldCost, foodCost);
                }
                else
                {
                }
                return;
            }
        }

        // At cap -> try to place a house to raise cap (if prefab available).
        if (owner.housePrefab != null)
        {
            // Only attempt if we're at or above cap
            if (econ.population >= econ.populationCap)
            {
                // Ensure we have the required wood (use configured threshold)
                int requiredWood = owner.woodToBuildHouse;
                    if (econ.wood < requiredWood)
                    {
                        return;
                    }

                // Spend wood (best-effort). If TrySpend fails, skip.
                    if (!econ.TrySpend(requiredWood, 0, 0, 0, 0))
                    {
                        return;
                    }

                GameObject placed = null;
                try
                {
                    placed = TryPlaceBuilding(owner.housePrefab, true);
                }
                catch (Exception ex)
                {
                    if (owner.verboseDebug) Debug.LogWarning($"[SpawnWorkersGoal] TryPlaceBuilding threw: {ex.Message}");
                }

                // If TryPlaceBuilding failed due to overlap, force placement fallback so enemy can progress.
                if (placed == null)
                {
                    try { placed = ForcePlaceBuilding(owner.housePrefab, true); } catch { placed = null; }
                }

                if (placed != null)
                {
                    var bc = placed.GetComponent<BuildingConstruction>();
                    if (bc != null)
                    {
                        try { AssignWorkersToConstruction(bc, Mathf.Min(3, owner.Workers != null ? owner.Workers.Count : 0)); } catch { }
                    }
                    
                }
                else
                {
                    econ.AddResource(ResourceType.Wood, requiredWood);
                }
            }
        }
    }

    public bool IsComplete => aborted;

    public void Abort()
    {
        aborted = true;
    }

    private Node FindSpawnNodeNearCastle()
    {
        var castle = owner.CastleInstance;
        if (castle == null) return null;

        var gm = GridManager.Instance;
        if (gm == null) return null;

        // Find a walkable node near the castle
        Node center = gm.NodeFromWorldPoint(castle.transform.position);
        if (center == null) return null;

        // Search in expanding circles for a walkable node
        for (int radius = 1; radius <= 5; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    if (Mathf.Abs(x) + Mathf.Abs(y) != radius) continue; // Only check perimeter
                    
                    Node candidate = gm.GetNode(center.gridX + x, center.gridY + y);
                    if (candidate != null && candidate.walkable)
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private void SpawnWorkerAtNode(Node spawnNode, int woodCost, int ironCost, int goldCost, int foodCost)
    {
        if (spawnNode == null) return;

        // Spawn the worker
        GameObject workerInstance = UnityEngine.Object.Instantiate(owner.VillagerPrefab, spawnNode.centerPosition, Quaternion.identity);
        workerInstance.name = "EnemyWorker";

        // Set up the unit agent
        var ua = workerInstance.GetComponent<UnitAgent>();
        if (ua != null)
        {
            ua.owner = owner.EnemyEconomy;
            ua.SetIdle(true);
        }

        // Add to owner's workers list
        if (owner.Workers != null && !owner.Workers.Contains(workerInstance))
        {
            owner.Workers.Add(workerInstance);
        }

        
    }

    private GameObject TryPlaceBuilding(GameObject prefab, bool isHouse)
    {
        if (prefab == null) return null;

        var castle = owner.CastleInstance;
        if (castle == null) return null;

        var gm = GridManager.Instance;
        if (gm == null) return null;

        // Find a suitable location near the castle
        Node center = gm.NodeFromWorldPoint(castle.transform.position);
        if (center == null) return null;

        // Search for a suitable building location
        for (int radius = 2; radius <= 8; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    if (Mathf.Abs(x) + Mathf.Abs(y) != radius) continue; // Only check perimeter
                    
                    Node candidate = gm.GetNode(center.gridX + x, center.gridY + y);
                    if (candidate != null && candidate.walkable)
                    {
                        // Check if this location is suitable for building
                        Vector3 worldPos = candidate.centerPosition;
                        
                        // Make sure no other buildings are too close
                        bool canPlace = true;
                        var buildings = UnityEngine.Object.FindObjectsOfType<Building>();
                        foreach (var building in buildings)
                        {
                            if (Vector3.Distance(worldPos, building.transform.position) < 2f)
                            {
                                canPlace = false;
                                break;
                            }
                        }
                        
                        if (canPlace)
                        {
                            // Place the building
                            GameObject buildingInstance = UnityEngine.Object.Instantiate(prefab, worldPos, Quaternion.identity);
                            buildingInstance.name = prefab.name;
                            
                            // Set ownership
                            var buildingComponent = buildingInstance.GetComponent<Building>();
                            if (buildingComponent != null)
                            {
                                buildingComponent.owner = owner.EnemyEconomy;
                            }
                            
                            // Mark node as occupied
                            // candidate.occupied = true;
                            
                            return buildingInstance;
                        }
                    }
                }
            }
        }

        return null;
    }

    private GameObject ForcePlaceBuilding(GameObject prefab, bool isHouse)
    {
        // Force placement - find any available spot, even if suboptimal
        var gm = GridManager.Instance;
        if (gm == null || prefab == null) return null;

        // Search the entire grid for any walkable unoccupied spot
        for (int x = 0; x < gm.GridSizeX; x++)
        {
            for (int y = 0; y < gm.GridSizeY; y++)
            {
                Node candidate = gm.GetNode(x, y);
                if (candidate != null && candidate.walkable && !NodeReservationSystem.Instance.IsReserved(candidate))
                {
                    Vector3 worldPos = candidate.centerPosition;
                    
                    // Place the building
                    GameObject buildingInstance = UnityEngine.Object.Instantiate(prefab, worldPos, Quaternion.identity);
                    buildingInstance.name = prefab.name + "_Forced";
                    
                    // Set ownership
                    var buildingComponent = buildingInstance.GetComponent<Building>();
                    if (buildingComponent != null)
                    {
                        buildingComponent.owner = owner.EnemyEconomy;
                    }
                    
                    // Mark node as occupied
                    candidate.walkable = false;
                    
                    return buildingInstance;
                }
            }
        }

        return null;
    }

    private void AssignWorkersToConstruction(BuildingConstruction bc, int workerCount)
    {
        if (bc == null || owner.Workers == null) return;

        int assigned = 0;
        foreach (var workerGO in owner.Workers)
        {
            if (assigned >= workerCount) break;
            if (workerGO == null) continue;

            var vt = workerGO.GetComponent<VillagerTaskSystem>();
            if (vt != null && vt.currentTask == VillagerTask.Idle)
            {
                vt.CommandBuild(bc);
                assigned++;
            }
        }
    }
}