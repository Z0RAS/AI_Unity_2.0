using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Economy.Helpers;

/// <summary>
/// Finalizer: wait until builders and any owned units inside the footprint are moved out to unique,
/// walkable, unreserved nodes, then finalize (create Building and occupy cells).
/// Guarantees:
///  - only collected (owner) units are moved
///  - each moved unit gets one reserved node and will not be re-assigned
///  - finalization happens only after all moved units reached their reserved nodes
///  - releases reservations and clears hold flags after relocation
/// </summary>
[DisallowMultipleComponent]
public class ConstructionFinalizer : MonoBehaviour
{
    BuildingConstruction bc;
    bool running = false;

    // Poll interval between checks (seconds) — removed timed waits, now polling each frame
    private const float waitPollInterval = 0f;

    void Awake()
    {
        bc = GetComponent<BuildingConstruction>();
    }

    public void StartFinalization()
    {
        if (running) return;
        StartCoroutine(FinalizeWhenAllCleared());
    }

    IEnumerator FinalizeWhenAllCleared()
    {
        if (bc == null) yield break;
        running = true;

        // Stop counting builders now; they'll be re-registered if needed later.
        bc.ClearBuilders();

        if (bc.progressCanvas != null)
            bc.progressCanvas.enabled = false;

        var gm = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;

        // Keep a stable map of assignments: UnitAgent -> reserved Node (never change once assigned)
        var assigned = new Dictionary<UnitAgent, Node>();

        // Main loop: keep scanning for owner builder units + any owner unit inside footprint,
        // assign them unique reserved nodes outside footprint, force them to move,
        // and wait until all have arrived. Finalize only when no such units remain.
        while (true)
        {
            // 1) recompute footprint indices (robust for even/odd sizes)
            if (gm == null)
            {
                gm = GridManager.Instance;
                if (gm == null) { break; }
            }
            if (bc.BuildingNode == null)
                bc.BuildingNode = gm.NodeFromWorldPoint(bc.transform.position);

            Vector2Int start = gm.GetStartIndicesForCenteredFootprint(bc.transform.position, bc.Size);
            int startX = start.x;
            int startY = start.y;
            int endXExclusive = startX + bc.Size.x;
            int endYExclusive = startY + bc.Size.y;

            // 2) collect units to move:
            // - villagers that are assigned to build this construction (vill.currentTask == Build && vill.AssignedConstruction == bc)
            // - any UnitAgent physically inside the footprint area
            // BUT ONLY if the unit belongs to the same owner as the building (bc.AssignedOwner).
            var toMove = new List<UnitAgent>();

            // collect villagers/builders (only those matching owner)
            var regs = VillagerTaskSystem.AllRegisteredVillagers;
            if (regs != null)
            {
                for (int i = 0; i < regs.Count; i++)
                {
                    var vill = regs[i];
                    if (vill == null) continue;
                    var ua = vill.GetComponent<UnitAgent>();
                    if (ua == null) continue;

                    // only consider units that belong to the building owner
                    if (bc.AssignedOwner != null && ua.owner != bc.AssignedOwner)
                        continue;

                    // If villager is assigned to build this construction, include them (they must be moved to outside nodes)
                    if (vill.AssignedConstruction == bc)
                    {
                        if (!toMove.Contains(ua)) toMove.Add(ua);
                    }
                }
            }

            // collect any other owner units physically inside footprint
            // compute world rectangle from node corners for precise OverlapAreaAll
            var n1 = gm.GetNode(startX, startY);
            var n2 = gm.GetNode(endXExclusive - 1, endYExclusive - 1);
            if (n1 != null && n2 != null)
            {
                Vector2 a = n1.centerPosition;
                Vector2 b = n2.centerPosition;
                Collider2D[] hits = Physics2D.OverlapAreaAll(a, b);
                if (hits != null)
                {
                    foreach (var h in hits)
                    {
                        if (h == null) continue;
                        UnitAgent u = h.GetComponent<UnitAgent>();
                        if (u == null) continue;

                        // only include units that belong to the building owner
                        if (bc.AssignedOwner != null && u.owner != bc.AssignedOwner)
                            continue;

                        if (!toMove.Contains(u)) toMove.Add(u);
                    }
                }
            }

            // Remove any dead/null or already-arrived units from toMove
            toMove.RemoveAll(u => u == null);

            // If nothing to move and no outstanding assignments -> safe to finalize
            if (toMove.Count == 0 && assigned.Count == 0)
            {
                break;
            }

            // For any unit in toMove not yet assigned, assign a node only once and do not reassign
            foreach (var u in toMove)
            {
                if (u == null) continue;
                if (assigned.ContainsKey(u))
                {
                    // Ensure hold flag is true for units we assigned previously
                    try { u.holdReservation = true; } catch { }
                    continue; // already assigned, do not reassign
                }

                // Try several nearby candidate nodes (sorted by distance) to handle narrow gaps/blocking units.
                var candidates = GetNearestCandidates(gm, startX, startY, endXExclusive, endYExclusive, u.transform.position, nrs, 8);
                if (candidates == null || candidates.Count == 0) continue;

                var vt = u.GetComponent<VillagerTaskSystem>();
                bool assignedThisUnit = false;
                foreach (var candidate in candidates)
                {
                    // Reserve the node first so other agents won't take it.
                    bool moved = false;
                    bool reserved = false;
                    try
                    {
                        if (nrs != null)
                            reserved = nrs.ReserveNode(candidate, u);
                    }
                    catch { reserved = false; }

                    if (reserved)
                    {
                        // Mark hold early so the reservation is not released by other logic
                        try { u.holdReservation = true; } catch { }
                    }

                    if (vt != null)
                    {
                        // For villagers: reserve first, then command them to move.
                        try
                        {
                            // If reservation succeeded, CommandMove will keep it; if reservation failed, CommandMove
                            // will attempt its own reservation logic.
                            vt.CommandMove(candidate.centerPosition);
                            moved = u.HasPath();
                        }
                        catch
                        {
                            moved = false;
                        }

                        if (!moved && reserved)
                        {
                            // Clean up reservation if the villager didn't accept the move
                            try { if (nrs != null) nrs.ReleaseReservation(u); } catch { }
                            try { u.holdReservation = false; } catch { }
                        }
                    }
                    else
                    {
                        // Non-villager path: attempt to reserve then force-set destination. If move fails, release.
                        if (!reserved)
                        {
                            try
                            {
                                if (nrs != null) reserved = nrs.ReserveNode(candidate, u);
                                if (reserved) try { u.holdReservation = true; } catch { }
                            }
                            catch { reserved = false; }
                        }

                        if (!reserved) continue;

                        try { u.holdReservation = true; } catch { }

                        try { u.ForceSetDestinationToNode(candidate); moved = u.HasPath(); } catch { moved = false; }

                        if (!moved)
                        {
                            try { if (nrs != null) nrs.ReleaseReservation(u); } catch { }
                            try { u.holdReservation = false; } catch { }
                        }
                    }

                    if (moved)
                    {
                        // Reservation was already acquired via ReserveNode; keep holdReservation true.
                        // Record assignment and don't reassign this unit again.
                        try { u.holdReservation = true; } catch { }

                        assigned[u] = candidate;
                        assignedThisUnit = true;
                        break;
                    }
                }

                if (!assignedThisUnit)
                {
                    // couldn't move to any candidate now; let finalizer retry next loop
                }
            }

            // Wait until every assigned unit has arrived at its assigned node (not necessarily reserved).
            bool allArrived = true;
            var assignedCopy = new List<KeyValuePair<UnitAgent, Node>>(assigned);
            foreach (var kv in assignedCopy)
            {
                var u = kv.Key;
                if (u == null)
                {
                    // unit died/removed -> cleanup reservation mapping
                    if (nrs != null)
                    {
                        try { nrs.ReleaseReservation(u); } catch { }
                    }
                    assigned.Remove(u);
                    continue;
                }

                // If unit still physically inside footprint, not arrived
                var posNode = gm.NodeFromWorldPoint(u.transform.position);
                bool inside =
                    !(posNode.gridX < startX || posNode.gridX >= endXExclusive ||
                      posNode.gridY < startY || posNode.gridY >= endYExclusive);

                // Prefer precise check using UnitAgent.IsAtReservedNodeCenter() if available
                bool atAssignedCenter = false;
                try { atAssignedCenter = u.IsAtReservedNodeCenter(0.18f); } catch { atAssignedCenter = false; }

                // Also accept arrival if unit is within a strict tolerance of the assigned node center
                bool atAssignedByDistance = false;
                try
                {
                    var assignedNode = kv.Value;
                    if (assignedNode != null && gm != null)
                    {
                        float tol = gm.nodeDiameter * 0.18f; // require close to center
                        atAssignedByDistance = Vector3.Distance(u.transform.position, assignedNode.centerPosition) <= tol;
                    }
                }
                catch { atAssignedByDistance = false; }

                if (atAssignedCenter || atAssignedByDistance || !inside)
                {
                    // arrived or already outside => release reservation (only releases if unit actually has one) and clear hold
                    if (nrs != null)
                    {
                        try { nrs.ReleaseReservation(u); } catch { }
                    }

                    try { u.holdReservation = false; } catch { }
                    assigned.Remove(u);

                    // Do not force builders to Idle here; they should remain assigned
                    // until the building is fully finalized. Final idle assignment
                    // is handled after finalization completes below.
                }
                else
                {
                    // Still waiting for this unit to leave footprint
                    allArrived = false;
                }
            }

            // If not all arrived, wait a short interval and loop (will not reassign existing units)
            if (!allArrived)
            {
                yield return null;
                continue;
            }

            // No assigned units remain (all arrived) but there might still be toMove units not yet assigned (due to reservation contention).
            // Loop will attempt new assignments for remaining toMove on next iteration.
            yield return null;
        } // end while

        // Now safe: all affected (owner) units moved out. Finalize building.

        bc.isFinished = true;
        bc.currentHealth = (bc.data != null) ? bc.data.maxHealth : bc.currentHealth;

        Building finishedBuilding = bc.gameObject.AddComponent<Building>();
        finishedBuilding.Init(bc.data, bc.AssignedOwner);

        if (GridManager.Instance != null)
            GridManager.Instance.OccupyCellsForBuilding(bc);

        // After finalizing, ensure the construction's colliders become solid so the building blocks movement
        try { bc.SetCollidersAsTrigger(false); } catch { }

        if (bc.AssignedOwner != null)
        {
            var drop = bc.GetComponent<DropOffBuilding>();
            if (drop != null)
                drop.owner = bc.AssignedOwner;
        }

        var comps = bc.GetComponents<IConstructionComplete>();
        if (comps != null)
        {
            foreach (var c in comps)
            {
                try { c.OnConstructionComplete(); } catch { }
            }
        }

        // After any building is finalized, set all villagers who were building this to Idle
        try
        {
            var regs2 = VillagerTaskSystem.AllRegisteredVillagers;
            if (regs2 != null)
            {
                for (int i = 0; i < regs2.Count; i++)
                {
                    var vill = regs2[i];
                    if (vill == null) continue;
                    var ua = vill.GetComponent<UnitAgent>();
                    if (ua == null) continue;
                    if (ua.owner != bc.AssignedOwner) continue;

                    // If villager was building this construction, set to Idle
                    if (vill.currentTask == VillagerTask.Build && vill.AssignedConstruction == bc)
                        vill.SetIdle();
                }
            }
        }
        catch { }

        // After any building is finalized, force all player-owned idle villagers to gather
        try
        {
            var regs3 = VillagerTaskSystem.AllRegisteredVillagers;
            if (regs3 != null)
            {
                for (int i = 0; i < regs3.Count; i++)
                {
                    var vill = regs3[i];
                    if (vill == null) continue;
                    var ua = vill.GetComponent<UnitAgent>();
                    if (ua == null) continue;
                    if (ua.owner != bc.AssignedOwner) continue;

                    // Only attempt to assign work for idle villagers
                    if (vill.IsIdle())
                        VillagerSearchHelper.TryFindWork(vill, 0f);
                }
            }
        }
        catch { }

        running = false;
        yield break;
    }


