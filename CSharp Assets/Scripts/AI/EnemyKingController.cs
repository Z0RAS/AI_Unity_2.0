using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using AI.BehaviorTree;

public class EnemyKingController : MonoBehaviour
{
    [Header("Prefabs / References")]
    public GameObject castlePrefab;           // prefab containing DropOffBuilding + Building/BuildingConstruction
    public GameObject villagerPrefab;         // prefab containing UnitAgent + VillagerTaskSystem
    public UnitData workerUnitData;           // OPTIONAL: prefer UnitData for accurate AI spawn costs
    public UnitData warriorUnitData;          // UnitData for melee troop (warrior)
    public UnitData archerUnitData;           // UnitData for ranged troop (archer)
    public GameObject barracksPrefab;         // prefab with UnitProduction + BuildingConstruction
    public GameObject housePrefab;            // optional house prefab
    public Transform spawnParent;

    [Header("Appearance")]
    public Sprite kingPortrait; // assign the enemy king portrait so dialogue shows image

    [Header("Initial spawn")]
    [Tooltip("World position to place castle. If left empty (Vector3.zero) the script will try to find a free node.")]
    public Vector3 castleSpawnPosition = Vector3.zero;
    public int initialWorkers = 3;

    [Header("Economy / thresholds")]
    public int woodToBuildBarracks = 50;
    public int woodToBuildHouse = 20;
    public int minWoodToTrainTroop = 30;
    public int troopBatchSize = 3;

    [Header("AI timing")]
    public float decisionInterval = 5f;
    public float scoutInterval = 6f;

    // runtime fields
    PlayerEconomy enemyEconomy;
    GameObject castleInstance;
    DropOffBuilding castleDropoff;
    List<GameObject> workers = new List<GameObject>();
    List<GameObject> barracks = new List<GameObject>();
    bool hostile = true;
    bool firstContactAnnounced = false; // track first contact announcement

    // Behavior tree
    private BehaviorTree behaviorTree;

    // --- Scouting & squad behavior (tweakable)
    [Header("Scouting / Squad")]
    public int minScoutCount = 1;
    public int maxScoutCount = 3;
    public float scoutRadius = 12f;
    public float scoutMoveInterval = 8f;
    public int squadSize = 3;
    public float rallyRadius = 3f;
    public float squadGatherTimeout = 8f;
    public float workerSpawnInterval = 10f;

    // new: avoid re-scouting same place too often
    [Header("Scout Settings")]
    [Tooltip("How long (seconds) before a previously scouted grid cell is considered 'unscouted' and can be scouted again.")]
    public float scoutCellCooldown = 60f;
    // map keyed by grid coordinates (gridX,gridY) -> last scouted time
    private readonly Dictionary<Vector2Int, float> scoutedCells = new Dictionary<Vector2Int, float>();

    private Coroutine scoutCoroutine;
    private Coroutine workerSpawnCoroutine;

    // -------------------
    // Detection for leader/base
    // -------------------
    [Header("Detection")]
    [Tooltip("How far each enemy unit can 'detect' player leader/base (world units)")]
    public float detectRadius = 6f;
    [Tooltip("How often detection runs (seconds)")]
    public float detectInterval = 0.6f;

    // discovered targets (set when enemy units come near them)
    private GameObject discoveredLeader;
    private GameObject discoveredPlayerBase;
    private Coroutine detectCoroutine;

    // --- Add these fields near the other detection fields (Detection section)
    [Header("Resource Scouting")]
    [Tooltip("How many villagers to send as scouts to discover resource nodes (food/wood/iron).")]
    public int resourceScoutCount = 3;
    [Tooltip("How often (seconds) the AI will re-send scouts if resources not discovered.")]
    public float resourceScoutInterval = 18f;

    // Discovered resource registry
    private readonly Dictionary<ResourceType, HashSet<ResourceNode>> discoveredResourceNodes = new Dictionary<ResourceType, HashSet<ResourceNode>>()
    {
        { ResourceType.Food, new HashSet<ResourceNode>() },
        { ResourceType.Wood, new HashSet<ResourceNode>() },
        { ResourceType.Iron, new HashSet<ResourceNode>() }
    };

    private Coroutine resourceScoutCoroutine;

    void Start()
    {
        if (spawnParent == null)
        {
            var root = new GameObject("EnemySpawns");
            spawnParent = root.transform;
        }

        // Ensure a GoalPlanner exists in the scene so goals can be enqueued by the behavior tree.
        // If no GoalPlanner present, create one (safe, lightweight).
        if (FindObjectOfType<GoalPlanner>() == null)
        {
            var plannerGO = new GameObject("GoalPlanner");
            plannerGO.AddComponent<GoalPlanner>();
        }

        // create dedicated enemy PlayerEconomy GameObject BEFORE spawning any buildings/units
        GameObject go = new GameObject("EnemyPlayer");
        go.transform.parent = spawnParent;
        enemyEconomy = go.AddComponent<PlayerEconomy>();
        // keep PlayerEconomy defaults (do not overwrite here)

        // determine spawn position
        Vector3 pos = castleSpawnPosition;
        if (pos == Vector3.zero && GridManager.Instance != null)
        {
            var center = GridManager.Instance.NodeFromWorldPoint(Vector3.zero);
            if (center != null) pos = center.centerPosition;
        }
        if (pos == Vector3.zero) pos = transform.position;

        // Spawn castle (do NOT assign dropoff.owner yet)
        if (castlePrefab != null)
        {
            castleInstance = Instantiate(castlePrefab, pos, Quaternion.identity, spawnParent);

            var bc = castleInstance.GetComponent<BuildingConstruction>();

            // Spawn initial workers around castle (spawn BEFORE starting construction so AlertNearbyIdleVillagers finds them)
            for (int i = 0; i < initialWorkers; i++)
                SpawnWorkerAtOffset(i);

            if (bc != null)
            {
                bc.assignedOwner = enemyEconomy;
                bc.BeginConstruction();

                // Assign workers once and only once here.
                AssignWorkersToConstruction(bc, initialWorkers + 1);

                // still start owners set when finished
                StartCoroutine(SetOwnersWhenFinished(castleInstance));
            }
            else
            {
                var buildingComp = castleInstance.GetComponent<Building>();
                if (buildingComp != null) buildingComp.owner = enemyEconomy;

                castleDropoff = castleInstance.GetComponent<DropOffBuilding>();
                if (castleDropoff != null) castleDropoff.owner = enemyEconomy;
            }
        }
        else
        {
            Debug.LogWarning("[EnemyKing] castlePrefab not assigned.");
        }

        BuildBehaviorTree();

        StartCoroutine(BTLoop());

        scoutCoroutine = StartCoroutine(ScoutLoop());
        workerSpawnCoroutine = StartCoroutine(WorkerSpawnLoop());

        StartCoroutine(FirstContactWatcher());

        // start detection loop for discovering player's leader and base via proximity of our units (simulates scouts)
        detectCoroutine = StartCoroutine(DetectPlayerAssetsLoop());
        resourceScoutCoroutine = StartCoroutine(ResourceScoutLoop());

        StartCoroutine(BuildThenTrainOnce()); // NEW: start one-shot build/train sequence
    }

