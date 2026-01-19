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