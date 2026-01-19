using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using AI.BehaviorTree;

/// <summary>
/// Thin facade for enemy systems. Keeps only high level orchestration (spawn, expose APIs, enqueue goals).
/// Heavy logic lives in goal scripts. This controller exposes a small, stable API other systems expect.
/// </summary>
public class EnemyKingController : MonoBehaviour
{
    [Header("Prefabs")]
    public GameObject castlePrefab;
    public GameObject villagerPrefab;
    public GameObject barracksPrefab;
    public GameObject housePrefab;

    [Header("Data")]
    public UnitData workerUnitData;
    public UnitData warriorUnitData;
    public UnitData archerUnitData;

    public Vector2 castleSpawnPosition;
    public int initialWorkers;
    public int woodToBuildBarracks;
    public int woodToBuildHouse;
    public int minWoodToTrainTroop;
    public int troopBatchSize;
    public float decisionInterval;
    public int minScoutCount;
    public int maxScoutCount;
    public float scoutRadius;
    public float scoutMoveInterval;
    public int squadSize;
    public float rallyRadius;
    public float squadGatherTimeout;
    public float workerSpawnInterval;
    public float detectRadius;
    public float detectInterval;
    public int resourceScoutCount;
    public float resourceScoutInterval;

    [Header("Debug")]
    public bool verboseDebug = false;

    // runtime state (kept minimal)
    public EnemySpawner enemySpawner;
    readonly List<GameObject> barracks = new List<GameObject>();

    private EnemyIntel intel;
    private BehaviorTree behaviorTree;
    private Dictionary<GameObject, (Vector3 position, float time)> unitPositions = new Dictionary<GameObject, (Vector3, float)>();

    // discovered player things (kept minimal; intel calls back into these internals)
    internal GameObject discoveredLeader;
    internal GameObject discoveredPlayerBase;

    // lightweight guards
    private bool aiInitialized = false;
    private bool resourceDiscoveryCompleted = false;

    void Start()
    {
        RandomizeParameters();
        EnsurePlannerExists();
        InitializeIntel();
        InitializeBehaviorTree();
        StartCoroutine(DecisionLoop(decisionInterval));
        StartCoroutine(StuckFixLoop(5f));
        // Enqueue castle build goal as first priority
        StartCoroutine(EnqueueCastleBuildGoal());
        aiInitialized = true;
    }

    void RandomizeParameters()
    {
        // Initial spawn
        castleSpawnPosition = new Vector2(Random.Range(-10f, 10f), Random.Range(-10f, 10f));
        initialWorkers = Random.Range(2, 6);

        // Economy thresholds
        woodToBuildBarracks = Random.Range(40, 80);
        woodToBuildHouse = Random.Range(15, 30);
        minWoodToTrainTroop = Random.Range(20, 50);
        troopBatchSize = Random.Range(2, 5);

        // AI timing
        decisionInterval = Random.Range(3f, 8f);

        // Scouting
        minScoutCount = Random.Range(1, 3);
        maxScoutCount = Random.Range(minScoutCount + 1, minScoutCount + 4);
        scoutRadius = Random.Range(8f, 16f);
        scoutMoveInterval = Random.Range(6f, 12f);
        squadSize = Random.Range(2, 5);
        rallyRadius = Random.Range(2f, 5f);
        squadGatherTimeout = Random.Range(6f, 12f);
        workerSpawnInterval = Random.Range(8f, 15f);

        // Detection
        detectRadius = Random.Range(4f, 10f);
        detectInterval = Random.Range(0.4f, 1f);

        // Resource scouting
        resourceScoutCount = Random.Range(2, 5);
        resourceScoutInterval = Random.Range(12f, 25f);
    }

