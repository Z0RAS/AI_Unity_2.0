using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

public class RTSCommandController : MonoBehaviour
{
    public Camera mainCamera;
    public bool useFormation = true;

    private void Update()
    {
        HandleRightClickInput();
        HandleHotkeys();
    }

    // Replaces the existing HandleRightClickInput implementation with verbose diagnostics.
    void HandleRightClickInput()
    {
        if (!Mouse.current.rightButton.wasPressedThisFrame) return;
        var selection = SelectionController.Instance;
        if (selection == null) return;
        List<UnitAgent> units = selection.selectedUnits;
        if (units == null || units.Count == 0) return;

        Vector2 mouseScreen = Mouse.current.position.ReadValue();
        Vector3 clickWorld = (mainCamera != null) ? mainCamera.ScreenToWorldPoint(mouseScreen) : Vector3.zero;
        clickWorld.z = 0f;


        // Detect UI blocking (common cause)
        #if UNITY_EDITOR || UNITY_STANDALONE
        var evt = UnityEngine.EventSystems.EventSystem.current;
        if (evt != null && evt.IsPointerOverGameObject())
        {
            return;
        }
        #endif

        Ray ray = (mainCamera != null) ? mainCamera.ScreenPointToRay(mouseScreen) : new Ray(clickWorld, Vector3.forward);
        RaycastHit2D hit = Physics2D.GetRayIntersection(ray);

        // If clicking a resource node, decide whether to Gather or just Move:
        if (hit.collider != null)
        {
            var resourceNode = hit.collider.GetComponentInParent<ResourceNode>();
            if (resourceNode != null)
            {
                // If any selected unit is not a villager -> treat as a normal move (not gather)
                bool anyNonWorker = units.Any(u => u != null && u.category != UnitCategory.Villager);

                // If all selected are villagers, ensure owner has a finished dropoff/base to allow gather behavior.
                bool allVillagers = !anyNonWorker;
                bool ownerHasBase = true;
                if (allVillagers)
                {
                    PlayerEconomy owner = units.Select(u => u?.owner).FirstOrDefault(o => o != null);
                    ownerHasBase = OwnerHasFinishedDropoff(owner);
                }

                // CASES:
                // - Mixed group (anyNonWorker) -> move (not gather).
                // - All villagers but owner has no base -> gather or move near node (do not stack).
                // - All villagers and owner has base -> gather.
                if (anyNonWorker)
                {
                    HandleMoveCommand(units, clickWorld);
                    return;
                }

                if (allVillagers && !ownerHasBase)
                {
                    var gm = GridManager.Instance;
                    Node targetNode = (gm != null) ? gm.NodeFromWorldPoint(resourceNode.transform.position) : null;
                    if (targetNode != null)
                    {
                        // Distribute selected villagers to nearby unoccupied nodes instead of allowing them
                        // to all move to the exact same node center (prevents stacking).
                        var chosen = new HashSet<Node>();
                        bool failed = false;

                        foreach (var u in units)
                        {
                            if (u == null) continue;

                            // Find a nearest unoccupied node for this unit that hasn't already been assigned.
                            Node assigned = GetNearestUnoccupiedNodeExcluding(gm, targetNode, units, chosen, 6);

                            // If we failed to find an assignment for any unit, fallback to normal move handling.
                            if (assigned == null)
                            {
                                failed = true;
                                break;
                            }

                            chosen.Add(assigned);

                            // Clear local reservation so SetDestinationToNode can reserve properly for this move.
                            try { NodeReservationSystem.Instance?.ReleaseReservation(u); } catch { }
                            u.holdReservation = false;
                            u.reservedNode = null;

                            // Use normal move (with reservation) so villagers walk normally and don't snap/teleport.
                            u.SetDestinationToNode(assigned, true);
                        }

                        if (!failed)
                        {
                            // Successfully assigned all units to distinct nearby nodes.
                            return;
                        }

                        // Fallback if distribution fails — let existing move logic handle it.
                        HandleMoveCommand(units, clickWorld);
                        return;
                    }
                    // no node -> fallback
                    HandleMoveCommand(units, clickWorld);
                    return;
                }

                // all villagers and owner has base -> perform gather
                HandleGatherCommand(units, resourceNode);
                return;
            }
            
            // If clicking a construction footprint (player clicked on a building under construction),
            // treat this as a build order: route villagers to perimeter approach nodes instead of moving
            // to the clicked world point (prevents them walking into the footprint center).
            var bc = hit.collider.GetComponentInParent<BuildingConstruction>();
            if (bc != null)
            {
                HandleBuildCommand(units, bc);
                return;
            }
        }

        // Fallback: default handling
        HandleMoveCommand(units, clickWorld);
    }