    // Spawn helper used on Start() for initial workers; does not spend resources.
    void SpawnWorkerAtOffset(int index)
    {
        if (villagerPrefab == null || castleInstance == null || GridManager.Instance == null)
        {
            // fallback spawn without grid
            Vector2 jitter = Random.insideUnitCircle * 1.2f;
            Vector3 spawnPos = castleInstance != null ? castleInstance.transform.position + new Vector3(jitter.x, jitter.y, 0f) : transform.position + new Vector3(jitter.x, jitter.y, 0f);
            GameObject w = Instantiate(villagerPrefab, spawnPos, Quaternion.identity, spawnParent);
            workers.Add(w);
            var ua = w.GetComponent<UnitAgent>();
            if (ua != null)
            {
                ua.category = UnitCategory.Villager;
                ua.owner = enemyEconomy;
            }
            var vt = w.GetComponent<VillagerTaskSystem>();
            if (vt != null && castleDropoff != null) vt.Init(castleDropoff);
            enemyEconomy.population = Mathf.Clamp(enemyEconomy.population + 1, 0, enemyEconomy.populationCap);
            return;
        }

        Node spawnNode = FindSpawnNodeNearCastle();
        if (spawnNode == null)
        {
            Vector2 jitter = Random.insideUnitCircle * 1.2f;
            Vector3 spawnPos = castleInstance.transform.position + new Vector3(jitter.x, jitter.y, 0f);
            GameObject w = Instantiate(villagerPrefab, spawnPos, Quaternion.identity, spawnParent);
            workers.Add(w);
            var ua = w.GetComponent<UnitAgent>();
            if (ua != null)
            {
                ua.category = UnitCategory.Villager;
                ua.owner = enemyEconomy;
            }
            var vt = w.GetComponent<VillagerTaskSystem>();
            if (vt != null && castleDropoff != null) vt.Init(castleDropoff);
            enemyEconomy.population = Mathf.Clamp(enemyEconomy.population + 1, 0, enemyEconomy.populationCap);
            return;
        }

        // place without cost for initial spawn
        SpawnWorkerAtNode(spawnNode, 0, 0, 0, 0);
    }

    // Assign workers deterministically (reserve approach nodes and command each worker once).
    private void AssignWorkersToConstruction(BuildingConstruction bc, int maxAssign = 3)
    {
        if (bc == null) return;
        if (workers == null || workers.Count == 0) return;

        int assigned = 0;

        var gm = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;
        Node centerNode = gm != null ? gm.NodeFromWorldPoint(bc.transform.position) : null;

        // choose candidates closest first
        var candidates = workers
            .Where(w => w != null)
            .Select(w => new { go = w, dist = Vector2.SqrMagnitude((Vector2)(w.transform.position - bc.transform.position)) })
            .OrderBy(x => x.dist)
            .Select(x => x.go)
            .ToList();

        // First pass: prefer idle workers only (do not interrupt gatherers).
        foreach (var w in candidates)
        {
            if (assigned >= maxAssign) break;

            var vTask = w.GetComponent<VillagerTaskSystem>();
            var ua = w.GetComponent<UnitAgent>();
            if (vTask == null || ua == null) continue;
            if (ua.owner != enemyEconomy) continue;

            // Skip if worker already assigned to this construction.
            if (vTask.AssignedConstruction == bc)
                continue;

            // Only take idle workers in the first pass
            if (!vTask.IsIdle()) continue;

            Node preferred = null;
            if (nrs != null && centerNode != null)
            {
                preferred = nrs.TryReserveBestApproach(centerNode, ua, Mathf.Max(3, bc.size.x));
            }

            // Command build once with preferred approach (may be null - vTask will attempt reservation)
            vTask.CommandBuild(bc, preferred);
            assigned++;
        }

        // Second pass: if still need builders, allow interrupting gatherers (but only if required).
        if (assigned < maxAssign)
        {
            foreach (var w in candidates)
            {
                if (assigned >= maxAssign) break;

                var vTask = w.GetComponent<VillagerTaskSystem>();
                var ua = w.GetComponent<UnitAgent>();
                if (vTask == null || ua == null) continue;
                if (ua.owner != enemyEconomy) continue;

                // Skip idle ones (they already were considered) and only consider gatherers now.
                if (vTask.IsIdle()) continue;
                if (vTask.currentTask != VillagerTask.Gather) continue;

                // Skip if worker already assigned to this construction.
                if (vTask.AssignedConstruction == bc)
                    continue;

                Node preferred = null;
                if (nrs != null && centerNode != null)
                {
                    preferred = nrs.TryReserveBestApproach(centerNode, ua, Mathf.Max(3, bc.size.x));
                }

                vTask.CommandBuild(bc, preferred);
                assigned++;
            }
        }

        if (assigned == 0)
        {
            // fallback: alert nearby idle/gatherers so they pick it up
            try { bc.AlertNearbyIdleVillagers(); } catch { }
        }
    }

    IEnumerator BTLoop()
    {
        while (true)
        {
            if (behaviorTree != null)
                behaviorTree.Tick();
            yield return new WaitForSeconds(decisionInterval);
        }
    }

    // Periodically attempt to produce workers (AI) — demand-driven and uses UnitData if assigned.
    IEnumerator WorkerSpawnLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(workerSpawnInterval);

            // do not spawn if no castle or reached pop cap
            if (castleInstance == null || enemyEconomy == null) continue;
            if (enemyEconomy.population >= enemyEconomy.populationCap) continue;

            // Demand check: compute desired workers from resource availability (simple heuristic).
            int resourceNodes = ResourceNode.AllNodes?.Count ?? 0;
            int desiredPerNode = 1;
            int desiredWorkers = Mathf.Clamp(Mathf.Max(initialWorkers, resourceNodes * desiredPerNode), 1, enemyEconomy.populationCap);

            if (workers.Count >= desiredWorkers) continue;

            // Find a valid spawn node first — do not spend resources unless we can place the unit.
            Node spawnNode = FindSpawnNodeNearCastle();
            if (spawnNode == null) continue; // no valid place to spawn now

            // Determine costs: prefer workerUnitData if assigned, otherwise fall back to zero.
            int woodCost = workerUnitData != null ? workerUnitData.woodCost : 0;
            int ironCost = workerUnitData != null ? workerUnitData.stoneCost : 0;
            int goldCost = workerUnitData != null ? workerUnitData.goldCost : 0;
            int foodCost = workerUnitData != null ? workerUnitData.foodCost : 0;