    Node FindNearestWalkableNodeOutsideGridRect(GridManager gm, int startX, int startY, int endXExclusive, int endYExclusive, Vector3 searchFrom, NodeReservationSystem reservationSystem = null)
    {
        Node best = null;
        float bestSqr = float.MaxValue;

        foreach (var n in gm.AllNodes)
        {
            if (n == null) continue;
            if (!n.walkable) continue;

            if (!(n.gridX < startX || n.gridX >= endXExclusive || n.gridY < startY || n.gridY >= endYExclusive))
                continue;

            // skip nodes reserved by anyone
            if (reservationSystem != null && reservationSystem.IsReserved(n))
                continue;

            float d = (n.centerPosition - searchFrom).sqrMagnitude;
            if (d < bestSqr)
            {
                bestSqr = d;
                best = n;
            }
        }

        return best;
    }

    // Return up to maxCount nearest walkable, unreserved nodes outside footprint
    List<Node> GetNearestCandidates(GridManager gm, int startX, int startY, int endXExclusive, int endYExclusive, Vector3 searchFrom, NodeReservationSystem reservationSystem, int maxCount)
    {
        var list = new List<(Node n, float d)>();
        foreach (var n in gm.AllNodes)
        {
            if (n == null) continue;
            if (!n.walkable) continue;
            if (!(n.gridX < startX || n.gridX >= endXExclusive || n.gridY < startY || n.gridY >= endYExclusive))
                continue;
            if (reservationSystem != null && reservationSystem.IsReserved(n))
                continue;
            float d = (n.centerPosition - searchFrom).sqrMagnitude;
            list.Add((n, d));
        }

        list.Sort((a, b) => a.d.CompareTo(b.d));
        var outList = new List<Node>();
        for (int i = 0; i < list.Count && outList.Count < maxCount; i++) outList.Add(list[i].n);
        return outList;
    }

    bool IsBaseBuilding(BuildingConstruction bcCheck)
    {
        if (bcCheck == null) return false;
        if (bcCheck.data != null && !string.IsNullOrEmpty(bcCheck.data.buildingName))
            return bcCheck.data.buildingName.ToLowerInvariant().Contains("base");
        // fallback: inspect name
        return bcCheck.gameObject.name.ToLowerInvariant().Contains("base");
    }
}