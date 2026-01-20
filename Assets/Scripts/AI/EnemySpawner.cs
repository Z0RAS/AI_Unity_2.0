using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Handles spawning of enemy base (castle) and initial villagers.
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    [Header("Prefabs")]
    public GameObject castlePrefab;
    public GameObject villagerPrefab;

    [Header("Initial spawn")]
    public Vector2 castleSpawnPosition = Vector2.zero;
    public int initialWorkers = 3;

    // runtime state
    public PlayerEconomy enemyEconomy;
    private Transform spawnParent;
    private GameObject castleInstance;
    private DropOffBuilding castleDropoff;
    private readonly List<GameObject> workers = new List<GameObject>();

    public GameObject CastleInstance => castleInstance;
    public DropOffBuilding CastleDropoff => castleDropoff;
    public List<GameObject> Workers => workers;

    void Start()
    {
        if (enemyEconomy == null)
        {
            var go = new GameObject("EnemyPlayer");
            enemyEconomy = go.AddComponent<PlayerEconomy>();
            enemyEconomy.isPlayer = false;
        }
        Initialize(enemyEconomy);
        SpawnCastleAndInitialWorkers();
    }

    void Initialize(PlayerEconomy economy)
    {
        enemyEconomy = economy;
        enemyEconomy.isPlayer = false;
        if (spawnParent == null)
        {
            var root = new GameObject("EnemySpawns");
            spawnParent = root.transform;
        }
    }

    public void SpawnCastleAndInitialWorkers()
    {
        // Compute initial position (existing behaviour kept as fallback)
        Vector3 desiredPos = new Vector3(castleSpawnPosition.x, castleSpawnPosition.y, 0f);
        if (desiredPos == Vector3.zero && GridManager.Instance != null)
        {
            var center = GridManager.Instance.NodeFromWorldPoint(Vector3.zero);
            if (center != null) desiredPos = center.centerPosition;
        }
        if (desiredPos == Vector3.zero) desiredPos = transform.position;

        // Try to snap castle spawn to canonical footprint center so recruiters reserve outside nodes correctly.
        Vector3 spawnPos = desiredPos;
        var gm = GridManager.Instance;
        try
        {
            if (gm != null && castlePrefab != null)
            {
                // Determine footprint of castle prefab
                Vector2Int footprint = Vector2Int.one;
                var bcPrefab = castlePrefab.GetComponent<BuildingConstruction>();
                if (bcPrefab != null)
                {
                    footprint = bcPrefab.Size;
                }
                else
                {
                    var bcomp = castlePrefab.GetComponent<Building>();
                    if (bcomp != null && bcomp.data != null)
                        footprint = bcomp.data.size;
                }

                // If we have a grid and a sensible footprint, compute canonical start indices & center.
                Node baseNode = gm.NodeFromWorldPoint(desiredPos);
                if (baseNode != null)
                {
                    Vector2Int start = gm.GetStartIndicesForCenteredFootprint(desiredPos, footprint);
                    Node first = gm.GetNode(start.x, start.y);
                    Node last = gm.GetNode(start.x + footprint.x - 1, start.y + footprint.y - 1);
                    if (first != null && last != null)
                    {
                        spawnPos = (first.centerPosition + last.centerPosition) * 0.5f;
                    }
                }
            }
        }
        catch { spawnPos = desiredPos; }

        // Instantiate castle at the aligned spawnPos
        if (castlePrefab == null)
        {
            Debug.LogWarning("[EnemySpawner] castlePrefab not assigned.");
            return;
        }

        castleInstance = Instantiate(castlePrefab, spawnPos, Quaternion.identity, spawnParent);

        var bc = castleInstance.GetComponent<BuildingConstruction>();
        if (bc != null)
        {
            // Ensure buildingNode follows the canonical footprint node if possible
            try
            {
                if (gm != null)
                    bc.buildingNode = gm.NodeFromWorldPoint(spawnPos);
            }
            catch { }

            bc.assignedOwner = enemyEconomy;
            bc.isUnderConstruction = true; // Castle starts as construction site
            bc.currentHealth = 0f; // Needs to be built from scratch

            // Make colliders non-blocking while under construction (helps recruiter/reservations)
            try { bc.SetCollidersAsTrigger(true); } catch { }
        }

        var buildingComp = castleInstance.GetComponent<Building>();
        if (buildingComp != null) buildingComp.owner = enemyEconomy;

        castleDropoff = castleInstance.GetComponent<DropOffBuilding>();
        if (castleDropoff != null) castleDropoff.owner = enemyEconomy;

        for (int i = 0; i < initialWorkers; i++) SpawnWorkerAtOffset(i);
    }

    void SpawnWorkerAtOffset(int index)
    {
        if (villagerPrefab == null) return;
        Vector2 jitter = UnityEngine.Random.insideUnitCircle * 1.2f;
        Vector3 spawnPos = (castleInstance != null ? castleInstance.transform.position : transform.position) + new Vector3(jitter.x, jitter.y, 0f);
        if (GridManager.Instance != null && castleInstance != null)
        {
            var node = FindSpawnNodeNearCastle();
            if (node != null)
            {
                SpawnWorkerAtNode(node, 0, 0, 0, 0);
                return;
            }
        }
        GameObject w = Instantiate(villagerPrefab, spawnPos, Quaternion.identity, spawnParent);
        WireSpawnedWorker(w);
    }

    public Node FindSpawnNodeNearCastle(int searchRadius = 4)
    {
        var gm = GridManager.Instance;
        if (castleInstance == null || gm == null) return null;
        Node center = gm.NodeFromWorldPoint(castleInstance.transform.position);
        if (center == null) return null;

        var nrs = NodeReservationSystem.Instance;

        Node best = null;
        float bestSqr = float.MaxValue;

        // Search nearby grid positions within searchRadius (grid-coordinates)
        int cx = center.gridX;
        int cy = center.gridY;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                Node n = gm.GetNode(cx + dx, cy + dy);
                if (n == null) continue;
                if (!n.walkable) continue;

                // skip reserved nodes
                if (nrs != null && nrs.IsReserved(n)) continue;

                float sq = (n.centerPosition - center.centerPosition).sqrMagnitude;
                if (sq < bestSqr)
                {
                    bestSqr = sq;
                    best = n;
                }
            }
        }

        // final fallback: nearest walkable node via GridManager
        if (best == null)
            best = gm.FindClosestWalkableNode(center.gridX, center.gridY);

        return best;
    }

    public void SpawnWorkerAtNode(Node spawnNode, int woodCost, int ironCost, int goldCost, int foodCost)
    {
        if (villagerPrefab == null || spawnNode == null) return;

        Vector2 jitter = UnityEngine.Random.insideUnitCircle * 0.05f;
        Vector3 spawnPos = spawnNode.centerPosition + new Vector3(jitter.x, jitter.y, 0f);
        GameObject w = Instantiate(villagerPrefab, spawnPos, Quaternion.identity, spawnParent);

        var ua = w.GetComponent<UnitAgent>();
        if (ua != null)
        {
            ua.category = UnitCategory.Villager;
            ua.owner = enemyEconomy;
            var nrs = NodeReservationSystem.Instance;
            bool reserved = true;
            try
            {
                reserved = (nrs != null) ? nrs.ReserveNode(spawnNode, ua) : true;
            }
            catch { reserved = false; }

            if (!reserved)
            {
                Destroy(w);
                return;
            }

            ua.lastAssignedNode = spawnNode;
        }

        var vt = w.GetComponent<VillagerTaskSystem>();

        workers.Add(w);
        if (enemyEconomy != null) enemyEconomy.population = Mathf.Clamp(enemyEconomy.population + 1, 0, enemyEconomy.populationCap);
    }

    void WireSpawnedWorker(GameObject w)
    {
        if (w == null) return;
        workers.Add(w);
        var ua = w.GetComponent<UnitAgent>();
        if (ua != null) { ua.category = UnitCategory.Villager; ua.owner = enemyEconomy; }
    }
}