    // -- Public read-only API consumed by other systems/goals ----------------
    public GameObject CastleInstance => enemySpawner?.CastleInstance;
    public PlayerEconomy EnemyEconomy => enemySpawner?.enemyEconomy;
    public GameObject VillagerPrefab => villagerPrefab;
    public UnitData WorkerUnitData => workerUnitData;
    public UnitData WarriorUnitData => warriorUnitData;
    public UnitData ArcherUnitData => archerUnitData;
    public List<GameObject> Barracks => barracks;
    public List<GameObject> Workers => enemySpawner?.Workers;
    public int InitialWorkers => initialWorkers;
    public int SquadSize => squadSize;
    public float RallyRadius => rallyRadius;
    public float SquadGatherTimeout => squadGatherTimeout;

    // discovery summary used by goals
    public bool FirstContactAnnounced => resourceDiscoveryCompleted || discoveredLeader != null || discoveredPlayerBase != null;
    public GameObject DiscoveredLeader => discoveredLeader;
    public GameObject DiscoveredPlayerBase => discoveredPlayerBase;

    // delegate detection announcements to intel component (intel handles announcements)
    public void RegisterDiscoveredResource(ResourceNode rn) => intel?.RegisterDiscoveredResource(rn);
    public void RegisterDiscoveredLeader(GameObject leader) => intel?.RegisterDiscoveredLeader(leader);
    public void RegisterDiscoveredPlayerBase(GameObject b) => intel?.RegisterDiscoveredPlayerBase(b);

    // -------------------------
    // Initialization helpers
    // -------------------------
    void EnsurePlannerExists()
    {
        if (FindObjectOfType<GoalPlanner>() == null)
        {
            var go = new GameObject("GoalPlanner");
            go.AddComponent<GoalPlanner>();
        }
    }

    void InitializeIntel()
    {
        intel = GetComponent<EnemyIntel>();
        if (intel == null) intel = gameObject.AddComponent<EnemyIntel>();
        intel.Init(this);
    }

    void InitializeBehaviorTree()
    {
        var root = new Selector(
            // Priority 1: Scout if not discovered anything
            new Sequence(
                new ConditionNode(() => !FirstContactAnnounced),
                new ActionNode(() => { EnqueueScoutGoal(); return NodeState.Success; })
            ),

            // Priority 2: Spawn workers if below minimum
            new Sequence(
                new ConditionNode(() => Workers.Count < initialWorkers),
                new ActionNode(() => { EnqueueSpawnWorkersGoal(); return NodeState.Success; })
            ),

            // Priority 3: Detect enemies if not already
            new Sequence(
                new ConditionNode(() => !resourceDiscoveryCompleted),
                new ActionNode(() => { EnqueueDetectGoal(); return NodeState.Success; })
            ),

            // Priority 4: Scout for resources
            new Sequence(
                new ConditionNode(() => !resourceDiscoveryCompleted),
                new ActionNode(() => { EnqueueResourceScoutGoal(); return NodeState.Success; })
            ),

            // Priority 5: Train units if conditions met
            new Sequence(
                new ConditionNode(() => CanTrainUnits()),
                new ActionNode(() => { EnqueueTrainUnitsGoal(); return NodeState.Success; })
            )
        );

        behaviorTree = new BehaviorTree(root);
    }

    private IEnumerator DecisionLoop(float interval)
    {
        while (true)
        {
            yield return new WaitForSeconds(interval);
            if (behaviorTree != null)
            {
                behaviorTree.Tick();
            }
        }
    }

    private IEnumerator StuckFixLoop(float interval)
    {
        while (true)
        {
            yield return new WaitForSeconds(interval);
            CheckAndFixStuckUnits();
        }
    }

    void CheckAndFixStuckUnits()
    {
        var units = UnityEngine.Object.FindObjectsOfType<UnitAgent>();
        foreach (var unit in units)
        {
            if (unit == null || unit.owner != EnemyEconomy) continue;

            Vector3 currentPos = unit.transform.position;
            float currentTime = Time.time;

            if (unitPositions.TryGetValue(unit.gameObject, out var last))
            {
                if (Vector3.Distance(currentPos, last.position) < 0.1f && currentTime - last.time > 10f)
                {
                    // Stuck, move to random walkable node
                    MoveToRandomWalkableNode(unit);
                }
            }

            unitPositions[unit.gameObject] = (currentPos, currentTime);
        }

        // Clean up old entries
        var toRemove = new List<GameObject>();
        foreach (var kvp in unitPositions)
        {
            if (kvp.Key == null) toRemove.Add(kvp.Key);
        }
        foreach (var key in toRemove) unitPositions.Remove(key);
    }

