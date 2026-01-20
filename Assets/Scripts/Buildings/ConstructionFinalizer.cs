using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Economy.Helpers;

/// <summary>
/// Finalizer: finalize construction without moving owner units out of the footprint.
/// Reason: building placement prevents building on top of units and AutoBuildRecruiter already
/// places builders outside the footprint. Finalizer no longer attempts to relocate units.
/// </summary>
[DisallowMultipleComponent]
public class ConstructionFinalizer : MonoBehaviour
{
    BuildingConstruction bc;
    bool running = false;

    void Awake()
    {
        bc = GetComponent<BuildingConstruction>();
    }

    // Note: OnTick / coroutine-based relocation removed intentionally.
    // Finalization is immediate when StartFinalization is called.

    public void StartFinalization()
    {
        if (running) return;

        // Stop counting builders now; they'll be re-registered if needed later.
        try { bc.ClearBuilders(); } catch { }

        if (bc.progressCanvas != null)
            try { bc.progressCanvas.enabled = false; } catch { }

        // Immediately finalize — we explicitly do NOT attempt to move any units out of the footprint.
        // (Build placement should ensure no units are standing in the footprint.)
        FinalizeNow();
    }

    // Common finalization logic shared by all paths
    private void FinalizeNow()
    {
        // Now safe: finalize building.
        try { bc.isFinished = true; } catch { }
        try { bc.currentHealth = (bc.data != null) ? bc.data.maxHealth : bc.currentHealth; } catch { }

        try
        {
            Building finishedBuilding = bc.gameObject.AddComponent<Building>();
            finishedBuilding.Init(bc.data, bc.AssignedOwner);
        }
        catch { }

        try
        {
            if (GridManager.Instance != null)
                GridManager.Instance.OccupyCellsForBuilding(bc);
        }
        catch { }

        try { bc.SetCollidersAsTrigger(false); } catch { }

        try
        {
            if (bc.AssignedOwner != null)
            {
                var drop = bc.GetComponent<DropOffBuilding>();
                if (drop != null)
                    drop.owner = bc.AssignedOwner;
            }
        }
        catch { }

        try
        {
            var comps = bc.GetComponents<IConstructionComplete>();
            if (comps != null)
            {
                foreach (var c in comps)
                {
                    try { c.OnConstructionComplete(); } catch { }
                }
            }
        }
        catch { }

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

                    if (vill.currentTask == VillagerTask.Build && vill.AssignedConstruction == bc)
                        vill.SetIdle();
                }
            }
        }
        catch { }

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

                    if (vill.IsIdle())
                        VillagerSearchHelper.TryFindWork(vill, 0f);
                }
            }
        }
        catch { }

        running = false;
    }

    // The finalizer no longer relocates units, so the candidate search helpers remain
    // only for potential future use and are harmless if unused. Keeping them avoids larger diffs.
    List<Node> GetNearestCandidatesAroundPoint(GridManager gm, int startX, int startY, int endXExclusive, int endYExclusive, Vector3 searchFrom, NodeReservationSystem reservationSystem, int maxCount, int maxRadius)
    {
        var outList = new List<Node>();
        if (gm == null) return outList;

        Node startNode = null;
        try { startNode = gm.NodeFromWorldPoint(searchFrom); } catch { startNode = null; }
        if (startNode == null)
        {
            try { startNode = bc.BuildingNode; } catch { startNode = null; }
            if (startNode == null) startNode = gm.FindClosestWalkableNode((startX + endXExclusive) / 2, (startY + endYExclusive) / 2);
            if (startNode == null) return outList;
        }

        int cx = startNode.gridX;
        int cy = startNode.gridY;

        for (int r = 0; r <= maxRadius; r++)
        {
            bool anyThisRing = false;
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue;
                    int nx = cx + dx;
                    int ny = cy + dy;
                    Node n = null;
                    try { n = gm.GetNode(nx, ny); } catch { n = null; }
                    if (n == null || !n.walkable) continue;

                    if (!(n.gridX < startX || n.gridX >= endXExclusive || n.gridY < startY || n.gridY >= endYExclusive))
                        continue;

                    if (reservationSystem != null && reservationSystem.IsReserved(n)) continue;

                    outList.Add(n);
                    anyThisRing = true;
                }
            }

            if (anyThisRing)
            {
                outList.Sort((a, b) =>
                {
                    float da = (a.centerPosition - searchFrom).sqrMagnitude;
                    float db = (b.centerPosition - searchFrom).sqrMagnitude;
                    return da.CompareTo(db);
                });

                if (outList.Count > maxCount)
                    outList.RemoveRange(maxCount, outList.Count - maxCount);

                return outList;
            }
        }

        return outList;
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
        return bcCheck.gameObject.name.ToLowerInvariant().Contains("base");
    }
}