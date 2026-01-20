using System;
using UnityEngine;

/// <summary>
/// TrainUnitsGoal: respects UnitData costs, builds barracks if needed and trains units (archers/warriors).
/// </summary>
public class TrainUnitsGoal : IGoal
{
    public string Name { get; private set; } = "TrainUnits";

    private readonly EnemyKingController owner;
    private readonly int batchSize;
    private float lastRun;
    private readonly float interval;
    private bool started;
    private bool aborted;

    public TrainUnitsGoal(EnemyKingController owner, int batchSize = 3, float interval = 8f)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.batchSize = Math.Max(1, batchSize);
        this.interval = Math.Max(0.5f, interval);
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

        // If no barracks and prefab present, attempt to place barracks when we have enough resources.
        // Use the actual prefab BuildingData costs when available (fixes mismatch vs owner.woodToBuildBarracks).
        if ((owner.Barracks == null || owner.Barracks.Count == 0) && owner.barracksPrefab != null)
        {
            int barrackWoodCost = owner.woodToBuildBarracks;
            int barrackIronCost = 0;
            int barrackGoldCost = 0;
            int barrackFoodCost = 0;

            try
            {
                var bcPrefab = owner.barracksPrefab.GetComponent<BuildingConstruction>();
                if (bcPrefab != null && bcPrefab.data != null)
                {
                    barrackWoodCost = bcPrefab.data.woodCost;
                    barrackIronCost = bcPrefab.data.ironCost;
                    barrackGoldCost = bcPrefab.data.goldCost;
                    barrackFoodCost = bcPrefab.data.foodCost;
                }
                else
                {
                    var bcomp = owner.barracksPrefab.GetComponent<Building>();
                    if (bcomp != null && bcomp.data != null)
                    {
                        barrackWoodCost = bcomp.data.woodCost;
                        barrackIronCost = bcomp.data.ironCost;
                        barrackGoldCost = bcomp.data.goldCost;
                        barrackFoodCost = bcomp.data.foodCost;
                    }
                }
            }
            catch { /* best-effort, fall back to owner.woodToBuildBarracks */ }

            if (econ.wood >= barrackWoodCost && econ.iron >= barrackIronCost)
            {
                var placed = TryPlaceBuilding(owner.barracksPrefab, false);
                if (placed != null)
                {
                    // Try to spend the actual building costs. If spend fails, cleanup placed prefab.
                    if (econ.TrySpend(barrackWoodCost, barrackIronCost, barrackGoldCost, barrackFoodCost, 0))
                    {
                        owner.StartCoroutine(RegisterBarracksWhenReady(placed));
                    }
                    else
                    {
                        try { UnityEngine.Object.Destroy(placed); } catch { }
                    }
                }
                return;
            }
        }

        // Prefer training archers if ArcherUnitData provided and affordable, otherwise warriors.
        UnitData preferred = owner.ArcherUnitData ?? owner.WarriorUnitData;
        if (preferred == null) return;

        int trained = 0;
        for (int i = 0; i < batchSize; i++)
        {
            // Re-evaluate affordability each iteration
            if (!CanAfford(econ, preferred)) break;

            Node spawnNode = FindSpawnNodeNearCastle();
            if (spawnNode == null) break;

            // Spend resources and population (popDelta = +1)
            if (!econ.TrySpend(preferred.woodCost, preferred.stoneCost, preferred.goldCost, preferred.foodCost, 1))
            {
                // couldn't spend - stop training this tick
                break;
            }

            try
            {
                Vector3 pos = spawnNode.centerPosition + (Vector3)(UnityEngine.Random.insideUnitCircle * 0.2f);
                GameObject go = GameObject.Instantiate(preferred.prefab, pos, Quaternion.identity, owner.transform);
                var ua = go.GetComponent<UnitAgent>();
                if (ua != null)
                {
                    ua.owner = econ;
                    // Set category: archers -> Archer, else Infantry
                    ua.category = (preferred == owner.ArcherUnitData) ? UnitCategory.Archer : UnitCategory.Infantry;
                }
                trained++;
            }
            catch
            {
                // if instantiation fails, refund population/resource to avoid leaks
                econ.AddResource(ResourceType.Food, preferred.foodCost);
                econ.AddResource(ResourceType.Wood, preferred.woodCost);
                econ.AddResource(ResourceType.Iron, preferred.stoneCost);
                econ.AddResource(ResourceType.Gold, preferred.goldCost);
                econ.population = Mathf.Max(0, econ.population - 1);
                break;
            }
        }

        if (owner == null || owner.EnemyEconomy == null)
        {
            return;
        }
    }

    public bool IsComplete => aborted;

    public void Abort() { aborted = true; }

    private bool CanAfford(PlayerEconomy econ, UnitData unitData)
    {
        if (econ == null || unitData == null) return false;
        return econ.wood >= unitData.woodCost &&
               econ.gold >= unitData.goldCost &&
               econ.food >= unitData.foodCost &&
               econ.population < econ.populationCap; // Check if we can afford population increase
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