    void HandleBuildCommand(List<UnitAgent> units, BuildingConstruction bc)
    {
        foreach (var agent in units)
        {
            var vill = agent.GetComponent<VillagerTaskSystem>();
            if (vill != null) vill.CommandBuild(bc);
        }
    }

    // reserve approach nodes for unit targets to avoid stacking; fallback to SetTarget
    void HandleAttackCommand(List<UnitAgent> units, UnitCombat enemy)
    {
        if (units == null || units.Count == 0 || enemy == null) return;

        GridManager grid = GridManager.Instance;
        Node enemyNode = (grid != null) ? grid.NodeFromWorldPoint(enemy.transform.position) : null;
        var nrs = NodeReservationSystem.Instance;
        int searchRadius = 4;

        foreach (var agent in units)
        {
            if (agent == null) continue;

            Node reserved = null;
            if (nrs != null && enemyNode != null)
                reserved = nrs.FindAndReserveBestNode(enemyNode, agent, searchRadius);

            if (reserved != null)
            {
                agent.lastAssignedNode = reserved;
                agent.holdReservation = true;
                agent.SetDestinationToNode(reserved, true);
                agent.GetComponent<UnitCombat>()?.SetTargetKeepReservation(enemy);
                continue;
            }

            var cb = agent.GetComponent<UnitCombat>();
            if (cb != null) cb.SetTarget(enemy);
        }
    }

