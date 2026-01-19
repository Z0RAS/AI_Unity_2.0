using Economy.Helpers;
using System.Collections.Generic;
using UnityEngine;


[RequireComponent(typeof(UnitAgent))]
public class VillagerTaskSystem : MonoBehaviour
{
    // global registry used by recruiter/other systems
    static List<VillagerTaskSystem> allRegisteredVillagers = new List<VillagerTaskSystem>();
    public static List<VillagerTaskSystem> AllRegisteredVillagers => allRegisteredVillagers;

    public VillagerTask currentTask = VillagerTask.Idle;
    public BuildingConstruction AssignedConstruction { get; private set; }

    [Header("Gathering")]
    public int carryCapacity = 5;

    [Header("Simple Settings")]
    public float idleSearchInterval = 1.0f;

    // minimal references
    UnitAgent agent;
    // helpers for other modules
    public bool verboseDebug = false;
    public Vector3 Position => transform.position;
    PlayerEconomy ownerEconomy;
    DropOffBuilding dropOff;

    // GATHER state (kept minimal)
    ResourceNode targetNode;
    Dictionary<ResourceType, int> carriedResources = new Dictionary<ResourceType, int>();
    int carriedTotal = 0;
    bool returningToDropOff;
    Vector3 myHarvestSpot;

    // BUILD state (kept minimal)
    BuildingConstruction buildingToConstruct;
    // preferred approach node (reserved by recruiter) — must be honored by CommandBuild/HandleBuildUpdate
    Node preferredApproachNode;

    // timers
    float idleTimer = 0f;

    void Awake()
    {
        agent = GetComponent<UnitAgent>();
    }

    void OnEnable()
    {
        // register into global list for systems that query all villagers
        if (!allRegisteredVillagers.Contains(this)) allRegisteredVillagers.Add(this);
    }

    void OnDisable()
    {
        if (allRegisteredVillagers.Contains(this)) allRegisteredVillagers.Remove(this);
    }

    public bool IsIdle()
    {
        return currentTask == VillagerTask.Idle;
    }

    void Start()
    {
        if (ownerEconomy == null)
        {
            DropOffBuilding drop = null;
#if UNITY_2021_2_OR_NEWER
            drop = FindFirstObjectByType<DropOffBuilding>();
#else
            drop = FindObjectOfType<DropOffBuilding>();
#endif
            Init(drop);
        }

        idleTimer = Random.Range(0f, idleSearchInterval * 0.5f);
    }

    public void Init(DropOffBuilding defaultDropOff)
    {
        if (defaultDropOff != null)
        {
            dropOff = defaultDropOff;
            ownerEconomy = defaultDropOff.owner;
            return;
        }

        if (agent != null && agent.owner != null)
        {
            ownerEconomy = agent.owner;
            var drops = Object.FindObjectsOfType<DropOffBuilding>();
            foreach (var d in drops)
            {
                if (d == null) continue;
                var b = d.GetComponent<Building>();
                var bc = d.GetComponent<BuildingConstruction>();
                if (b != null || (bc != null && bc.isFinished))
                {
                    if (d.owner == ownerEconomy)
                    {
                        dropOff = d;
                        break;
                    }
                }
            }
        }
    }

    void Update()
    {
        if (currentTask == VillagerTask.Build)
        {
            HandleBuildUpdate();
            return;
        }

        if (currentTask == VillagerTask.Gather)
        {
            HandleGatherUpdate();
            return;
        }

        if (currentTask == VillagerTask.Idle)
        {
            // If recruiter previously assigned this villager to a construction, begin build now.
            if (AssignedConstruction != null)
            {
                try
                {
                    // Start actual build command (this will compute approach hint if needed)
                    CommandBuild(AssignedConstruction, preferredApproachNode);
                }
                catch { }
                return;
            }

            if (Time.time >= idleTimer)
            {
                TryFindWork();
                idleTimer = Time.time + idleSearchInterval;
            }
        }
    }

