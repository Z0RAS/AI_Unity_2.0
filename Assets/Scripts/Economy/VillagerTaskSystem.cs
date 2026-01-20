using Economy.Helpers;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(UnitAgent))
]
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

    // Harvest timing (tick-driven)
    [Tooltip("Harvest one unit every N ticks (TimeController ticks).")]
    public int harvestIntervalTicks = 50;
    private int harvestTickCounter = 0;

    // Tick-based guard to avoid backlog-driven instant harvesting.
    private long lastHarvestTick = -999;

    // Small startup delay (ticks) after arriving before harvesting to avoid same-tick arrival+harvest.
    private int initialHarvestDelayTicks = 0;

    // BUILD state (kept minimal)
    BuildingConstruction buildingToConstruct;
    // preferred approach node (reserved by recruiter) — must be honored by CommandBuild/HandleBuildUpdate
    Node preferredApproachNode;

    // idle / scheduling converted to ticks
    private int idleTickCounter = 0;
    private int idleIntervalTicks = 1;
    // legacy float fallback used only when TimeController is absent
    float idleTimer = 0f;

    void Awake()
    {
        agent = GetComponent<UnitAgent>();
    }

    void OnEnable()
    {
        // register into global list for systems that query all villagers
        if (!allRegisteredVillagers.Contains(this)) allRegisteredVillagers.Add(this);

        // subscribe to tick for harvest timing and other tick-driven behaviors
        try { TimeController.OnTick -= OnTick; } catch { }
        try { TimeController.OnTick += OnTick; } catch { }
    }

    void OnDisable()
    {
        if (allRegisteredVillagers.Contains(this)) allRegisteredVillagers.Remove(this);
        try { TimeController.OnTick -= OnTick; } catch { }
    }

    void OnDestroy()
    {
        try { TimeController.OnTick -= OnTick; } catch { }
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

        // compute tick-based intervals if TimeController exists, otherwise fall back to seconds-based behavior
        if (TimeController.Instance != null)
        {
            idleIntervalTicks = Mathf.Max(1, Mathf.RoundToInt(idleSearchInterval / Mathf.Max(TimeController.Instance.tickInterval, 1e-6f)));
        }
        else
        {
            // fallback guess (if no TimeController): assume 50 TPS as a reasonable default for converting seconds -> ticks
            idleIntervalTicks = Mathf.Max(1, Mathf.RoundToInt(idleSearchInterval / 0.02f));
        }

        // randomize initial idle counter to stagger villagers
        idleTickCounter = Random.Range(0, Mathf.Max(1, idleIntervalTicks / 2));

        // legacy float fallback: schedule first check in seconds (only used if TimeController absent)
        idleTimer = Time.time + idleSearchInterval * Random.Range(0.0f, 0.5f);
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

    // Keep Update light — all behavior is tick-driven for performance.
    void Update()
    {
        // Legacy fallback: if TimeController not present, keep original idle timing behavior here.
        if (TimeController.Instance == null)
        {
            if (currentTask == VillagerTask.Idle)
            {
                if (Time.time >= idleTimer)
                {
                    // fallback search uses force to keep behavior consistent when no TimeController exists
                    VillagerSearchHelper.TryFindWork(this, 0f, force: true);
                    idleTimer = Time.time + idleSearchInterval;
                }
            }

            // We still leave build/gather checks to Update when no TimeController exists.
            if (currentTask == VillagerTask.Build)
                HandleBuildUpdate();
            if (currentTask == VillagerTask.Gather)
                HandleGatherUpdate();
        }
    }

    // Central tick handler — runs all villager logic on TimeController ticks.
    void OnTick(float dt)
    {
        if (TimeController.Instance == null) return; // safety

        // BUILD: run build logic on tick
        if (currentTask == VillagerTask.Build)
        {
            HandleBuildUpdate(); // updated to be safe when called from ticks
            // no return; allow other logic to be considered below
        }

        // RETURN RESOURCES: explicit state handling (fixes stalling where units remain stuck in ReturnResources)
        if (currentTask == VillagerTask.ReturnResources)
        {
            // Mirror the returning-to-dropoff behavior previously executed inside the Gather handler.
            DropOffBuilding drop = dropOff ?? FindCompletedDropoff();
            if (drop == null)
            {
                // no dropoff -> clear gather state and fall back to idle search
                ClearGatherState();
                return;
            }

            var gm = GridManager.Instance;
            Node dropNode = null;
            if (drop != null)
            {
                // prefer tick-consistent sim position
                Vector3 searchPos = agent != null ? agent.SimPosition : transform.position;
                dropNode = drop.GetNearestDropoffNode(searchPos);
            }

            if (dropNode == null && gm != null)
            {
                Node center = gm.NodeFromWorldPoint(drop.transform.position);
                if (center != null)
                    dropNode = gm.FindClosestWalkableNode(center.gridX, center.gridY);
            }

            if (dropNode != null)
            {
                var nrs = NodeReservationSystem.Instance;
                if (nrs != null)
                {
                    try
                    {
                        Node reserved = VillagerReservationHelper.TryReserveOrFindAlt(agent, dropNode, 2);
                        if (reserved != null) dropNode = reserved;
                    }
                    catch { }
                }

                // Always set destination to drop node to ensure villager heads there
                try { agent.SetDestinationToNode(dropNode); } catch { }

                // Use multiple arrival checks: reserved-node center, or distance-based tolerance.
                float tol = (GridManager.Instance != null) ? GridManager.Instance.nodeDiameter * 0.8f : 0.9f;
                bool arrived = false;

                try
                {
                    // If the agent holds this reservation, prefer reserved-center check
                    if (agent != null && (agent.reservedNode == dropNode || agent.holdReservation))
                    {
                        arrived = agent.IsAtReservedNodeCenter(0.25f);
                    }

                    // Fallback: distance check using SimPosition for tick-accurate checks
                    if (!arrived)
                    {
                        Vector3 sim = agent != null ? agent.SimPosition : transform.position;
                        float d = Vector2.Distance(new Vector2(sim.x, sim.y), dropNode.centerPosition);
                        if (d <= tol) arrived = true;
                    }
                }
                catch
                {
                    // conservative fallback
                    Vector3 sim2 = agent != null ? agent.SimPosition : transform.position;
                    float d2 = Vector2.Distance(new Vector2(sim2.x, sim2.y), dropNode.centerPosition);
                    if (d2 <= tol) arrived = true;
                }

                if (arrived)
                {
                    DepositCarried(drop);
                }

                return;
            }

            // If no node found, clear to avoid permanent stuck
            ClearGatherState();
            return;
        }

        // GATHER: handle harvesting on ticks (including the transition that sets returningToDropOff)
        if (currentTask == VillagerTask.Gather)
        {
            // If we're supposed to be gathering but have no target (node got removed),
            // fall back to searching for work again so this villager keeps working.
            if (targetNode == null && !returningToDropOff)
            {
                // reset state to idle so TryFindWork can assign new target
                currentTask = VillagerTask.Idle;
                // force immediate search to ensure all idle villagers attempt to get work
                VillagerSearchHelper.TryFindWork(this, 0f, force: true);
                return;
            }

            // If we're returning, the return path is handled by the explicit ReturnResources state above.
            // However, preserve existing behavior when return is initiated inside this tick.
            if (returningToDropOff)
            {
                // Transition to explicit return state so subsequent ticks continue return handling.
                currentTask = VillagerTask.ReturnResources;

                // Execute same first-step logic (attempt a reservation and set destination) now; completion handled by ReturnResources block next tick.
                var drop = dropOff ?? FindCompletedDropoff();
                if (drop == null)
                {
                    ClearGatherState();
                    return;
                }

                var gm = GridManager.Instance;
                Node dropNode = null;
                if (drop != null)
                    dropNode = drop.GetNearestDropoffNode(agent != null ? agent.SimPosition : transform.position);

                if (dropNode == null && gm != null)
                {
                    Node center = gm.NodeFromWorldPoint(drop.transform.position);
                    if (center != null)
                        dropNode = gm.FindClosestWalkableNode(center.gridX, center.gridY);
                }

                if (dropNode != null)
                {
                    var nrs = NodeReservationSystem.Instance;
                    if (nrs != null)
                    {
                        try
                        {
                            Node reserved = VillagerReservationHelper.TryReserveOrFindAlt(agent, dropNode, 2);
                            if (reserved != null) dropNode = reserved;
                        }
                        catch { }
                    }

                    // Always set destination to drop node to ensure villager heads there
                    agent.SetDestinationToNode(dropNode);

                    // Check arrival immediately (same logic as before)
                    float tol = (GridManager.Instance != null) ? GridManager.Instance.nodeDiameter * 0.8f : 0.9f;
                    bool arrived = false;

                    try
                    {
                        if (agent.reservedNode == dropNode || agent.holdReservation)
                        {
                            arrived = agent.IsAtReservedNodeCenter(0.25f);
                        }

                        if (!arrived)
                        {
                            Vector3 sim = agent.SimPosition;
                            float d = Vector2.Distance(new Vector2(sim.x, sim.y), dropNode.centerPosition);
                            if (d <= tol) arrived = true;
                        }
                    }
                    catch
                    {
                        Vector3 sim2 = agent.SimPosition;
                        float d2 = Vector2.Distance(new Vector2(sim2.x, sim2.y), dropNode.centerPosition);
                        if (d2 <= tol) arrived = true;
                    }

                    if (arrived)
                    {
                        DepositCarried(drop);
                    }

                    return;
                }

                ClearGatherState();
                return;
            }

            // harvesting: check arrival to harvest spot (use SimPosition for tick-driven accuracy)
            Vector3 simPosHarvest = agent != null ? agent.SimPosition : transform.position;
            bool atSpot = (simPosHarvest - myHarvestSpot).sqrMagnitude <= 0.5f * 0.5f;
            if (!atSpot)
            {
                // reset counter when not at spot to avoid fast catch-ups
                harvestTickCounter = 0;
                return;
            }

            // If we just arrived to begin gathering, require the initial settle ticks before harvesting.
            if (initialHarvestDelayTicks > 0)
            {
                if (verboseDebug) Debug.Log($"[Villager] {name} settling for {initialHarvestDelayTicks} tick(s) before harvesting.");
                initialHarvestDelayTicks--;
                harvestTickCounter = 0; // ensure counter doesn't accumulate during settle
                return;
            }

            // Use tick-index guard to ensure one harvest per configured tick interval.
            long currentTick = TimeController.GlobalTickIndex;
            long ticksSinceLast = currentTick - lastHarvestTick;
            if (lastHarvestTick >= 0 && ticksSinceLast < Mathf.Max(1, harvestIntervalTicks))
            {
                // still waiting for required tick interval
                return;
            }

            // perform a single harvest now and mark lastHarvestTick
            lastHarvestTick = currentTick;

            // Attempt to harvest one unit
            int harvested = 0;
            try
            {
                if (targetNode != null) harvested = targetNode.Harvest(1);
            }
            catch
            {
                harvested = 0;
                targetNode = null;
            }

            if (harvested <= 0)
            {
                // node depleted or invalid -> clear and try find new work next tick
                targetNode = null;
                ClearGatherState();
                VillagerSearchHelper.TryFindWork(this, 0f, force: true);
                return;
            }

            // Add to carried resources
            ResourceType rtype = targetNode != null ? targetNode.resourceType : default;
            if (carriedResources.ContainsKey(rtype)) carriedResources[rtype] += harvested;
            else carriedResources[rtype] = harvested;
            carriedTotal += harvested;

            if (verboseDebug) Debug.Log($"[Villager] {name} harvested {harvested} of {rtype} (carried {carriedTotal}/{carryCapacity}).");

            // If filled -> return to dropoff
            if (carriedTotal >= carryCapacity)
            {
                returningToDropOff = true;
            }

            return;
        } // end gather

        // Idle tick handling
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

            // count ticks and run idle search when interval reached
            idleTickCounter++;
            if (idleTickCounter >= idleIntervalTicks)
            {
                idleTickCounter = 0;
                // force all idle villagers to attempt work (fix: many were never trying due to stagger slot)
                VillagerSearchHelper.TryFindWork(this, 0f, force: true);
            }
        }
    }

    // --- Build (kept mostly as before but safe to call from ticks) ---
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

                    Vector3 uaPos = ua != null ? ua.SimPosition : ua.transform.position;
                    float dsq = (new Vector2(n.centerPosition.x - uaPos.x, n.centerPosition.y - uaPos.y)).sqrMagnitude;
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

                        // sort by distance to agent (use SimPosition for tick-aware behavior)
                        Vector3 aPos = agent != null ? agent.SimPosition : agent.transform.position;
                        candidates.Sort((a, b) =>
                        {
                            float da = (new Vector2(a.centerPosition.x - aPos.x, a.centerPosition.y - aPos.y)).sqrMagnitude;
                            float db = (new Vector2(b.centerPosition.x - aPos.x, b.centerPosition.y - aPos.y)).sqrMagnitude;
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

    // --- Gather (kept minimal) ---
    void HandleGatherUpdate()
    {
        // This method remains callable from ticks or Update fallback, but contains only arrival/movement logic.
        if (returningToDropOff)
        {
            // Return logic handled fully in OnTick when TimeController present.
            return;
        }

        // harvesting: check arrival to harvest spot (if called from Update fallback we still rely on transform)
        Vector3 pos = (TimeController.Instance != null && agent != null) ? agent.SimPosition : transform.position;
        bool atSpot = (pos - myHarvestSpot).sqrMagnitude <= 0.5f * 0.5f;
        if (!atSpot) return;

        // harvesting is driven by TimeController ticks in OnTick, so do not perform instantaneous harvest here.
    }

    // Helper that finds a reasonable dropoff node for a building (best-effort).
    Node GetBestDropoffNode(DropOffBuilding drop)
    {
        if (drop == null) return null;
        var gm = GridManager.Instance;
        if (gm == null) return null;

        Node n = drop.GetNearestDropoffNode(transform.position);
        if (n != null) return n;

        Node center = gm.NodeFromWorldPoint(drop.transform.position);
        if (center != null)
        {
            // prefer perimeter nodes, otherwise closest walkable
            Node p = gm.FindClosestWalkableNode(center.gridX, center.gridY);
            if (p != null) return p;
        }

        return null;
    }

    public void CommandGather(ResourceNode node, Vector3 harvestSpot, bool playerCommand = false)
    {
        // Strong reset: drop current tasks and carried resources so player-issued commands take immediate precedence.
        ClearAllTasks(); // clears build/gather state and carried resources

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
            // Ensure agent is active and clear any local/global reservations so the new player command is authoritative.
            agent.SetIdle(false);

            try { agent.ClearCombatReservation(); } catch { }
            try { NodeReservationSystem.Instance?.ReleaseReservation(agent); } catch { }
            try { agent.reservedNode = null; agent.holdReservation = false; } catch { }

            // Cancel any existing path so we don't later snap back to it.
            try { agent.Stop(); } catch { }
        }

        // Set gather state for the new command
        currentTask = VillagerTask.Gather;
        targetNode = node;
        harvestTickCounter = 0;
        returningToDropOff = false;

        // prevent immediate harvesting in the same tick we arrive — give first harvest interval as guard
        initialHarvestDelayTicks = 1;

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

        // Release any dropoff reservation so other villagers may use it
        try { NodeReservationSystem.Instance?.ReleaseReservation(agent); } catch { }
        try { agent.reservedNode = null; agent.holdReservation = false; } catch { }

        // Clear carried resources and return state
        carriedResources.Clear();
        carriedTotal = 0;
        returningToDropOff = false;
        targetNode = null;

        // Ensure villager returns to Idle state so OnTick idle logic / TryFindWork can run reliably.
        // Reset harvest/idle counters so they can be scheduled immediately.
        currentTask = VillagerTask.Idle;
        harvestTickCounter = 0;
        idleTickCounter = 0;

        // Force immediate search bypassing stagger so unit resumes work promptly.
        VillagerSearchHelper.TryFindWork(this, 0f, force: true);
    }

    void ClearGatherState()
    {
        targetNode = null;
        carriedResources.Clear();
        carriedTotal = 0;
        returningToDropOff = false;
        myHarvestSpot = Vector3.zero;
        harvestTickCounter = 0;
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

        // reset tick counters for immediate idle scheduling
        idleTickCounter = 0;
        // legacy fallback
        idleTimer = 0f;
    }

    // External systems can call this to force the villager to Idle state
    public void SetIdle()
    {
        ClearAllTasks();
        currentTask = VillagerTask.Idle;

        // reset tick counters for immediate idle scheduling
        idleTickCounter = 0;
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