            // Only attempt to spend if we can (TrySpend returns false if insufficient or pop cap)
            if (enemyEconomy.TrySpend(woodCost, ironCost, goldCost, foodCost, 1))
            {
                SpawnWorkerAtNode(spawnNode, woodCost, ironCost, goldCost, foodCost);
            }
            else
            {
                // not enough resources — skip this tick
                continue;
            }
        }
    }

    // Try to find a safe spawn node near the castle (walkable, outside footprint, not reserved).
    Node FindSpawnNodeNearCastle(int searchRadius = 4)
    {
        if (castleInstance == null || GridManager.Instance == null) return null;

        var gm = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;
        Node center = gm.NodeFromWorldPoint(castleInstance.transform.position);
        if (center == null) return null;

        // Prefer an unreserved node close to castle
        Node candidate = nrs != null ? nrs.FindClosestFreeNode(center, searchRadius) : gm.FindClosestWalkableNode(center.gridX, center.gridY);
        if (candidate != null) return candidate;

        // Fallback: scan outwards for any walkable node outside the castle footprint
        var bc = castleInstance.GetComponent<BuildingConstruction>();
        int startX = int.MinValue, startY = int.MinValue, endX = int.MinValue, endY = int.MinValue;
        if (bc != null)
        {
            int halfX = bc.size.x / 2;
            int halfY = bc.size.y / 2;
            startX = center.gridX - halfX;
            startY = center.gridY - halfY;
            endX = startX + bc.size.x;
            endY = startY + bc.size.y;
        }

        float bestSqr = float.MaxValue;
        Node best = null;

        for (int r = 1; r <= 8; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    int nx = center.gridX + dx;
                    int ny = center.gridY + dy;
                    Node n = gm.GetNode(nx, ny);
                    if (n == null) continue;
                    if (!n.walkable) continue;

                    if (bc != null)
                    {
                        if (!(n.gridX < startX || n.gridX >= endX || n.gridY < startY || n.gridY >= endY))
                            continue; // inside footprint
                    }

                    if (nrs != null && nrs.IsReserved(n)) continue;

                    // avoid nodes with units physically present
                    Collider2D[] hits = Physics2D.OverlapCircleAll(n.centerPosition, 0.3f);
                    bool occupied = false;
                    foreach (var h in hits) { if (h != null && h.GetComponent<UnitAgent>() != null) { occupied = true; break; } }
                    if (occupied) continue;

                    float d = (n.centerPosition - castleInstance.transform.position).sqrMagnitude;
                    if (d < bestSqr)
                    {
                        bestSqr = d;
                        best = n;
                    }
                }
            }
            if (best != null) break;
        }

        return best;
    }

    // Spawn at a node. If reservation fails after instantiation, refund and destroy the spawned unit.
    void SpawnWorkerAtNode(Node spawnNode, int woodCost, int ironCost, int goldCost, int foodCost)
    {
        if (villagerPrefab == null || spawnNode == null)
        {
            // refund if costs were taken
            if (enemyEconomy != null && (woodCost > 0 || ironCost > 0 || goldCost > 0 || foodCost > 0))
            {
                if (woodCost > 0) enemyEconomy.AddResource(ResourceType.Wood, woodCost);
                if (ironCost > 0) enemyEconomy.AddResource(ResourceType.Iron, ironCost);
                if (goldCost > 0) enemyEconomy.AddResource(ResourceType.Gold, goldCost);
                if (foodCost > 0) enemyEconomy.AddResource(ResourceType.Food, foodCost);
                enemyEconomy.population = Mathf.Max(0, enemyEconomy.population - 1);
            }
            return;
        }

        Vector2 jitter = Random.insideUnitCircle * 0.05f;
        Vector3 spawnPos = spawnNode.centerPosition + new Vector3(jitter.x, jitter.y, 0f);
        GameObject w = Instantiate(villagerPrefab, spawnPos, Quaternion.identity, spawnParent);

        var vTask = w.GetComponent<VillagerTaskSystem>();
        var ua = w.GetComponent<UnitAgent>();

        // Wire villager to our dropoff / economy (castleDropoff may be set later)
        if (vTask != null && castleDropoff != null)
            vTask.Init(castleDropoff);

        // When unit spawns, set its UnitAgent.category and owner economics if needed
        if (ua != null)
        {
            ua.category = UnitCategory.Villager;
            ua.owner = enemyEconomy; // crucial: mark worker as belonging to this AI

            // Try to reserve the node for this actual unit
            var nrs = NodeReservationSystem.Instance;
            bool reserved = false;
            if (nrs != null)
            {
                reserved = nrs.TryReserve(spawnNode, ua);
            }
            else
            {
                reserved = true;
            }

            if (!reserved)
            {
                // couldn't claim spawn node — rollback spawn and refund costs
                Destroy(w);
                if (enemyEconomy != null)
                {
                    if (woodCost > 0) enemyEconomy.AddResource(ResourceType.Wood, woodCost);
                    if (ironCost > 0) enemyEconomy.AddResource(ResourceType.Iron, ironCost);
                    if (goldCost > 0) enemyEconomy.AddResource(ResourceType.Gold, goldCost);
                    if (foodCost > 0) enemyEconomy.AddResource(ResourceType.Food, foodCost);
                    enemyEconomy.population = Mathf.Max(0, enemyEconomy.population - 1);
                }
                return;
            }

            ua.lastAssignedNode = spawnNode;
        }

        workers.Add(w);

        // population was already incremented by TrySpend via popDelta
        // Defensive: clamp
        enemyEconomy.population = Mathf.Clamp(enemyEconomy.population, 0, enemyEconomy.populationCap);
    }

    // Build the behavior tree that replaces the previous goal planner loop.
    private void BuildBehaviorTree()
    {
        // condition: we have a discovered player leader/base OR we have soldiers available
        ConditionNode hasTargetsOrSoldiers = new ConditionNode(() =>
        {
            if (discoveredLeader != null || discoveredPlayerBase != null) return true;
            // check if enemy has any combat units (non-villagers)
            return UnityEngine.Object.FindObjectsOfType<UnitAgent>().Any(u => u != null && u.owner == enemyEconomy && u.category != UnitCategory.Villager);
        });

        ActionNode attackAction = new ActionNode(() =>
        {
            var planner = FindObjectOfType<GoalPlanner>();
            if (planner != null)
            {
                planner.EnqueueGoal(new AttackGoal(this));
                return NodeState.Success;
            }

            if (discoveredLeader != null) InstructAttackOn(discoveredLeader);
            else if (discoveredPlayerBase != null) InstructAttackOn(discoveredPlayerBase);
            return NodeState.Success;
        });

        // Only run attackAction when hasTargetsOrSoldiers succeeds
        Sequence attackSequence = new Sequence(hasTargetsOrSoldiers, attackAction);

        ActionNode gather = new ActionNode(() =>
        {
            var planner = FindObjectOfType<GoalPlanner>();
            if (planner != null)
            {
                planner.EnqueueGoal(new GatherGoal(this));
                return NodeState.Success;
            }
            EnsureWorkersGather();
            return NodeState.Success;
        });

        var root = new Selector(attackSequence, gather);
        behaviorTree = new BehaviorTree(root);
    }

    IEnumerator ScoutLoop()
    {
        // keep sending non-villager scout units to random perimeter points around castle until contact is made
        while (!firstContactAnnounced)
        {
            // pick available non-villager units for scouting only (do NOT use villagers)
            var candidates = Object.FindObjectsOfType<UnitAgent>().Where(u => u != null && u.owner == enemyEconomy && u.category != UnitCategory.Villager).ToList();

            // if not enough combat units available — wait and retry
            if (candidates.Count < minScoutCount)
            {
                yield return new WaitForSeconds(scoutMoveInterval);
                continue;
            }

            // choose up to maxScoutCount from available soldiers
            var chosen = candidates.OrderBy(u => Vector2.SqrMagnitude((Vector2)(u.transform.position - (castleInstance != null ? (Vector3)castleInstance.transform.position : transform.position)))).Take(maxScoutCount).ToList();

            // send each scout to a random point near castle within scoutRadius; avoid recently scouted cells
            for (int i = 0; i < chosen.Count; i++)
            {
                var u = chosen[i];
                if (u == null || castleInstance == null) continue;

                Vector3 dest;
                Node destNode = null;
                int attempts = 0;
                do
                {
                    attempts++;
                    Vector2 rnd = Random.insideUnitCircle.normalized * (Random.Range(scoutRadius * 0.4f, scoutRadius));
                    dest = castleInstance.transform.position + new Vector3(rnd.x, rnd.y, 0f);
                    if (GridManager.Instance != null)
                        destNode = GridManager.Instance.NodeFromWorldPoint(dest);
                    // map to integer cell (use node grid coords)
                    if (destNode == null) continue;
                    var cell = new Vector2Int(destNode.gridX, destNode.gridY);
                    if (scoutedCells.TryGetValue(cell, out float last) && Time.time - last < scoutCellCooldown)
                    {
                        destNode = null; // mark as unacceptable, try again
                    }
                    if (attempts > 12) break;
                } while (destNode == null);

                if (destNode == null) continue;

                // record scouted cell immediately (prevents multiple scouts to same cell)
                var sc = new Vector2Int(destNode.gridX, destNode.gridY);
                scoutedCells[sc] = Time.time;

                u.SetDestinationToNode(destNode);
            }

            // while waiting, periodically re-evaluate whether firstContactAnnounced became true
            float elapsed = 0f;
            while (elapsed < scoutMoveInterval && !firstContactAnnounced)
            {
                elapsed += 0.25f;
                yield return new WaitForSeconds(0.25f);

                // prune old entries occasionally
                if (elapsed % 2f < 0.25f)
                {
                    var keys = scoutedCells.Keys.ToArray();
                    foreach (var k in keys)
                        if (Time.time - scoutedCells[k] > scoutCellCooldown * 2f) // keep slightly longer
                            scoutedCells.Remove(k);
                }
            }
        }
    }

    // NOTE: this coroutine is now started automatically for the enemy king via Start()
    IEnumerator ResourceScoutLoop()
    {
        // Repeatedly dispatch a few idle villagers to unknown resource nodes until we've discovered at least one of each
        while (true)
        {
            // If we've found at least one node of each critical type, wait longer and keep occasional checks
            if (HaveDiscoveredAllResourceTypes())
            {
                yield return new WaitForSeconds(Mathf.Max(resourceScoutInterval * 2f, 30f));
                continue;
            }

            // Dispatch a short, one-shot scout wave
            SendResourceScouts(resourceScoutCount);

            // Wait before trying again
            yield return new WaitForSeconds(resourceScoutInterval);
        }
    }

    // Check if we've discovered enough resources of each type (Food, Wood, Iron)
    private bool ResourceDiscoverySufficient()
    {
        // quick check: if we have any resource nodes, consider it discovered
        if (ResourceNode.AllNodes != null && ResourceNode.AllNodes.Count > 0)
            return true;

        // otherwise check individual tallies
        foreach (var kvp in discoveredResourceNodes)
        {
            if (kvp.Value.Count > 0)
                return true; // found at least one node of this type
        }

        return false;
    }

    // --- Helper to check discovery completion
    private bool HaveDiscoveredAllResourceTypes()
    {
        foreach (ResourceType rt in new[] { ResourceType.Food, ResourceType.Wood, ResourceType.Iron })
        {
            if (!discoveredResourceNodes.TryGetValue(rt, out var set) || set == null || set.Count == 0)
                return false;
        }
        return true;
    }

    // Train troops but spawn a balanced mix of ranged + melee to avoid endless archers.
    // Prefer the explicit UnitData fields (warriorUnitData / archerUnitData) if provided.
    public void TrainTroopsImmediate(int batchSize)
    {
        if (barracks == null || barracks.Count == 0) return;

        foreach (var b in barracks)
        {
            if (b == null) continue;
            var prod = b.GetComponent<UnitProduction>();
            if (prod == null) continue;

            // Try explicit preferences first
            UnitData preferred = null;

            // prefer archer if available and affordable
            if (archerUnitData != null && prod.producibleUnits != null && prod.producibleUnits.Contains(archerUnitData) && CanAfford(archerUnitData))
                preferred = archerUnitData;

            // prefer warrior if available and affordable (fallback)
            if (preferred == null && warriorUnitData != null && prod.producibleUnits != null && prod.producibleUnits.Contains(warriorUnitData) && CanAfford(warriorUnitData))
                preferred = warriorUnitData;

            // Fallback: examine producibleUnits and pick first non-villager unit we can afford
            if (preferred == null)
            {
                foreach (var ud in prod.producibleUnits)
                {
                    if (ud == null) continue;
                    if (ud.category == UnitCategory.Villager) continue;
                    if (CanAfford(ud))
                    {
                        preferred = ud;
                        break;
                    }
                }
            }

            if (preferred == null)
                continue;

            int spawned = prod.SpawnUnitsImmediate(preferred, batchSize);
            if (spawned > 0)
            {
                // After spawn, form squad: gather troops at rally point, then send as whole squad.
                var spawnedAgents = Object.FindObjectsOfType<UnitAgent>().Where(u => u != null && u.owner == enemyEconomy && u.category != UnitCategory.Villager).ToList();
                // pick closest 'spawned' ones heuristically: take newest by distance to castle
                Node rallyBaseNode = GridManager.Instance.NodeFromWorldPoint(castleInstance.transform.position);
                Node rallyNode = NodeReservationSystem.Instance?.FindClosestFreeNode(rallyBaseNode, 2) ?? rallyBaseNode;
                Vector3 rallyPoint = rallyNode != null ? rallyNode.centerPosition : castleInstance.transform.position;

                // pick up to squadSize units to form a squad
                var squad = spawnedAgents.OrderBy(u => Vector2.Distance(u.transform.position, rallyPoint)).Take(squadSize).ToList();

                StartCoroutine(FormAndSendSquad(squad, FindNearestPlayerTarget(castleInstance.transform.position)));
            }
        }
    }

    IEnumerator FormAndSendSquad(List<UnitAgent> squad, GameObject target)
    {
        if (squad == null || squad.Count == 0) yield break;
        if (castleInstance == null) yield break;

        // Reserve a rally node near castle for the squad gather
        Node baseNode = GridManager.Instance.NodeFromWorldPoint(castleInstance.transform.position);
        Node rallyNode = NodeReservationSystem.Instance?.FindClosestFreeNode(baseNode, 3) ?? baseNode;

        // Compute rally point world position
        Vector3 rallyPoint = rallyNode != null ? rallyNode.centerPosition : castleInstance.transform.position;

        // If SquadFormationController exists, use it to assemble & move the group; otherwise fall back to per-unit commands.
        var sfc = SquadFormationController.Instance;
        if (sfc != null)
        {
            // Command units to rally in formation
            sfc.MoveGroupWithFormation(squad, rallyPoint);

            float start = Time.time;
            // wait until enough units are within rallyRadius or timeout
            while (Time.time - start < squadGatherTimeout)
            {
                int countAtRally = 0;
                foreach (var u in squad)
                {
                    if (u == null) continue;
                    // If unit has an assigned node, check against that; otherwise check distance to rallyPoint.
                    Node assigned = u.lastAssignedNode;
                    float d = assigned != null ? Vector2.Distance(u.transform.position, assigned.centerPosition) : Vector2.Distance(u.transform.position, rallyPoint);
                    if (d <= rallyRadius) countAtRally++;
                }

                if (countAtRally >= Mathf.Min(squadSize, squad.Count))
                    break;

                // if contact found meanwhile, abort forming and allow attack behavior to take over
                if (firstContactAnnounced) break;

                yield return null;
            }

            // Now send whole squad to target position using formation (if we have a target)
            if (target != null)
            {
                sfc.MoveGroupWithFormation(squad, target.transform.position);
            }
            else
            {
                // fallback: find nearest player target and send
                var fallback = FindNearestPlayerTarget(castleInstance.transform.position);
                if (fallback != null)
                    sfc.MoveGroupWithFormation(squad, fallback.transform.position);
            }
        }
        else
        {
            // Legacy behavior: command units to rally by reserving a rally node and issuing node destinations
            foreach (var u in squad)
            {
                if (u == null) continue;
                if (rallyNode != null)
                    u.SetDestinationToNode(rallyNode);
            }

            float start = Time.time;
            // wait until enough units are within rallyRadius or timeout
            while (Time.time - start < squadGatherTimeout)
            {
                int countAtRally = 0;
                foreach (var u in squad)
                {
                    if (u == null) continue;
                    if (Vector2.Distance(u.transform.position, rallyNode.centerPosition) <= rallyRadius) countAtRally++;
                }

                if (countAtRally >= Mathf.Min(squadSize, squad.Count))
                    break;

                if (firstContactAnnounced) break;

                yield return null;
            }

            // Now send whole squad to target if exists, otherwise roam toward player/last known
            if (target != null)
            {
                Node tn = GridManager.Instance.NodeFromWorldPoint(target.transform.position);
                if (tn != null)
                {
                    foreach (var u in squad)
                    {
                        if (u == null) continue;
                        u.SetDestinationToNode(tn);
                    }
                }
            }
            else
            {
                var fallback = FindNearestPlayerTarget(castleInstance.transform.position);
                if (fallback != null)
                {
                    Node tn = GridManager.Instance.NodeFromWorldPoint(fallback.transform.position);
                    if (tn != null)
                        foreach (var u in squad) if (u != null) u.SetDestinationToNode(tn);
                }
            }
        }

        yield break;
    }

    // InstructAttackIfAppropriate remains public so AttackGoal can call it
    public void InstructAttackIfAppropriate()
    {
        // don't spam direct attacks if we haven't had contact yet
        if (!firstContactAnnounced)
            return;

        // gather all non-villager enemy units spawned by barracks (assume they are in scene)
        var soldiers = Object.FindObjectsOfType<UnitAgent>().Where(u => u.category != UnitCategory.Villager && u.owner == enemyEconomy).ToList();
        if (soldiers.Count == 0) return;

        // find a player target
        GameObject target = FindNearestPlayerTarget(castleInstance != null ? castleInstance.transform.position : transform.position);
        if (target == null) return;

        // a little personality: if target found, taunt
        EnemyDialogueController.Instance?.Speak($"Radau taikinį: {target.name}. Laikas judėti.", kingPortrait, 2.6f);

        // Form squads by grouping soldiers then send: do not order each soldier to attack instantly.
        var soldiersPool = soldiers.OrderBy(s => Vector2.Distance(s.transform.position, castleInstance.transform.position)).ToList();
        while (soldiersPool.Count > 0)
        {
            var squad = soldiersPool.Take(squadSize).ToList();
            soldiersPool = soldiersPool.Skip(squadSize).ToList();
            StartCoroutine(FormAndSendSquad(squad, target));
        }
    }

    // NEW: order attack specifically on provided target (used after discovery)
    public void InstructAttackOn(GameObject target)
    {
        if (target == null) return;

        var soldiers = Object.FindObjectsOfType<UnitAgent>().Where(u => u.category != UnitCategory.Villager && u.owner == enemyEconomy).ToList();
        if (soldiers.Count == 0) return;

        EnemyDialogueController.Instance?.Speak($"Target acquired: {target.name}. Move!", kingPortrait, 2.6f);

        var soldiersPool = soldiers.OrderBy(s => Vector2.Distance(s.transform.position, castleInstance.transform.position)).ToList();
        while (soldiersPool.Count > 0)
        {
            var squad = soldiersPool.Take(squadSize).ToList();
            soldiersPool = soldiersPool.Skip(squadSize).ToList();
            StartCoroutine(FormAndSendSquad(squad, target));
        }
    }

    ResourceNode FindNearestResourceNode(Vector3 from, float range)
    {
        ResourceNode[] nodes = Object.FindObjectsOfType<ResourceNode>();
        float best = range * range;
        ResourceNode pick = null;
        foreach (var rn in nodes)
        {
            if (rn == null) continue;
            if (rn.amount <= 0) continue;
            float d = (rn.transform.position - from).sqrMagnitude;
            if (d < best)
            {
                best = d;
                pick = rn;
            }
        }
        return pick;
    }

    GameObject FindNearestPlayerTarget(Vector3 from)
    {
        // prefer player units then buildings
        UnitAgent[] units = Object.FindObjectsOfType<UnitAgent>();
        GameObject best = null;
        float bestDist = float.MaxValue;
        foreach (var u in units)
        {
            if (u == null) continue;
            // skip units that belong to enemyEconomy (don't target own units)
            if (u.owner == enemyEconomy) continue;

            if (u is LeaderUnit) // attack leader first
            {
                float d = (u.transform.position - from).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = u.gameObject;
                }
            }
            else
            {
                // prefer non-enemy units
                float d = (u.transform.position - from).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = u.gameObject;
                }
            }
        }

        // if none, consider buildings (skip buildings owned by enemyEconomy)
        if (best == null)
        {
            var builds = Object.FindObjectsOfType<Building>();
            foreach (var b in builds)
            {
                if (b == null) continue;
                if (b.owner == enemyEconomy) continue;
                float d = (b.transform.position - from).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = b.gameObject;
                }
            }
        }

        return best;
    }

    // Coroutine to set owners after a BuildingConstruction finishes
    IEnumerator SetOwnersWhenFinished(GameObject buildingObj)
    {
        if (buildingObj == null) yield break;

        float timeout = 60f;
        float t = 0f;

        while (t < timeout)
        {
            if (buildingObj == null) yield break;

            var bc = buildingObj.GetComponent<BuildingConstruction>();
            if (bc == null || bc.isFinished)
            {
                // building finalized: set owners on Building and DropOff
                var bcomp = buildingObj.GetComponent<Building>();
                if (bcomp != null)
                    bcomp.owner = enemyEconomy;

                var drop = buildingObj.GetComponent<DropOffBuilding>();
                if (drop != null)
                    drop.owner = enemyEconomy;

                // If this is the castle dropoff, cache it and initialize any existing workers' task systems.
                if (drop != null && castleDropoff == null)
                {
                    castleDropoff = drop;

                    // Initialize any already-spawned workers so their VillagerTaskSystem has the correct drop-off/owner.
                    foreach (var w in workers)
                    {
                        if (w == null) continue;
                        var vTask = w.GetComponent<VillagerTaskSystem>();
                        if (vTask != null)
                        {
                            vTask.Init(castleDropoff);
                        }
                        var ua = w.GetComponent<UnitAgent>();
                        if (ua != null)
                        {
                            ua.owner = enemyEconomy;
                        }
                    }
                }

                yield break;
            }
            t += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }

        Debug.LogWarning("[EnemyKing] SetOwnersWhenFinished timed out waiting for construction to finish.");
    }

    IEnumerator FirstContactWatcher()
    {
        float checkInterval = 0.5f;

        while (!firstContactAnnounced)
        {
            if (FogOfWar.Instance == null || !FogOfWar.Instance.IsReady)
            {
                yield return new WaitForSeconds(checkInterval);
                continue;
            }

            var enemyUnits = Object.FindObjectsOfType<UnitAgent>().Where(u => u != null && u.owner == enemyEconomy).ToArray();
            var enemyBuildings = Object.FindObjectsOfType<Building>().Where(b => b != null && b.owner == enemyEconomy).ToArray();

            bool seen = false;

            foreach (var eu in enemyUnits)
            {
                if (eu == null) continue;
                if (FogOfWar.Instance.IsVisibleAtWorldPos(eu.transform.position))
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
            {
                foreach (var eb in enemyBuildings)
                {
                    if (eb == null) continue;
                    if (FogOfWar.Instance.IsVisibleAtWorldPos(eb.transform.position))
                    {
                        seen = true;
                        break;
                    }
                }
            }

            if (seen)
            {
                firstContactAnnounced = true;
                EnemyDialogueController.Instance?.SpeakFirstContact(kingPortrait);
                yield break;
            }

            yield return new WaitForSeconds(checkInterval);
        }
    }

    // Detection coroutine: periodically scans around enemy units to discover player's leader and base.
    IEnumerator DetectPlayerAssetsLoop()
    {
        while (true)
        {
            // if already discovered both, stop checking
            if (discoveredLeader != null && discoveredPlayerBase != null)
                yield break;

            // gather our units (both villagers and soldiers can detect)
            var myUnits = Object.FindObjectsOfType<UnitAgent>().Where(u => u != null && u.owner == enemyEconomy).ToArray();

            foreach (var u in myUnits)
            {
                if (u == null) continue;

                // find nearby colliders once per unit
                Collider2D[] hits = Physics2D.OverlapCircleAll(u.transform.position, detectRadius);
                if (hits == null || hits.Length == 0) continue;

                foreach (var h in hits)
                {
                    if (h == null) continue;

                    // Leader detection
                    var leader = h.GetComponent<LeaderUnit>();
                    if (leader != null && leader.gameObject != null)
                    {
                        if (leader.owner != enemyEconomy)
                        {
                            if (discoveredLeader == null)
                            {
                                discoveredLeader = leader.gameObject;
                                EnemyDialogueController.Instance?.Speak($"Leader spotted: {leader.gameObject.name}", kingPortrait, 2.2f);
                                // when leader discovered, immediately order attack on it
                                InstructAttackOn(discoveredLeader);
                            }
                        }
                    }

                    // Base detection: building with name containing "base" (case-insensitive) and not owned by us
                    var building = h.GetComponent<Building>();
                    if (building != null && building.data != null && building.owner != enemyEconomy)
                    {
                        var bname = (building.data.buildingName ?? "").ToLowerInvariant();
                        if (bname.Contains("base") || bname.Contains("castle") || building.data.buildingName.ToLowerInvariant().Contains("town"))
                        {
                            if (discoveredPlayerBase == null)
                            {
                                discoveredPlayerBase = building.gameObject;
                                EnemyDialogueController.Instance?.Speak($"Base located: {building.data.buildingName}", kingPortrait, 2.2f);
                                // if we already have soldiers, order attack on base (prefer leader if found)
                                if (discoveredLeader != null)
                                    InstructAttackOn(discoveredLeader);
                                else
                                    InstructAttackOn(discoveredPlayerBase);
                            }
                        }
                    }

                    // Resource node detection
                    var rn = h.GetComponent<ResourceNode>();
                    if (rn != null && rn.amount > 0)
                    {
                        var rtype = rn.resourceType;
                        if (!discoveredResourceNodes.TryGetValue(rtype, out var set))
                        {
                            set = new HashSet<ResourceNode>();
                            discoveredResourceNodes[rtype] = set;
                        }
                        if (!set.Contains(rn))
                        {
                            set.Add(rn);
                            EnemyDialogueController.Instance?.Speak($"Resource spotted: {rtype} at {rn.transform.position.ToString()}", kingPortrait, 1.6f);
                        }
                    }

                    if (discoveredLeader != null && discoveredPlayerBase != null)
                        break;
                }

                if (discoveredLeader != null && discoveredPlayerBase != null)
                    break;
            }

            yield return new WaitForSeconds(detectInterval);
        }
    }

    // Made public so BuildGoal can call it
    public IEnumerator RegisterBarracksWhenReady(GameObject b)
    {
        float timeout = 30f;
        float t = 0f;
        while (b != null && t < timeout)
        {
            var bc = b.GetComponent<BuildingConstruction>();
            if (bc == null || bc.isFinished)
            {
                var prod = b.GetComponent<UnitProduction>();
                if (prod != null)
                {
                    var building = b.GetComponent<Building>();
                    if (building != null) building.owner = enemyEconomy;
                    barracks.Add(b);
                }
                yield break;
            }
            t += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }
    }

    // Helper: get BuildingData from a prefab (prefab may hold BuildingConstruction.data or BuildingData directly)
    private BuildingData GetBuildingDataFromPrefab(GameObject prefab)
    {
        if (prefab == null) return null;
        var bcPrefab = prefab.GetComponent<BuildingConstruction>();
        if (bcPrefab != null && bcPrefab.data != null) return bcPrefab.data;
        var bd = prefab.GetComponent<BuildingData>();
        return bd;
    }

    // Helper: refund building cost back into the enemy economy (safe no-op if enemyEconomy null)
    private void RefundBuildingCost(BuildingData bd)
    {
        if (bd == null || enemyEconomy == null) return;
        if (bd.woodCost > 0) enemyEconomy.AddResource(ResourceType.Wood, bd.woodCost);
        if (bd.ironCost > 0) enemyEconomy.AddResource(ResourceType.Iron, bd.ironCost);
        if (bd.goldCost > 0) enemyEconomy.AddResource(ResourceType.Gold, bd.goldCost);
        if (bd.foodCost > 0) enemyEconomy.AddResource(ResourceType.Food, bd.foodCost);
    }

    public GameObject TryPlaceBuilding(GameObject buildingPrefab, bool isHouse = false)
    {
        if (buildingPrefab == null || castleInstance == null) return null;

        if (!FindValidPlacementNearCastle(buildingPrefab, out Vector3 pos))
        {
            return null;
        }

        GameObject b = Instantiate(buildingPrefab, pos, Quaternion.identity, spawnParent);

        var bc = b.GetComponent<BuildingConstruction>();
        if (bc != null)
        {
            bc.assignedOwner = enemyEconomy;
            bc.BeginConstruction();

            var buildingComp = b.GetComponent<Building>();
            if (buildingComp != null) buildingComp.owner = enemyEconomy;

            var drop = b.GetComponent<DropOffBuilding>();
            if (drop != null) drop.owner = enemyEconomy;

            bc.AlertNearbyIdleVillagers();
        }
        else
        {
            var buildingComp = b.GetComponent<Building>();
            if (buildingComp != null) buildingComp.owner = enemyEconomy;
            var drop = b.GetComponent<DropOffBuilding>();
            if (drop != null) drop.owner = enemyEconomy;
        }

        return b;
    }

    private bool FindValidPlacementNearCastle(GameObject buildingPrefab, out Vector3 outPos, int searchRadiusNodes = 8)
    {
        outPos = Vector3.zero;
        if (buildingPrefab == null || castleInstance == null || GridManager.Instance == null)
            return false;

        var bcPrefab = buildingPrefab.GetComponent<BuildingConstruction>();
        Vector2Int footprint = bcPrefab != null ? bcPrefab.size : new Vector2Int(1, 1);

        float cell = GridManager.Instance.nodeDiameter;
        Vector2 boxSize = new Vector2(footprint.x * cell - 0.05f, footprint.y * cell - 0.05f);

        Node castleCenterNode = GridManager.Instance.NodeFromWorldPoint(castleInstance.transform.position);
        if (castleCenterNode == null)
            return false;

        for (int r = 0; r <= searchRadiusNodes; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue;

                    int nx = castleCenterNode.gridX + dx;
                    int ny = castleCenterNode.gridY + dy;
                    Node n = GridManager.Instance.GetNode(nx, ny);
                    if (n == null) continue;
                    if (!n.walkable) continue;

                    Vector3 candidatePos = n.centerPosition;

                    int halfX = footprint.x / 2;
                    int halfY = footprint.y / 2;
                    int startX = n.gridX - halfX;
                    int startY = n.gridY - halfY;
                    bool footprintOk = true;
                    for (int fx = 0; fx < footprint.x && footprintOk; fx++)
                    {
                        for (int fy = 0; fy < footprint.y; fy++)
                        {
                            Node fn = GridManager.Instance.GetNode(startX + fx, startY + fy);
                            if (fn == null || !fn.walkable)
                            {
                                footprintOk = false;
                                break;
                            }
                        }
                    }
                    if (!footprintOk) continue;

                    LayerMask buildMask = ~0;
                    if (BuildingPlacementController.Instance != null)
                        buildMask = BuildingPlacementController.Instance.buildingMask;

                    Collider2D overlap = Physics2D.OverlapBox(candidatePos, boxSize, 0f, buildMask);
                    if (overlap != null)
                        continue;

                    Collider2D[] hits = Physics2D.OverlapBoxAll(candidatePos, boxSize, 0f);
                    bool bad = false;
                    foreach (var h in hits)
                    {
                        if (h == null) continue;
                        if (h.GetComponent<ResourceNode>() != null) { bad = true; break; }
                        if (h.GetComponent<UnitAgent>() != null) { bad = true; break; }
                    }
                    if (bad) continue;

                    outPos = candidatePos;
                    return true;
                }
            }
        }

        return false;
    }

    // --- Immediate dispatch helper to send idle villagers to undiscovered resource nodes
    private void SendResourceScouts(int count)
    {
        if (count <= 0) return;
        if (castleInstance == null) return;

        // Prefer non-villager combat units for scouting (they won't auto-return to gather).
        var soldierCandidates = Object.FindObjectsOfType<UnitAgent>()
            .Where(u => u != null && u.owner == enemyEconomy && u.category != UnitCategory.Villager)
            .Select(u => new { ua = u, dist = (u.transform.position - castleInstance.transform.position).sqrMagnitude })
            .OrderBy(x => x.dist)
            .Select(x => x.ua)
            .ToList();

        int assigned = 0;

        // find undiscovered resource nodes (use cached registry if available)
        var allNodes = ResourceNode.AllNodes;
        List<ResourceNode> pool;
        if (allNodes != null && allNodes.Count > 0)
            pool = allNodes.Where(n => n != null && n.amount > 0).ToList();
        else
            pool = Object.FindObjectsOfType<ResourceNode>().Where(n => n != null && n.amount > 0).ToList();

        // Prefer nodes we haven't discovered yet
        var undiscovered = new List<ResourceNode>();
        foreach (var rn in pool)
        {
            if (rn == null) continue;
            if (!discoveredResourceNodes.TryGetValue(rn.resourceType, out var set) || !set.Contains(rn))
                undiscovered.Add(rn);
        }

        // If nothing undiscovered, target the nearest nodes (keep scouting known nodes to update location)
        if (undiscovered.Count == 0)
            undiscovered = pool;

        var usedNodes = new HashSet<ResourceNode>();

        // 1) Assign soldiers first
        foreach (var soldier in soldierCandidates)
        {
            if (soldier == null) continue;
            if (assigned >= count) break;

            // pick nearest undiscovered node to this soldier
            ResourceNode pick = null;
            float best = float.MaxValue;
            foreach (var rn in undiscovered)
            {
                if (rn == null) continue;
                if (usedNodes.Contains(rn)) continue;
                float d = (rn.transform.position - soldier.transform.position).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    pick = rn;
                }
            }

            if (pick == null) continue;

            usedNodes.Add(pick);
            assigned++;

            // send soldier to a node position (use nearest harvest spot if available)
            var spots = pick.GetHarvestSpots();
            Vector3 dest = pick.transform.position;
            if (spots != null && spots.Count > 0)
                dest = spots.OrderBy(s => (s - soldier.transform.position).sqrMagnitude).First();

            Node destNode = GridManager.Instance != null ? GridManager.Instance.NodeFromWorldPoint(dest) : null;
            if (destNode != null)
                soldier.SetDestinationToNode(destNode);
            // if destNode is null we skip — GridManager should normally be present; avoid calling non-existent world-target API.
        }

        if (assigned >= count) return;

        // 2) Fallback: use idle villagers only if not enough soldiers available.
        var villagerCandidates = Object.FindObjectsOfType<UnitAgent>()
            .Where(u => u != null && u.owner == enemyEconomy && u.category == UnitCategory.Villager)
            .Select(u => new { ua = u, dist = (u.transform.position - castleInstance.transform.position).sqrMagnitude })
            .OrderBy(x => x.dist)
            .Select(x => x.ua)
            .ToList();

        foreach (var vill in villagerCandidates)
        {
            if (vill == null) continue;
            if (assigned >= count) break;

            var vt = vill.GetComponent<VillagerTaskSystem>();
            if (vt == null) continue;

            // Only use truly idle villagers to avoid interrupting gathering/building.
            if (!vt.IsIdle()) continue;

            // pick nearest undiscovered node to this villager
            ResourceNode pick = null;
            float best = float.MaxValue;
            foreach (var rn in undiscovered)
            {
                if (rn == null) continue;
                if (usedNodes.Contains(rn)) continue;
                float d = (rn.transform.position - vill.transform.position).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    pick = rn;
                }
            }

            if (pick == null) continue;

            usedNodes.Add(pick);
            assigned++;

            // send villager to a node position (use nearest harvest spot if available)
            var spots = pick.GetHarvestSpots();
            Vector3 dest = pick.transform.position;
            if (spots != null && spots.Count > 0)
                dest = spots.OrderBy(s => (s - vill.transform.position).sqrMagnitude).First();

            // Command using UnitAgent so VillagerTaskSystem receives a move command but we only pick idle villagers.
            Node destNode = GridManager.Instance != null ? GridManager.Instance.NodeFromWorldPoint(dest) : null;
            if (destNode != null)
                vill.SetDestinationToNode(destNode);
            // skip if no grid available to avoid calling non-existent API
        }
    }

    // Ensure workers gather (public so goals can call). Keeps previous semantics: only order idle villagers.
    public void EnsureWorkersGather()
    {
        if (workers == null || workers.Count == 0) return;

        var gm = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;

        foreach (var w in workers.ToList())
        {
            if (w == null) continue;
            var vt = w.GetComponent<VillagerTaskSystem>();
            var ua = w.GetComponent<UnitAgent>();
            if (vt == null || ua == null) continue;

            // Only command truly idle villagers — avoid interrupting gatherers here.
            if (!vt.IsIdle()) continue;

            ResourceNode best = FindNearestResourceNode(w.transform.position, 30f);
            if (best == null) continue;

            var spots = best.GetHarvestSpots();
            if (spots == null || spots.Count == 0) continue;

            // Prefer closest harvest spot that we can reserve for this worker
            Vector3 chosenSpot = Vector3.zero;
            Node chosenNode = null;

            var ordered = spots.OrderBy(s => (s - w.transform.position).sqrMagnitude).ToList();

            foreach (var spot in ordered)
            {
                if (gm == null) break;
                Node n = gm.NodeFromWorldPoint(spot);
                if (n == null || !n.walkable) continue;

                bool got = false;
                if (nrs != null)
                {
                    // Try to reserve the node for this worker atomically.
                    got = nrs.TryReserve(n, ua);
                }
                else
                {
                    got = true;
                }

                if (got)
                {
                    chosenNode = n;
                    chosenSpot = n.centerPosition;
                    break;
                }
            }

            if (chosenNode != null)
            {
                // Only command to gather if we've secured a node for the unit.
                vt.CommandGather(best, chosenSpot);
            }
        }
    }

    // ---------------------------
    // Helper: affordability checks
    // ---------------------------
    private bool CanAfford(UnitData ud)
    {
        if (ud == null || enemyEconomy == null) return false;
        // check resources but do not spend
        if (enemyEconomy.wood < ud.woodCost) return false;
        if (enemyEconomy.iron < ud.stoneCost) return false;
        if (enemyEconomy.gold < ud.goldCost) return false;
        if (enemyEconomy.food < ud.foodCost) return false;
        if (enemyEconomy.population + 1 > enemyEconomy.populationCap) return false;
        return true;
    }

    private bool CanAffordAnyTroop()
    {
        // prefer configured UnitData first
        if (warriorUnitData != null && CanAfford(warriorUnitData)) return true;
        if (archerUnitData != null && CanAfford(archerUnitData)) return true;

        // otherwise try to inspect known barracks producible units quickly (cheap check)
        foreach (var b in barracks)
        {
            if (b == null) continue;
            var prod = b.GetComponent<UnitProduction>();
            if (prod == null || prod.producibleUnits == null) continue;
            foreach (var ud in prod.producibleUnits)
            {
                if (ud == null) continue;
                if (ud.category == UnitCategory.Villager) continue;
                if (CanAfford(ud)) return true;
            }
        }

        return false;
    }

    // Add these methods inside the existing `EnemyKingController` class (near other helpers).
    // Minimal one-shot expansion sequence: place house, increase pop cap when finished, place barracks,
    // wait until barracks ready, then spawn one combat unit. This guarantees the enemy builds and
    // produces at least one soldier even if goals/active-phase logic hasn't progressed.

    private const int debugHousePopIncrease = 5; // how much a house increases pop cap for this sequence

    IEnumerator BuildThenTrainOnce()
    {
        // small delay so initial spawn/ownership wiring completes
        yield return new WaitForSeconds(0.5f);

        // 1) Place a house (if available) and increase pop cap when finished
        if (housePrefab != null)
        {
            var placedHouse = TryPlaceBuilding(housePrefab, true);
            if (placedHouse != null)
            {
                if (verboseDebug) Debug.Log("[EnemyKing] placed house (one-shot sequence)");
                // Wait for construction to finish then increase pop cap
                yield return StartCoroutine(WaitForConstructionAndIncreasePop(placedHouse, debugHousePopIncrease));
            }
            else if (verboseDebug) Debug.LogWarning("[EnemyKing] failed to place house in one-shot sequence");
        }

        // small pause
        yield return new WaitForSeconds(0.25f);

        // 2) Place barracks (if available) and wait until ready (RegisterBarracksWhenReady will add to list)
        GameObject placedBarracks = null;
        if (barracksPrefab != null)
        {
            placedBarracks = TryPlaceBuilding(barracksPrefab, false);
            if (placedBarracks != null)
            {
                if (verboseDebug) Debug.Log("[EnemyKing] placed barracks (one-shot sequence)");
                yield return StartCoroutine(RegisterBarracksWhenReady(placedBarracks));
            }
            else if (verboseDebug) Debug.LogWarning("[EnemyKing] failed to place barracks in one-shot sequence");
        }

        // small pause to ensure barracks registered
        yield return new WaitForSeconds(0.25f);

        // 3) Spawn one combat unit (prefer archer then warrior)
        UnitData preferred = archerUnitData ?? warriorUnitData ?? workerUnitData;
        if (preferred != null && preferred.prefab != null)
        {
            // ensure population cap is sufficient (we added cap after house finished above)
            if (enemyEconomy != null && enemyEconomy.population + 1 > enemyEconomy.populationCap)
            {
                if (verboseDebug) Debug.Log("[EnemyKing] population cap insufficient for one-shot spawn; aborting spawn");
                yield break;
            }

            Node spawnNode = FindSpawnNodeNearCastle();
            if (spawnNode == null)
            {
                if (verboseDebug) Debug.LogWarning("[EnemyKing] no spawn node found for one-shot spawn");
                yield break;
            }

            // Spend resources if available; if not, still spawn to ensure one officer is produced for gameplay.
            bool spent = enemyEconomy != null ? enemyEconomy.TrySpend(preferred.woodCost, preferred.stoneCost, preferred.goldCost, preferred.foodCost, 1) : false;
            if (!spent && verboseDebug) Debug.Log("[EnemyKing] one-shot spawn: insufficient resources; spawning anyway for deterministic behavior");

            Vector3 pos = spawnNode.centerPosition + (Vector3)(UnityEngine.Random.insideUnitCircle * 0.2f);
            GameObject go = Instantiate(preferred.prefab, pos, Quaternion.identity, spawnParent);
            var ua = go.GetComponent<UnitAgent>();
            if (ua != null)
            {
                ua.owner = enemyEconomy;
                ua.category = preferred == archerUnitData ? UnitCategory.Archer : UnitCategory.Infantry;
            }

            // reflect population change
            if (enemyEconomy != null) enemyEconomy.population = Mathf.Clamp(enemyEconomy.population + 1, 0, enemyEconomy.populationCap);

            if (verboseDebug) Debug.Log($"[EnemyKing] one-shot spawned unit {go.name} (cat={ua?.category})");
        }
        else
        {
            if (verboseDebug) Debug.LogWarning("[EnemyKing] no UnitData prefabs assigned for one-shot spawn");
        }
    }

    IEnumerator WaitForConstructionAndIncreasePop(GameObject buildingObj, int addCap)
    {
        if (buildingObj == null) yield break;
        float timeout = 60f;
        float t = 0f;
        while (t < timeout)
        {
            if (buildingObj == null) yield break;
            var bc = buildingObj.GetComponent<BuildingConstruction>();
            if (bc == null || bc.isFinished)
            {
                // Increase population cap immediately when house is finished
                if (enemyEconomy != null && addCap > 0)
                {
                    enemyEconomy.AddPopulationCap(addCap);
                    if (verboseDebug) Debug.Log($"[EnemyKing] increased population cap by {addCap} after finishing {buildingObj.name}");
                }
                yield break;
            }
            t += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }

        Debug.LogWarning("[EnemyKing] WaitForConstructionAndIncreasePop timed out waiting for building to finish.");
    }
}