    // --- Build (minimal) ---
    void HandleBuildUpdate()
    {
        if (buildingToConstruct == null || buildingToConstruct.isFinished)
        {
            ClearAllTasks();
            return;
        }

        // If recruiter reserved a preferred node, and we have reached it, register ourselves as builder.
        try
        {
            if (preferredApproachNode != null && agent != null)
            {
                bool atReservedCenter = false;
                try { atReservedCenter = agent.IsAtReservedNodeCenter(0.18f); } catch { atReservedCenter = false; }
                if (atReservedCenter)
                {
                    try
                    {
                        // idempotent: AddBuilder will ignore duplicates
                        buildingToConstruct.AddBuilder(agent);
                    }
                    catch { }
                    // keep currentTask as Build so we remain in build state
                }
            }
        }
        catch { }

        if (buildingToConstruct != null)
        {
            var gm = GridManager.Instance;
            if (gm != null)
            {
                // prefer the recruiter-provided approach node if present and still valid/reservable
                Node approach = null;

                if (preferredApproachNode != null)
                {
                    try
                    {
                        if (preferredApproachNode.walkable)
                        {
                            var nrs = NodeReservationSystem.Instance;
                            // Accept preferred approach if:
                            // - no reservation system present, OR
                            // - the node is currently unreserved, OR
                            // - this agent already holds the reservation (agent.reservedNode)
                            if (nrs == null
                                || !nrs.IsReserved(preferredApproachNode)
                                || (agent != null && agent.reservedNode == preferredApproachNode))
                            {
                                approach = preferredApproachNode;
                            }
                            else
                            {
                                approach = null;
                            }
                        }
                    }
                    catch { approach = null; }
                }

                if (approach == null)
                {
                    Node center = gm.NodeFromWorldPoint(buildingToConstruct.transform.position);
                    // prefer the closest outer walkable node to the agent — avoid pathing into the footprint center
                    if (center != null)
                    {
                        approach = FindClosestPerimeterNodeForAgent(gm, buildingToConstruct, agent);
                        if (approach == null)
                            approach = gm.FindClosestWalkableNode(center.gridX, center.gridY);
                    }
                }

                if (approach != null)
                {
                    // Only set destination if we don't already have a path to that node/reservation.
                    try
                    {
                        bool needSet = true;
                        if (agent != null)
                        {
                            var currentReserved = agent.DebugReservedNode;
                            if (currentReserved == approach && agent.DebugHasPath)
                                needSet = false;
                        }

                        if (needSet)
                            agent.SetDestinationToNode(approach);
                    }
                    catch
                    {
                        try { agent.SetDestinationToNode(approach); } catch { }
                    }
                }
            }
        }
    }

    // Helper: find closest perimeter node (outside footprint) for this agent and building.
    // Best-effort; does not reserve the node (reservation should have been performed by caller where possible).
    Node FindClosestPerimeterNodeForAgent(GridManager gm, BuildingConstruction building, UnitAgent ua, HashSet<Node> exclude = null)
    {
        if (gm == null || building == null || ua == null) return null;

        Vector2Int start = gm.GetStartIndicesForCenteredFootprint(building.transform.position, building.Size);
        int innerMinX = start.x;
        int innerMinY = start.y;
        int innerMaxX = start.x + building.Size.x - 1;
        int innerMaxY = start.y + building.Size.y - 1;

        int searchRadius = Mathf.Max(2, Mathf.Max(building.Size.x, building.Size.y) + 2);
        float bestSqr = float.MaxValue;
        Node best = null;

        for (int r = 1; r <= searchRadius; r++)
        {
            int minX = innerMinX - r;
            int maxX = innerMaxX + r;
            int minY = innerMinY - r;
            int maxY = innerMaxY + r;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    // only perimeter cells
                    if (x != minX && x != maxX && y != minY && y != maxY) continue;

                    Node n = null;
                    try { n = gm.GetNode(x, y); } catch { n = null; }
                    if (n == null || !n.walkable) continue;

                    // skip nodes inside footprint
                    bool insideFootprint = (n.gridX >= innerMinX && n.gridX <= innerMaxX && n.gridY >= innerMinY && n.gridY <= innerMaxY);
                    if (insideFootprint) continue;

                    // skip excluded nodes (already assigned/reserved by other builders)
                    if (exclude != null && exclude.Contains(n)) continue;

                    float dsq = (new Vector2(n.centerPosition.x - ua.transform.position.x, n.centerPosition.y - ua.transform.position.y)).sqrMagnitude;
                    if (dsq < bestSqr) { bestSqr = dsq; best = n; }
                }
            }

