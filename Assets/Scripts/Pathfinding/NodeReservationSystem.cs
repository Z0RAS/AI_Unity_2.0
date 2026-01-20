using System.Collections.Generic;
using UnityEngine;

public class NodeReservationSystem : MonoBehaviour
{
    public static NodeReservationSystem Instance;

    private readonly Dictionary<Node, UnitAgent> reservations = new();
    private readonly Dictionary<UnitAgent, Node> reverse = new();
    public bool debugReservations = false;

    private void Awake()
    {
        Instance = this;
        CleanupStaleReservations();
    }

    // Cleanup stale reservations on startup
    private void CleanupStaleReservations()
    {
        var toRemove = new List<Node>();
        foreach (var kv in reservations)
        {
            if (kv.Value == null || kv.Value.gameObject == null || !kv.Value.gameObject.activeInHierarchy)
                toRemove.Add(kv.Key);
        }
        foreach (var n in toRemove)
            reservations.Remove(n);

        reverse.Clear();
        foreach (var kv in reservations)
        {
            if (kv.Value != null)
                reverse[kv.Value] = kv.Key;
        }
    }

    // Check if a node is reserved
    public bool IsReserved(Node node)
    {
        return node != null && reservations.ContainsKey(node);
    }

    // Reserve a node for a unit
    public bool ReserveNode(Node node, UnitAgent unit)
    {
        if (node == null || unit == null) return false;

        // If the node is already reserved by another unit, fail
        if (reservations.TryGetValue(node, out var owner) && owner != unit)
        {
            if (debugReservations) Debug.Log($"[NodeReservationSystem] Node ({node.gridX}, {node.gridY}) is already reserved by {owner.name}.");
            return false;
        }

        // Prevent reserving nodes that are physically occupied by another unit (or logically claimed).
        if (IsNodePhysicallyOccupiedByOther(node, unit))
        {
            if (debugReservations) Debug.Log($"[NodeReservationSystem] Node ({node.gridX}, {node.gridY}) is physically occupied; cannot reserve for {unit.name}.");
            return false;
        }

        // Release the unit's previous reservation, if any
        if (reverse.TryGetValue(unit, out var prev) && prev != node)
        {
            ReleaseReservation(unit);
        }

        // Assign the reservation
        reservations[node] = unit;
        reverse[unit] = node;
        unit.reservedNode = node;

        if (debugReservations) Debug.Log($"[NodeReservationSystem] Node ({node.gridX}, {node.gridY}) successfully reserved for {unit.name}.");
        return true;
    }

    // Release a reservation for a unit
    public void ReleaseReservation(UnitAgent unit)
    {
        if (unit == null) return;

        if (reverse.TryGetValue(unit, out var node))
        {
            reservations.Remove(node);
            reverse.Remove(unit);
            unit.reservedNode = null;

            if (debugReservations) Debug.Log($"[NodeReservationSystem] Released reservation for {unit.name} on node ({node.gridX}, {node.gridY}).");
        }
    }

    // Find and reserve the best node for a unit
    public Node FindAndReserveBestNode(Node center, UnitAgent unit, int searchRadius = 6, float pathBias = 0.12f)
    {
        if (center == null || unit == null || GridManager.Instance == null) return null;

        Node best = null;
        float bestScore = float.MaxValue;
        GridManager grid = GridManager.Instance;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                Node n = grid.GetNode(center.gridX + dx, center.gridY + dy);
                if (n == null || !n.walkable) continue;
                if (IsReserved(n)) continue;

                // Skip nodes physically occupied by other units (don't choose nodes where a unit already stands)
                if (IsNodePhysicallyOccupiedByOther(n, unit)) continue;

                float distanceToAgent = Vector2.Distance(unit.transform.position, n.centerPosition);
                float distanceToCenter = Vector2.Distance(center.centerPosition, n.centerPosition);
                float score = distanceToAgent + distanceToCenter * pathBias;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = n;
                }
            }
        }

        if (best != null && ReserveNode(best, unit))
        {
            if (debugReservations) Debug.Log($"[NodeReservationSystem] Reserved best node ({best.gridX}, {best.gridY}) for {unit.name}.");
            return best;
        }

        if (debugReservations) Debug.Log($"[NodeReservationSystem] Failed to reserve a node for {unit.name}.");
        return null;
    }

    // Helper: determine if a node is currently occupied by any other unit (logical claim / sim-position based)
    // requester may be null; if provided it's ignored when checking occupancy so units can keep their own reservations.
    private bool IsNodePhysicallyOccupiedByOther(Node node, UnitAgent requester)
    {
        if (node == null) return false;

        var gm = GridManager.Instance;
        if (gm == null)
            return false;

        // 1) Check existing reservations / lastAssigned markers
        foreach (var kv in reservations)
        {
            if (kv.Key == node)
            {
                var owner = kv.Value;
                if (owner != null && owner != requester) return true;
            }
        }

        // 2) Conservative check: consider units reserved/assigned to this node or whose logical center maps to this node.
        // Prefer registry iteration (cheap) instead of physics overlap calls.
        var all = UnitAgent.AllAgents;
        if (all != null)
        {
            for (int i = 0; i < all.Count; i++)
            {
                var ua = all[i];
                if (ua == null) continue;
                if (ua == requester) continue;

                // reservedNode or lastAssignedNode claiming this node -> occupied
                try
                {
                    if (ua.reservedNode != null && ua.reservedNode == node) return true;
                    if (ua.lastAssignedNode != null && ua.lastAssignedNode == node) return true;
                }
                catch { }

                // sim position mapped to this node
                try
                {
                    var unitNode = gm.NodeFromWorldPoint(ua.SimPosition);
                    if (unitNode != null)
                    {
                        if (unitNode.gridX == node.gridX && unitNode.gridY == node.gridY) return true;
                    }
                }
                catch { }
            }
        }
        else
        {
            // Fallback to FindObjects if registry isn't populated (rare)
            UnitAgent[] allFound = UnityEngine.Object.FindObjectsOfType<UnitAgent>();
            for (int i = 0; i < allFound.Length; i++)
            {
                var ua = allFound[i];
                if (ua == null) continue;
                if (ua == requester) continue;

                try
                {
                    if (ua.reservedNode != null && ua.reservedNode == node) return true;
                    if (ua.lastAssignedNode != null && ua.lastAssignedNode == node) return true;
                }
                catch { }

                try
                {
                    var unitNode = gm.NodeFromWorldPoint(ua.SimPosition);
                    if (unitNode != null)
                    {
                        if (unitNode.gridX == node.gridX && unitNode.gridY == node.gridY) return true;
                    }
                }
                catch { }
            }
        }

        return false;
    }
}