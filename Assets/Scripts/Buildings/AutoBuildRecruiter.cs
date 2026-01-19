using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Auto-recruit / staggered recruitment logic for BuildingConstruction.
/// Assigns idle villagers to a construction and reserves unique approach nodes
/// on the immediate perimeter (outer ring) around the footprint. No fallbacks.
/// </summary>
[DisallowMultipleComponent]
public class AutoBuildRecruiter : MonoBehaviour
{
    BuildingConstruction bc;
    private bool autoRecruited = false;

    [Header("Recruitment")]
    [Tooltip("Delay (seconds) between recruit commands to spread CPU work")]
    public float recruitStagger = 0.06f;

    [Tooltip("Enable verbose recruitment diagnostics to the console.")]
    public bool verboseDebug = false;

    // Hard cap requested by design
    const int GLOBAL_MAX_BUILDERS = 5;

    void Awake()
    {
        bc = GetComponent<BuildingConstruction>();
    }

    public void ResetRecruitment()
    {
        autoRecruited = false;
    }

    public void AlertNearbyIdleVillagers()
    {
        if (autoRecruited) return;
        if (bc == null) { autoRecruited = true; return; }

        var registered = VillagerTaskSystem.AllRegisteredVillagers;
        if (registered == null || registered.Count == 0)
        {
            autoRecruited = true;
            return;
        }

        int existingBuilders = 0;
        try { existingBuilders = bc.GetBuilders().Count; } catch { existingBuilders = 0; }
        int remainingSlots = Mathf.Max(0, GLOBAL_MAX_BUILDERS - existingBuilders);
        if (remainingSlots == 0)
        {
            autoRecruited = true;
            return;
        }

        var idleList = new List<VillagerTaskSystem>();
        foreach (var v in registered)
        {
            if (v == null) continue;
            var ua = v.GetComponent<UnitAgent>();
            if (ua == null) continue;
            if (bc.AssignedOwner != null && ua.owner != bc.AssignedOwner) continue;
            if (!v.IsIdle()) continue;
            idleList.Add(v);
        }

        if (idleList.Count == 0)
        {
            autoRecruited = true;
            return;
        }

        int toRecruit = Mathf.Min(remainingSlots, idleList.Count);
        int scheduled = 0;
        for (int i = 0; i < idleList.Count && scheduled < toRecruit; i++)
        {
            var vill = idleList[i];
            if (vill == null) continue;
            StartCoroutine(RecruitDelayed(vill, scheduled * recruitStagger));
            scheduled++;
        }

        autoRecruited = true;
    }

    IEnumerator RecruitDelayed(VillagerTaskSystem vill, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (vill == null) yield break;
        if (!vill.IsIdle()) yield break;

        var ua = vill.GetComponent<UnitAgent>();
        var nrs = NodeReservationSystem.Instance;
        var gm = GridManager.Instance;

        if (nrs == null || gm == null || bc == null || ua == null)
            yield break;

        Vector2Int startIdx = gm.GetStartIndicesForCenteredFootprint(bc.transform.position, bc.Size);
        Node centerNode = gm.GetNode(startIdx.x + bc.Size.x / 2, startIdx.y + bc.Size.y / 2);

        if (centerNode == null)
        {
            if (verboseDebug) Debug.LogError($"[AutoBuildRecruiter] Center node is null for building at {bc.transform.position}.");
            yield break;
        }

        if (verboseDebug) Debug.Log($"[AutoBuildRecruiter] Searching for perimeter nodes around building at {bc.transform.position} for {ua.name}.");

        // Prefer reserving nodes on the immediate outer perimeter of the footprint,
        // choosing the node closest to the villager.
        Node reservedNode = ReservePerimeterNodeClosestToUnit(nrs, gm, bc, ua, searchRadius: 6);

        // fallback to the previous best-effort reservation if perimeter reservation failed
        if (reservedNode == null)
            reservedNode = nrs.FindAndReserveBestNode(centerNode, ua, searchRadius: 6);

        if (reservedNode == null)
        {
            if (verboseDebug) Debug.Log($"[AutoBuildRecruiter] Failed to reserve any perimeter node for {ua.name}; skipping.");
            yield break;
        }

        try { ua.holdReservation = true; } catch { }
        try { vill.CommandBuild(bc, reservedNode); } catch { }

        if (verboseDebug) Debug.Log($"[AutoBuildRecruiter] Reserved perimeter node {reservedNode.gridX},{reservedNode.gridY} for {ua.name} and assigned to build.");
    }

    // Collects perimeter nodes around the footprint up to `searchRadius`, then selects
    // candidates that are walkable and not reserved, sorts them by distance to `ua`
    // and attempts to ReserveNode in that order returning the first successful reservation.
    private Node ReservePerimeterNodeClosestToUnit(NodeReservationSystem nrs, GridManager gm, BuildingConstruction building, UnitAgent ua, int searchRadius = 4)
    {
        if (nrs == null || gm == null || building == null || ua == null) return null;

        Vector2Int start = gm.GetStartIndicesForCenteredFootprint(building.transform.position, building.Size);

        int innerMinX = start.x;
        int innerMinY = start.y;
        int innerMaxX = start.x + building.Size.x - 1;
        int innerMaxY = start.y + building.Size.y - 1;

        var candidates = new List<Node>();

        // gather perimeter nodes for rings 1..searchRadius
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
                    // only consider the perimeter cells for this ring
                    if (x != minX && x != maxX && y != minY && y != maxY) continue;

                    Node n = null;
                    try { n = gm.GetNode(x, y); } catch { n = null; }
                    if (n == null) continue;
                    if (!n.walkable) continue;
                    // skip already reserved nodes to avoid contention
                    if (nrs.IsReserved(n)) continue;

                    candidates.Add(n);
                }
            }

            // if we collected any candidates on this ring, try them (closest-first)
            if (candidates.Count > 0)
            {
                // sort by distance to unit
                candidates.Sort((a, b) =>
                {
                    float da = Vector2.SqrMagnitude(new Vector2(a.centerPosition.x - ua.transform.position.x, a.centerPosition.y - ua.transform.position.y));
                    float db = Vector2.SqrMagnitude(new Vector2(b.centerPosition.x - ua.transform.position.x, b.centerPosition.y - ua.transform.position.y));
                    return da.CompareTo(db);
                });

                // attempt to reserve in order
                foreach (var node in candidates)
                {
                    bool ok = false;
                    try { ok = nrs.ReserveNode(node, ua); } catch { ok = false; }
                    if (ok) return node;
                }

                // clear candidates for next ring
                candidates.Clear();
            }
        }

        return null;
    }
}