    void HandleAttackCommand_ForBuilding(List<UnitAgent> units, GameObject target)
    {
        if (units == null || units.Count == 0 || target == null) return;

        GridManager grid = GridManager.Instance;
        if (grid == null) { foreach (var a in units) a.GetComponent<UnitCombat>()?.SetTarget(target); return; }

        Node buildNode = grid.NodeFromWorldPoint(target.transform.position);
        if (buildNode == null) buildNode = grid.FindClosestWalkableNode(0, 0);

        var nrs = NodeReservationSystem.Instance;

        var candidates = new List<Node>();
        int searchRadius = 6;
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                Node n = grid.GetNode(buildNode.gridX + dx, buildNode.gridY + dy);
                if (n == null || !n.walkable) continue;
                candidates.Add(n);
            }
        }

        foreach (var agent in units)
        {
            if (agent == null) continue;

            Node reserved = null;
            if (nrs != null)
                reserved = nrs.FindAndReserveBestNode(buildNode, agent, searchRadius);

            // If we got a candidate reservation, ensure it's not physically occupied by another unit.
            if (reserved != null)
            {
                float physRadius = Mathf.Max(GridManager.Instance.nodeDiameter * 0.35f, 0.2f);
                Collider2D[] hits = Physics2D.OverlapCircleAll(reserved.centerPosition, physRadius);
                bool occupiedByOtherUnit = false;
                foreach (var h in hits)
                {
                    if (h == null) continue;
                    var other = h.GetComponent<UnitAgent>();
                    if (other != null && other != agent)
                    {
                        occupiedByOtherUnit = true;
                        break;
                    }
                }

                if (occupiedByOtherUnit)
                {
                    // Try to find an alternative close free node and reserve it.
                    Node alt = nrs.FindAndReserveBestNode(reserved, agent, 3);
                    if (alt != null)
                    {
                        reserved = alt;
                    }
                    else
                    {
                        // cannot find a nearby free node
                        reserved = null;
                    }
                }
            }

            if (reserved != null)
            {
                agent.lastAssignedNode = reserved;
                agent.holdReservation = true;
                agent.SetDestinationToNode(reserved, true);
                agent.GetComponent<UnitCombat>()?.SetTargetKeepReservation(target);
                // remove reserved node from candidates so other units don't try to use it
                candidates.Remove(reserved);
                continue;
            }

            // Fallback: attempt to reserve any nearby free node (wider radius) before giving up
            if (nrs != null)
            {
                Node fallbackAlt = nrs.FindAndReserveBestNode(buildNode, agent, 4);
                if (fallbackAlt != null)
                {
                    // ensure not occupied physically
                    float physRadius2 = Mathf.Max(GridManager.Instance.nodeDiameter * 0.35f, 0.2f);
                    Collider2D[] hits2 = Physics2D.OverlapCircleAll(fallbackAlt.centerPosition, physRadius2);
                    bool occupied2 = false;
                    foreach (var h2 in hits2)
                    {
                        if (h2 == null) continue;
                        var other2 = h2.GetComponent<UnitAgent>();
                        if (other2 != null && other2 != agent)
                        {
                            occupied2 = true;
                            break;
                        }
                    }

                    if (!occupied2)
                    {
                        agent.lastAssignedNode = fallbackAlt;
                        agent.holdReservation = true;
                        agent.SetDestinationToNode(fallbackAlt, true);
                        agent.GetComponent<UnitCombat>()?.SetTargetKeepReservation(target);
                        candidates.Remove(fallbackAlt);
                        continue;
                    }
                    else
                    {
                        // If occupied, release that reservation (unlikely) and continue fallback logic
                        try { NodeReservationSystem.Instance?.ReleaseReservation(agent); } catch { }
                    }
                }
            }

            // No safe approach node available — do not path into the building center or onto others.
            // Make the unit wait and let UnitCombat retry reservations/attacks (SetTargetKeepReservation)
            var cb = agent.GetComponent<UnitCombat>();
            if (cb != null)
                cb.SetTargetKeepReservation(target);

            agent.Stop();
        }
    }

    void HandleGatherCommand(List<UnitAgent> units, ResourceNode node)
    {
        List<Vector3> spots = node.GetHarvestSpots();
        for (int i = 0; i < units.Count; i++)
        {
            Vector3 spot = spots[i % spots.Count];
            var vill = units[i].GetComponent<VillagerTaskSystem>();
            if (vill != null) vill.CommandGather(node, spot, true);
        }
    }

    void HandleMoveCommand(List<UnitAgent> units, Vector3 clickWorld)
    {
        GridManager grid = GridManager.Instance;
        Node n = grid != null ? grid.NodeFromWorldPoint(clickWorld) : null;

        // Prevent commanding selection to move onto a node physically occupied by others.
        // For single-unit clicks, redirect to the nearest unoccupied node or abort if none.
        if (n != null && IsNodePhysicallyOccupiedByOther(n, units))
        {
            if (units.Count <= 1)
            {
                Node free = FindNearestUnoccupiedNode(grid, n, units, 6);
                if (free == null) return; // no safe location -> do nothing
                n = free;
            }
            else
            {
                // For groups, allow formation/distribution logic to handle avoiding occupied nodes.
                // (The rest of this method will either use formation or distribute to nearby nodes.)
            }
        }

        // allow formation handling for group moves but bypass formation when units are stacked at same node
        if (useFormation)
        {
            if (!(units.Count > 1 && AreUnitsStackedOnSameNode(units)))
            {
                SquadFormationController.Instance.MoveGroupWithFormation(units, clickWorld);
                return;
            }
            // else fall through to multi-unit distribution (stacked case)
        }

        // normalize target node
        if (n == null || !n.walkable)
        {
            if (n != null && grid != null) n = grid.FindClosestWalkableNode(n.gridX, n.gridY);
        }

        if (n == null) return;

        // If single unit -> target nearest unoccupied and move there
        if (units.Count <= 1)
        {
            Node free = FindNearestUnoccupiedNode(grid, n, units, 6);
            if (free == null) return;

            var u = units.FirstOrDefault();
            if (u == null) return;

            try { NodeReservationSystem.Instance?.ReleaseReservation(u); } catch { }
            u.holdReservation = false;
            u.reservedNode = null;
            u.SetDestinationToNode(free, true);
            return;
        }

        // MULTI-UNIT (no formation OR stacked fallback): distribute nearby free nodes to each unit so they can all move.
        var chosen = new HashSet<Node>();
        foreach (var u in units)
        {
            if (u == null) continue;

            // Find nearest unoccupied node for this unit, avoiding nodes already chosen for other units
            Node assigned = GetNearestUnoccupiedNodeExcluding(grid, n, units, chosen, 6);
            if (assigned == null)
            {
                // fallback: try the original target if not occupied by others
                if (!IsNodePhysicallyOccupiedByOther(n, units))
                    assigned = n;
            }

            if (assigned == null) continue;

            chosen.Add(assigned);

            try { NodeReservationSystem.Instance?.ReleaseReservation(u); } catch { }
            u.holdReservation = false;
            u.reservedNode = null;

            u.SetDestinationToNode(assigned, true);
        }
    }

    // new helper: like FindNearestUnoccupiedNode but excludes nodes present in `exclude`
    private Node GetNearestUnoccupiedNodeExcluding(GridManager gm, Node start, List<UnitAgent> ignoreList, HashSet<Node> exclude, int maxRadius)
    {
        if (gm == null || start == null) return null;

        if (!IsNodePhysicallyOccupiedByOther(start, ignoreList) && (exclude == null || !exclude.Contains(start)))
            return start;

        int cx = start.gridX;
        int cy = start.gridY;

        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue;
                    Node n = gm.GetNode(cx + dx, cy + dy);
                    if (n == null || !n.walkable) continue;
                    if (exclude != null && exclude.Contains(n)) continue;
                    if (IsNodePhysicallyOccupiedByOther(n, ignoreList)) continue;
                    return n;
                }
            }
        }

        return null;
    }

    void HandleHotkeys()
    {
        var squad = SquadFormationController.Instance;
        if (Keyboard.current.digit1Key.wasPressedThisFrame) squad.SetFormation(FormationType.Box);
        if (Keyboard.current.digit2Key.wasPressedThisFrame) squad.SetFormation(FormationType.Line);
        if (Keyboard.current.digit3Key.wasPressedThisFrame) squad.SetFormation(FormationType.Wedge);
        if (Keyboard.current.equalsKey.wasPressedThisFrame) { squad.formationWidth++; squad.ReformSelection(); }
        if (Keyboard.current.minusKey.wasPressedThisFrame) { squad.formationWidth = Mathf.Max(1, squad.formationWidth - 1); squad.ReformSelection(); }
    }

    // Helper: return true if owner has at least one finished DropOffBuilding (base) available.
    private bool OwnerHasFinishedDropoff(PlayerEconomy owner)
    {
        if (owner == null) return false;
        var drops = Object.FindObjectsOfType<DropOffBuilding>();
        foreach (var d in drops)
        {
            if (d == null) continue;
            if (d.owner != owner) continue;
            var b = d.GetComponent<Building>();
            var bc = d.GetComponent<BuildingConstruction>();
            if (b != null) return true;
            if (bc != null && bc.isFinished) return true;
        }
        return false;
    }

    // Finds nearest walkable node not physically occupied by another unit (ignores units in `ignoreList`).
    private Node FindNearestUnoccupiedNode(GridManager gm, Node start, List<UnitAgent> ignoreList, int maxRadius)
    {
        if (gm == null || start == null) return null;

        // quick check: if start is unoccupied (or only occupied by ignored units) accept it
        if (!IsNodePhysicallyOccupiedByOther(start, ignoreList))
            return start;

        int cx = start.gridX;
        int cy = start.gridY;

        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    // perimeter only to preserve locality
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue;
                    Node n = gm.GetNode(cx + dx, cy + dy);
                    if (n == null || !n.walkable) continue;
                    if (IsNodePhysicallyOccupiedByOther(n, ignoreList)) continue;
                    return n;
                }
            }
        }

        return null;
    }

    private bool IsNodePhysicallyOccupiedByOther(Node node, List<UnitAgent> ignoreList)
    {
        if (node == null) return false;
        float physRadius = Mathf.Max(GridManager.Instance != null ? GridManager.Instance.nodeDiameter * 0.35f : 0.3f, 0.15f);
        Collider2D[] hits = Physics2D.OverlapCircleAll(node.centerPosition, physRadius);
        foreach (var h in hits)
        {
            if (h == null) continue;
            var ua = h.GetComponent<UnitAgent>();
            if (ua == null) continue;
            // ignore units that are part of the issuing selection
            bool isIgnored = false;
            if (ignoreList != null)
            {
                for (int i = 0; i < ignoreList.Count; i++)
                {
                    if (ignoreList[i] == ua) { isIgnored = true; break; }
                }
            }
            if (!isIgnored) return true;
        }
        return false;
    }

    // Returns true when all units are on the same grid node (stacked).
    private bool AreUnitsStackedOnSameNode(List<UnitAgent> units)
    {
        if (units == null || units.Count == 0) return false;
        var gm = GridManager.Instance;
        if (gm == null) return false;
        Node first = null;
        foreach (var u in units)
        {
            if (u == null) continue;
            Node n = gm.NodeFromWorldPoint(u.transform.position);
            if (n == null) return false;
            if (first == null) first = n;
            else if (n != first) return false;
        }
        return true;
    }
}