    void MoveToRandomWalkableNode(UnitAgent unit)
    {
        var gm = GridManager.Instance;
        if (gm == null) return;

        // Find a random walkable node within a radius
        Node center = gm.NodeFromWorldPoint(unit.transform.position);
        if (center == null) return;

        for (int attempts = 0; attempts < 10; attempts++)
        {
            int dx = UnityEngine.Random.Range(-5, 6);
            int dy = UnityEngine.Random.Range(-5, 6);
            Node target = gm.GetNode(center.gridX + dx, center.gridY + dy);
            if (target != null && target.walkable)
            {
                unit.SetDestinationToNode(target);
                
                return;
            }
        }
    }

    void EnqueueScoutGoal()
    {
        var planner = FindObjectOfType<GoalPlanner>();
        if (planner != null)
            planner.EnqueueGoal(new ScoutGoal(this, minScoutCount, maxScoutCount, scoutRadius, scoutMoveInterval, 60f, () => FirstContactAnnounced));
    }

    void EnqueueSpawnWorkersGoal()
    {
        var planner = FindObjectOfType<GoalPlanner>();
        if (planner != null)
            planner.EnqueueGoal(new SpawnWorkersGoal(this, workerSpawnInterval));
    }

    void EnqueueDetectGoal()
    {
        var planner = FindObjectOfType<GoalPlanner>();
        if (planner != null)
            planner.EnqueueGoal(new DetectGoal(this, detectRadius, detectInterval));
    }

    void EnqueueResourceScoutGoal()
    {
        var planner = FindObjectOfType<GoalPlanner>();
        if (planner != null)
            planner.EnqueueGoal(new ResourceScoutGoal(this, resourceScoutCount, resourceScoutInterval, () => resourceDiscoveryCompleted));
    }

    void EnqueueTrainUnitsGoal()
    {
        var planner = FindObjectOfType<GoalPlanner>();
        if (planner != null)
            planner.EnqueueGoal(new TrainUnitsGoal(this, troopBatchSize));
    }

    bool CanTrainUnits()
    {
        return EnemyEconomy != null && EnemyEconomy.wood >= minWoodToTrainTroop && barracks.Count > 0;
    }

    private IEnumerator EnqueueCastleBuildGoal()
    {
        // Wait for spawner to initialize
        yield return new WaitForEndOfFrame();
        
        if (enemySpawner != null && enemySpawner.CastleInstance != null)
        {
            var planner = FindObjectOfType<GoalPlanner>();
            if (planner != null)
            {
                // Create build goal for the existing castle instance
                var buildGoal = new BuildGoal(this, enemySpawner.CastleInstance, 120f);
                planner.EnqueueGoal(buildGoal);
                
            }
        }
    }

    void EnqueueGoals()
    {
        var planner = FindObjectOfType<GoalPlanner>();
        if (planner == null) return;

        planner.EnqueueGoal(new ScoutGoal(this, minScoutCount, maxScoutCount, scoutRadius, scoutMoveInterval, 60f, () => FirstContactAnnounced));
        planner.EnqueueGoal(new SpawnWorkersGoal(this, workerSpawnInterval));
        planner.EnqueueGoal(new DetectGoal(this, detectRadius, detectInterval));
        planner.EnqueueGoal(new ResourceScoutGoal(this, resourceScoutCount, resourceScoutInterval, () => resourceDiscoveryCompleted));
        planner.EnqueueGoal(new TrainUnitsGoal(this, troopBatchSize));
    }
}