            if (best != null) break;
        }

        return best;
    }

    public void CommandBuild(BuildingConstruction building, Node preferredApproach = null)
    {
        // This method now only marks the villager as assigned to the construction.
        // Movement should be commanded by the caller (AutoBuildRecruiter or player) via CommandMove.
        if (building == null || building.isFinished) return;
        ClearAllTasks();
        if (agent != null) agent.SetIdle(false);
        currentTask = VillagerTask.Build;
        buildingToConstruct = building;

        // remember assigned construction for external systems / recruiter
        AssignedConstruction = building;

        // If caller provided an approach node (auto-recruiter does this), honor it.
        preferredApproachNode = preferredApproach;

        var gm = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;

        // Build a set of nodes already claimed by existing builders so we avoid assigning duplicates.
        HashSet<Node> excludedNodes = new HashSet<Node>();
        try
        {
            var existingBuilders = building.GetBuilders();
            if (existingBuilders != null)
            {
                foreach (var eb in existingBuilders)
                {
                    if (eb == null) continue;
                    if (eb.reservedNode != null) excludedNodes.Add(eb.reservedNode);
                    if (eb.lastAssignedNode != null) excludedNodes.Add(eb.lastAssignedNode);
                }
            }
        }
        catch { }

        // If no approach node provided (player-issued command), attempt to pick & reserve a sensible perimeter approach node
        if (preferredApproachNode == null && gm != null)
        {
            Node center = gm.NodeFromWorldPoint(building.transform.position);
            if (center != null)
            {
                // gather candidates ring-by-ring and choose closest-first, attempt reservation using helper
                Vector2Int start = gm.GetStartIndicesForCenteredFootprint(building.transform.position, building.Size);
                int innerMinX = start.x;
                int innerMinY = start.y;
                int innerMaxX = start.x + building.Size.x - 1;
                int innerMaxY = start.y + building.Size.y - 1;

                int searchRadius = Mathf.Max(2, Mathf.Max(building.Size.x, building.Size.y) + 2);
                Node best = null;

                try
                {
                    for (int r = 1; r <= searchRadius && best == null; r++)
                    {
                        var candidates = new List<Node>();
                        int minX = innerMinX - r;
                        int maxX = innerMaxX + r;
                        int minY = innerMinY - r;
                        int maxY = innerMaxY + r;

                        for (int x = minX; x <= maxX; x++)
                        {
                            for (int y = minY; y <= maxY; y++)
                            {
                                if (x != minX && x != maxX && y != minY && y != maxY) continue;
                                Node n = null;
                                try { n = gm.GetNode(x, y); } catch { n = null; }
                                if (n == null || !n.walkable) continue;
                                bool insideFootprint = (n.gridX >= innerMinX && n.gridX <= innerMaxX && n.gridY >= innerMinY && n.gridY <= innerMaxY);
                                if (insideFootprint) continue;
                                if (excludedNodes.Contains(n)) continue;
                                if (nrs != null && nrs.IsReserved(n)) continue;
                                candidates.Add(n);
                            }
                        }

                        if (candidates.Count == 0) continue;

                        // sort by distance to agent
                        candidates.Sort((a, b) =>
                        {
                            float da = (new Vector2(a.centerPosition.x - agent.transform.position.x, a.centerPosition.y - agent.transform.position.y)).sqrMagnitude;
                            float db = (new Vector2(b.centerPosition.x - agent.transform.position.x, b.centerPosition.y - agent.transform.position.y)).sqrMagnitude;
                            return da.CompareTo(db);
                        });

                        // attempt to reserve using the helper (which falls back to FindAndReserveBestNode)
                        foreach (var cand in candidates)
                        {
                            Node reserved = VillagerReservationHelper.TryReserveOrFindAlt(agent, cand, Mathf.Max(2, building.Size.x));
                            if (reserved != null && !excludedNodes.Contains(reserved))
                            {
                                best = reserved;
                                break;
                            }
                        }
                    }
                }
                catch { best = null; }

                if (best != null)
                {
                    preferredApproachNode = best;
                    try { agent.holdReservation = true; } catch { }
                    try { agent.lastAssignedNode = best; } catch { }
                    // Immediately set destination so player can see unit move
                    try { agent.SetDestinationToNode(preferredApproachNode, true); } catch { try { agent.SetDestinationToNode(preferredApproachNode); } catch { } }
                }
                else
                {
                    // fallback: choose nearest perimeter node outside footprint excluding already-taken nodes
                    Node fallback = FindClosestPerimeterNodeForAgent(gm, building, agent, excludedNodes) ?? gm.FindClosestWalkableNode(center.gridX, center.gridY);
                    if (fallback != null && !excludedNodes.Contains(fallback))
                    {
                        preferredApproachNode = fallback;
                        try { agent.SetDestinationToNode(fallback, true); } catch { try { agent.SetDestinationToNode(fallback); } catch { } }
                    }
                }
            }
        }
        else if (preferredApproachNode != null)
        {
            // caller-provided approach — ensure movement starts and reservation is respected
            try
            {
                Node reserved = VillagerReservationHelper.TryReserveOrFindAlt(agent, preferredApproachNode, Mathf.Max(2, building.Size.x));
                if (reserved != null)
                    preferredApproachNode = reserved;
            }
            catch { }

            try { agent.holdReservation = true; } catch { }
            try { agent.lastAssignedNode = preferredApproachNode; } catch { }

            try { agent.SetDestinationToNode(preferredApproachNode, true); } catch { try { agent.SetDestinationToNode(preferredApproachNode); } catch { } }
        }
    }

    // --- Gather (simplified) ---
    void HandleGatherUpdate()
    {
        if (returningToDropOff)
        {
            currentTask = VillagerTask.ReturnResources;

            DropOffBuilding drop = dropOff ?? FindCompletedDropoff();
            if (drop == null)
            {
                ClearGatherState();
                return;
            }

            var gm = GridManager.Instance;
            Node dropNode = null;
            if (drop != null)
                dropNode = drop.GetNearestDropoffNode(transform.position);

            if (dropNode == null && gm != null)
            {
                Node center = gm.NodeFromWorldPoint(drop.transform.position);
                if (center != null)
                    dropNode = gm.FindClosestWalkableNode(center.gridX, center.gridY);
            }

            if (dropNode != null)
            {
                // ask agent to go to drop node
                if (!agent.HasPath())
                    agent.SetDestinationToNode(dropNode);

                // arrival check using node diameter if available
                float tol = (GridManager.Instance != null) ? GridManager.Instance.nodeDiameter * 0.6f : 0.9f;
                if ((agent.transform.position - dropNode.centerPosition).sqrMagnitude <= (tol * tol))
                {
                    DepositCarried(drop);
                }

                return;
            }

            // If no node found, clear to avoid permanent stuck
            ClearGatherState();
            return;
        }

        // harvesting: check arrival to harvest spot
        bool atSpot = (agent.transform.position - myHarvestSpot).sqrMagnitude <= 0.5f * 0.5f;
        if (!atSpot) return;

        // instantaneous harvest (no timers)
        if (targetNode == null)
        {
            ClearGatherState();
            TryFindWork();
            return;
        }

        int space = carryCapacity - carriedTotal;
        if (space <= 0)
        {
            // already full -> return
            returningToDropOff = true;
            return;
        }

        int harvested = 0;
        try
        {
            harvested = targetNode.Harvest(space);
        }
        catch
        {
            harvested = 0;
            targetNode = null;
        }

        if (harvested > 0)
        {
            ResourceType rtype = targetNode != null ? targetNode.resourceType : default;
            if (carriedResources.ContainsKey(rtype)) carriedResources[rtype] += harvested;
            else carriedResources[rtype] = harvested;
            carriedTotal += harvested;
        }

        // If filled or node depleted -> return
        if (carriedTotal >= carryCapacity || targetNode == null)
        {
            returningToDropOff = true;

            // compute dropoff and send immediately
            var drop = FindCompletedDropoff();
            if (drop != null)
            {
                Node target = drop.GetNearestDropoffNode(transform.position);
                if (target == null && GridManager.Instance != null)
                {
                    Node center = GridManager.Instance.NodeFromWorldPoint(drop.transform.position);
                    if (center != null)
                        target = GridManager.Instance.FindClosestWalkableNode(center.gridX, center.gridY);
                }

                if (target != null)
                    agent.SetDestinationToNode(target);
                else
                    ClearGatherState();
            }
            else
            {
                ClearGatherState();
            }
        }
    }

    public void CommandGather(ResourceNode node, Vector3 harvestSpot, bool playerCommand = false)
    {
        ClearGatherState();
        if (node == null) return;

        // If this is NOT a player-issued command and owner has no dropoff -> treat as move / skip gather
        if (!playerCommand)
        {
            var dropCheck = FindCompletedDropoff();
            if (dropCheck == null)
            {
                CommandMove(harvestSpot);
                return;
            }
        }

        if (agent != null)
        {
            agent.SetIdle(false);

            // Ensure previous reservation is fully released so we don't snap back to spawn.
            try { agent.ClearCombatReservation(); } catch { }
            try { agent.reservedNode = null; agent.holdReservation = false; } catch { }
        }

        currentTask = VillagerTask.Gather;
        targetNode = node;

        var gm = GridManager.Instance;
        Node desired = gm != null ? gm.NodeFromWorldPoint(harvestSpot) : null;
        if (desired != null && desired.walkable)
        {
            myHarvestSpot = desired.centerPosition;

            // Player-issued: move directly to node center (bypass reservation so villager stays on node).
            if (playerCommand)
            {
                try { agent.MoveDirectToNode(desired); }
                catch { agent.SetDestinationToNode(desired); }
            }
            else
            {
                agent.SetDestinationToNode(desired);
            }
        }
        else if (gm != null)
        {
            Node chosen = gm.FindClosestWalkableNode(Mathf.RoundToInt(harvestSpot.x), Mathf.RoundToInt(harvestSpot.y));
            if (chosen != null)
            {
                myHarvestSpot = chosen.centerPosition;
                if (playerCommand)
                {
                    try { agent.MoveDirectToNode(chosen); }
                    catch { agent.SetDestinationToNode(chosen); }
                }
                else
                {
                    agent.SetDestinationToNode(chosen);
                }
            }
            else
            {
                myHarvestSpot = harvestSpot;
                Node fallback = gm.NodeFromWorldPoint(node.transform.position);
                if (fallback != null)
                {
                    if (playerCommand)
                    {
                        try { agent.MoveDirectToNode(fallback); }
                        catch { agent.SetDestinationToNode(fallback); }
                    }
                    else
                    {
                        agent.SetDestinationToNode(fallback);
                    }
                }
                else
                {
                    agent.SetDestinationToNode(null);
                }
            }
        }
        else
        {
            myHarvestSpot = harvestSpot;
            agent.SetDestinationToNode(null);
        }
    }

    void DepositCarried(DropOffBuilding drop)
    {
        if (drop == null) return;
        PlayerEconomy target = (drop.owner != null) ? drop.owner : ownerEconomy;
        if (target != null && carriedTotal > 0 && carriedResources.Count > 0)
        {
            foreach (var kv in carriedResources)
            {
                try { target.AddResource(kv.Key, kv.Value); } catch { }
            }
        }

        carriedResources.Clear();
        carriedTotal = 0;
        returningToDropOff = false;
        targetNode = null;

        // immediately search for more work
        TryFindWork();
    }

    void ClearGatherState()
    {
        targetNode = null;
        carriedResources.Clear();
        carriedTotal = 0;
        returningToDropOff = false;
        myHarvestSpot = Vector3.zero;
        currentTask = VillagerTask.Idle;
    }

    void ClearAllTasks()
    {
        buildingToConstruct = null;
        AssignedConstruction = null;
        preferredApproachNode = null;
        ClearGatherState();
    }

    public void CommandMove(Vector3 pos)
    {
        ClearAllTasks();
        currentTask = VillagerTask.Move;
        if (agent != null) agent.SetIdle(false);
        var gm = GridManager.Instance;
        Node n = gm != null ? gm.NodeFromWorldPoint(pos) : null;
        agent.SetDestinationToNode(n);
    }

    public void OnMoveCompleted()
    {
        if (currentTask != VillagerTask.Move) return;
        currentTask = VillagerTask.Idle;
        idleTimer = 0f;
    }

    // External systems can call this to force the villager to Idle state
    public void SetIdle()
    {
        ClearAllTasks();
        currentTask = VillagerTask.Idle;
        idleTimer = 0f;
        if (agent != null) try { agent.SetIdle(true); } catch { }
    }

    void TryFindWork() => VillagerSearchHelper.TryFindWork(this, 0f);

    internal DropOffBuilding FindCompletedDropoff()
    {
        var drops = Object.FindObjectsOfType<DropOffBuilding>();
        PlayerEconomy myOwner = agent != null ? agent.owner : null;
        foreach (var d in drops)
        {
            if (d == null) continue;
            var b = d.GetComponent<Building>();
            var bc = d.GetComponent<BuildingConstruction>();
            if ((b != null || (bc != null && bc.isFinished)) && (myOwner == null || d.owner == myOwner))
                return d;
        }
        return null;
    }

    // New: external assignment method used by recruiter
    public void AssignToConstruction(BuildingConstruction building)
    {
        if (building == null) return;
        if (AssignedConstruction == building) return;
        AssignedConstruction = building;
    }
}