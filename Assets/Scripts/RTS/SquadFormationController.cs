using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class SquadFormationController : MonoBehaviour
{
    public static SquadFormationController Instance;

    public FormationType formationType = FormationType.Line;
    public bool holdFormation = false;
    public bool rotateFormation = true;
    public int formationWidth = 5;
    public Vector3 lastFormationTarget;
    private Vector2 lastMoveDirection;

    private int nextFormationId = 1;
    private readonly Dictionary<int, List<UnitAgent>> formationUnits = new Dictionary<int, List<UnitAgent>>();

    // monitor map for tick-driven completion checks
    private class FormationMonitor
    {
        public int id;
        public List<UnitAgent> units;
        public float stableTimer;
        public float elapsed;
    }
    private readonly Dictionary<int, FormationMonitor> monitors = new Dictionary<int, FormationMonitor>();

    private void Awake()
    {
        Instance = this;
    }

    private void OnEnable()
    {
        if (TimeController.Instance != null)
            TimeController.OnTick += OnTick;
    }

    private void OnDisable()
    {
        if (TimeController.Instance != null)
            TimeController.OnTick -= OnTick;
    }

    private void OnTick(float dt)
    {
        // update monitors
        var toRemove = new List<int>();
        foreach (var kv in monitors)
        {
            var m = kv.Value;
            bool allClose = true;
            float arriveRadius = 0.25f;

            foreach (var u in m.units)
            {
                if (u == null) { allClose = false; break; }
                if (u.formationId != m.id) { allClose = false; break; }

                Node assignedNode = u.lastAssignedNode;
                if (assignedNode == null) { allClose = false; break; }

                float d = Vector2.Distance(u.transform.position, assignedNode.centerPosition);
                if (d > arriveRadius) { allClose = false; break; }
            }

            if (allClose) m.stableTimer += dt; else m.stableTimer = 0f;

            m.elapsed += dt;
            float holdTime = 0.5f;
            float timeout = 8f;

            if (m.stableTimer >= holdTime || m.elapsed >= timeout)
            {
                // complete monitor
                foreach (var u in m.units)
                {
                    if (u == null) continue;
                    if (u.formationId != m.id) continue;
                    u.ClearFormationAssignment();
                    NodeReservationSystem.Instance?.ReleaseReservation(u);
                }
                toRemove.Add(kv.Key);
            }
        }

        foreach (var id in toRemove) monitors.Remove(id);
    }

    // unchanged MoveGroupWithFormation (commands units) - movement ticks handled by UnitAgent
    public void MoveGroupWithFormation(List<UnitAgent> units, Vector3 clickWorld)
    {
        if (units == null || units.Count == 0) return;

        lastFormationTarget = clickWorld;
        GridManager grid = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;

        foreach (var u in units)
            nrs?.ReleaseReservation(u);

        Vector3 groupCenter3D = GetGroupCenter(units);
        Vector2 center2D = new Vector2(groupCenter3D.x, groupCenter3D.y);
        Vector2 click2D = new Vector2(clickWorld.x, clickWorld.y);

        Vector2 dirRaw = (click2D - center2D);
        Vector2 dir = dirRaw.normalized;
        if (dirRaw.sqrMagnitude < 0.001f)
            dir = lastMoveDirection;

        Vector2 axisDir;
        if (Mathf.Abs(dirRaw.x) >= Mathf.Abs(dirRaw.y))
            axisDir = new Vector2(Mathf.Sign(dirRaw.x != 0f ? dirRaw.x : (lastMoveDirection.x != 0f ? lastMoveDirection.x : 1f)), 0f);
        else
            axisDir = new Vector2(0f, Mathf.Sign(dirRaw.y != 0f ? dirRaw.y : (lastMoveDirection.y != 0f ? lastMoveDirection.y : 1f)));

        if (dirRaw.sqrMagnitude < 0.0001f && lastMoveDirection.sqrMagnitude > 0.0001f)
        {
            if (Mathf.Abs(lastMoveDirection.x) >= Mathf.Abs(lastMoveDirection.y))
                axisDir = new Vector2(Mathf.Sign(lastMoveDirection.x), 0f);
            else
                axisDir = new Vector2(0f, Mathf.Sign(lastMoveDirection.y));
        }

        lastMoveDirection = dir;

        float baseAngle = Mathf.Atan2(axisDir.y, axisDir.x) * Mathf.Rad2Deg;

        float angle;
        if (formationType == FormationType.Wedge)
            angle = baseAngle + 90f + 180f;
        else
            angle = baseAngle - 90f;

        Vector2 orderingDir = (formationType == FormationType.Wedge) ? axisDir.normalized : dir.normalized;

        Node centerNode = grid.NodeFromWorldPoint(clickWorld);
        if (centerNode == null || !centerNode.walkable)
        {
            if (centerNode != null) centerNode = grid.FindClosestWalkableNode(centerNode.gridX, centerNode.gridY);
        }
        if (centerNode == null) return;

        units.Sort((a, b) => a.GetFormationPriority().CompareTo(b.GetFormationPriority()));

        List<Vector2Int> offsets = GenerateGridOffsets(units.Count);

        if (rotateFormation)
        {
            for (int i = 0; i < offsets.Count; i++)
                offsets[i] = RotateGridOffset(offsets[i], angle);
        }

        List<Vector2Int> orderedOffsets = OrderOffsetsByDirectionAndRows(offsets, orderingDir, units.Count);

        var desiredNodes = new List<Node>(orderedOffsets.Count);
        foreach (var o in orderedOffsets)
        {
            int slotX = centerNode.gridX + o.x;
            int slotY = centerNode.gridY + o.y;

            Node desired = grid.GetNode(slotX, slotY);
            if (desired == null || !desired.walkable) desired = grid.FindClosestWalkableNode(slotX, slotY);
            if (desired != null) desiredNodes.Add(desired);
        }

        int formationId = nextFormationId++;
        var assignedUnits = new List<UnitAgent>();
        formationUnits[formationId] = assignedUnits;

        var unassignedUnits = new List<UnitAgent>(units);
        var unassignedNodes = new List<Node>(desiredNodes);

        // keep previous assignments first
        for (int i = unassignedUnits.Count - 1; i >= 0; i--)
        {
            var unit = unassignedUnits[i];
            if (unit == null || unit.lastAssignedNode == null) continue;

            int idx = -1;
            float keepThreshold = 0.5f; // node center distance tolerance
            for (int j = 0; j < unassignedNodes.Count; j++)
            {
                if (unassignedNodes[j] == null) continue;
                if (Vector2.Distance(unassignedNodes[j].centerPosition, unit.lastAssignedNode.centerPosition) <= keepThreshold)
                {
                    idx = j;
                    break;
                }
            }

            if (idx >= 0)
            {
                Node candidateBase = unassignedNodes[idx];
                Node nodeToAssign = null;

                if (nrs != null)
                {
                    // Try to reserve candidateBase; if that fails, let reservation system find & reserve a nearby node.
                    if (nrs.ReserveNode(candidateBase, unit))
                    {
                        nodeToAssign = candidateBase;
                    }
                    else
                    {
                        nodeToAssign = nrs.FindAndReserveBestNode(candidateBase, unit, 2);
                    }
                }
                else
                {
                    // No reservation system — accept the base node.
                    nodeToAssign = candidateBase;
                }

                if (nodeToAssign != null)
                {
                    unit.lastAssignedNode = nodeToAssign;
                    unit.formationId = formationId;
                    unit.formationSlot = idx;
                    assignedUnits.Add(unit);
                    float dist = Vector2.Distance(unit.transform.position, nodeToAssign.centerPosition);
                    if (dist >= 0.15f) unit.SetDestinationToNode(nodeToAssign, true);
                    unassignedNodes.RemoveAt(idx);
                    unassignedUnits.RemoveAt(i);
                }
            }
        }

        // greedy nearest for remaining
        for (int i = unassignedUnits.Count - 1; i >= 0; i--)
        {
            var unit = unassignedUnits[i];
            if (unit == null) continue;

            float bestDist = float.MaxValue;
            int bestNodeIdx = -1;
            for (int j = 0; j < unassignedNodes.Count; j++)
            {
                var node = unassignedNodes[j];
                float d = Vector2.Distance(unit.transform.position, node.centerPosition);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestNodeIdx = j;
                }
            }

            if (bestNodeIdx >= 0)
            {
                var candidateBase = unassignedNodes[bestNodeIdx];
                Node nodeToAssign = null;
                if (nrs != null)
                {
                    if (nrs.ReserveNode(candidateBase, unit))
                    {
                        nodeToAssign = candidateBase;
                    }
                    else
                    {
                        nodeToAssign = nrs.FindAndReserveBestNode(candidateBase, unit, 2);
                    }
                }
                else
                {
                    nodeToAssign = candidateBase;
                }

                if (nodeToAssign != null)
                {
                    unit.lastAssignedNode = nodeToAssign;
                    unit.formationId = formationId;
                    unit.formationSlot = bestNodeIdx;
                    assignedUnits.Add(unit);
                    float dist = Vector2.Distance(unit.transform.position, nodeToAssign.centerPosition);
                    if (dist >= 0.15f) unit.SetDestinationToNode(nodeToAssign, true);
                    unassignedNodes.RemoveAt(bestNodeIdx);
                    unassignedUnits.RemoveAt(i);
                }
            }
        }

        // leaders, final fill, fallback
        for (int ui = unassignedUnits.Count - 1; ui >= 0; ui--)
        {
            var unit = unassignedUnits[ui];
            if (unit == null) continue;
            bool isLeader = unit.category == UnitCategory.Leader || unit is LeaderUnit;
            if (!isLeader) continue;

            bool assigned = false;
            for (int ni = 0; ni < unassignedNodes.Count; ni++)
            {
                var candidateBase = unassignedNodes[ni];
                if (candidateBase == null) continue;

                Node nodeToAssign = null;
                if (nrs != null)
                {
                    nodeToAssign = nrs.FindAndReserveBestNode(candidateBase, unit, 1);
                }
                else
                {
                    nodeToAssign = candidateBase;
                }

                if (nodeToAssign == null) continue;

                unit.lastAssignedNode = nodeToAssign;
                unit.formationId = formationId;
                unit.formationSlot = ni;
                assignedUnits.Add(unit);
                float dist = Vector2.Distance(unit.transform.position, nodeToAssign.centerPosition);
                if (dist >= 0.15f) unit.SetDestinationToNode(nodeToAssign, true);
                unassignedNodes.RemoveAt(ni);
                unassignedUnits.RemoveAt(ui);
                assigned = true;
                break;
            }

            if (!assigned)
            {
                Node fallback = null;
                if (nrs != null)
                    fallback = nrs.FindAndReserveBestNode(centerNode, unit, 2);
                else
                    fallback = grid.FindClosestWalkableNode(centerNode.gridX, centerNode.gridY);

                if (fallback != null)
                {
                    unit.lastAssignedNode = fallback;
                    unit.formationId = formationId;
                    unit.formationSlot = -1;
                    assignedUnits.Add(unit);
                    float dist = Vector2.Distance(unit.transform.position, fallback.centerPosition);
                    if (dist >= 0.15f) unit.SetDestinationToNode(fallback, true);
                    unassignedUnits.RemoveAt(ui);
                }
            }
        }

        int unitIndex = 0;
        while (unassignedNodes.Count > 0 && unassignedUnits.Count > 0 && unitIndex < unassignedUnits.Count)
        {
            var unit = unassignedUnits[unitIndex];
            if (unit == null) { unassignedUnits.RemoveAt(unitIndex); continue; }

            int bestNodeIdx = -1;
            float bestDist = float.MaxValue;
            for (int ni = 0; ni < unassignedNodes.Count; ni++)
            {
                var node = unassignedNodes[ni];
                float d = Vector2.Distance(unit.transform.position, node.centerPosition);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestNodeIdx = ni;
                }
            }

            if (bestNodeIdx < 0) { unitIndex++; continue; }

            var targetNodeCandidate = unassignedNodes[bestNodeIdx];
            Node finalNode = null;

            if (nrs != null)
            {
                finalNode = nrs.FindAndReserveBestNode(targetNodeCandidate, unit, 1);
            }
            else
            {
                finalNode = targetNodeCandidate;
            }

            if (finalNode == null)
            {
                unassignedNodes.RemoveAt(bestNodeIdx);
                continue;
            }

            unit.lastAssignedNode = finalNode;
            unit.formationId = formationId;
            unit.formationSlot = bestNodeIdx;
            assignedUnits.Add(unit);
            float distToNode = Vector2.Distance(unit.transform.position, finalNode.centerPosition);
            if (distToNode >= 0.15f) unit.SetDestinationToNode(finalNode, true);

            unassignedUnits.RemoveAt(unitIndex);
            unassignedNodes.RemoveAt(bestNodeIdx);
        }

        foreach (var leftover in unassignedUnits)
        {
            if (leftover == null) continue;
            Node fallback = null;
            if (nrs != null)
            {
                fallback = nrs.FindAndReserveBestNode(centerNode, leftover, 2);
            }
            else
            {
                fallback = grid.FindClosestWalkableNode(centerNode.gridX, centerNode.gridY);
            }

            if (fallback != null)
            {
                leftover.lastAssignedNode = fallback;
                leftover.formationId = formationId;
                leftover.formationSlot = -1;
                assignedUnits.Add(leftover);
                leftover.SetDestinationToNode(fallback, true);
                continue;
            }

            leftover.formationId = formationId;
            leftover.formationSlot = -1;
            assignedUnits.Add(leftover);
            leftover.SetDestinationToNode(centerNode, true);
        }

        // start monitor (tick-driven) instead of coroutine
        var monitor = new FormationMonitor { id = formationId, units = assignedUnits, stableTimer = 0f, elapsed = 0f };
        monitors[formationId] = monitor;
    }

    private List<Vector2Int> OrderOffsetsByDirectionAndRows(List<Vector2Int> offsets, Vector2 moveDir, int slotsCount)
    {
        if (offsets == null || offsets.Count == 0) return offsets;

        Vector2 dir = moveDir.normalized;
        if (dir.sqrMagnitude < 0.0001f) dir = lastMoveDirection.normalized;

        Vector2 perp = new Vector2(-dir.y, dir.x);
        var rows = new Dictionary<int, List<(Vector2Int offset, float forward, float lateral)>>();

        foreach (var off in offsets)
        {
            Vector2 v = new Vector2(off.x, off.y);
            float forward = Vector2.Dot(v, dir);
            float lateral = Vector2.Dot(v, perp);
            int rowKey = Mathf.RoundToInt(forward);

            if (!rows.TryGetValue(rowKey, out var list))
            {
                list = new List<(Vector2Int offset, float forward, float lateral)>();
                rows[rowKey] = list;
            }

            list.Add((offset: off, forward: forward, lateral: lateral));
        }

        var rowKeys = new List<int>(rows.Keys);
        rowKeys.Sort((a, b) => b.CompareTo(a));
        var ordered = new List<Vector2Int>(offsets.Count);

        foreach (int rk in rowKeys)
        {
            var list = rows[rk];
            list.Sort((a, b) =>
            {
                int c = Mathf.Abs(a.lateral).CompareTo(Mathf.Abs(b.lateral));
                if (c != 0) return c;
                c = a.lateral.CompareTo(b.lateral);
                if (c != 0) return c;
                float ma = Mathf.Abs(a.offset.x) + Mathf.Abs(a.offset.y);
                float mb = Mathf.Abs(b.offset.x) + Mathf.Abs(b.offset.y);
                return ma.CompareTo(mb);
            });

            foreach (var t in list)
            {
                ordered.Add(t.offset);
                if (ordered.Count >= slotsCount) return ordered;
            }
        }

        if (ordered.Count < slotsCount)
        {
            var remaining = new List<(Vector2Int offset, float forward, float lateral)>();
            foreach (var off in offsets)
            {
                Vector2 v = new Vector2(off.x, off.y);
                float forward = Vector2.Dot(v, dir);
                float lateral = Vector2.Dot(v, perp);
                remaining.Add((offset: off, forward: forward, lateral: lateral));
            }
            remaining.Sort((a, b) =>
            {
                int c = b.forward.CompareTo(a.forward);
                if (c != 0) return c;
                c = Mathf.Abs(a.lateral).CompareTo(Mathf.Abs(b.lateral));
                if (c != 0) return c;
                float ma = Mathf.Abs(a.offset.x) + Mathf.Abs(a.offset.y);
                float mb = Mathf.Abs(b.offset.x) + Mathf.Abs(b.offset.y);
                return ma.CompareTo(mb);
            });

            foreach (var t in remaining)
            {
                if (!ordered.Contains(t.offset)) ordered.Add(t.offset);
                if (ordered.Count >= slotsCount) break;
            }
        }

        return ordered;
    }

    private Vector3 GetGroupCenter(List<UnitAgent> units)
    {
        Vector3 sum = Vector3.zero;
        foreach (var u in units) sum += u.transform.position;
        return sum / units.Count;
    }

    private List<Vector2Int> GenerateGridOffsets(int count)
    {
        int w = formationWidth;
        switch (formationType)
        {
            case FormationType.Line: return FormationPatternsGrid.Line(count, w);
            case FormationType.Box: return FormationPatternsGrid.Box(count);
            case FormationType.Wedge: return FormationPatternsGrid.Wedge(count);
        }
        return FormationPatternsGrid.Line(count, w);
    }

    private Vector2Int RotateGridOffset(Vector2Int offset, float angle)
    {
        float rad = angle * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);
        float rx = offset.x * cos - offset.y * sin;
        float ry = offset.x * sin + offset.y * cos;
        return new Vector2Int(Mathf.RoundToInt(rx), Mathf.RoundToInt(ry));
    }

    public void ReformSelection()
    {
        var selection = SelectionController.Instance;
        if (selection == null) return;
        var units = selection.selectedUnits;
        if (units == null || units.Count == 0) return;
        MoveGroupWithFormation(units, lastFormationTarget);
    }

    public void SetFormation(FormationType type)
    {
        formationType = type;
        ReformSelection();
    }
}