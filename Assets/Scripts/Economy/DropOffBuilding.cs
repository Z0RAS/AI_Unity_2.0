using System;
using UnityEngine;

public class DropOffBuilding : MonoBehaviour
{
    public PlayerEconomy owner;

    // If true, only buildings whose Building.data.buildingName contains "base"
    // will act as a valid drop-off for villagers. Set false to allow any DropOffBuilding.
    // Default false to avoid unintentionally blocking dropoffs (was previously true).
    public bool onlyAcceptBaseAsDropoff = false;

    void Start()
    {
        // If owner wasn't assigned in inspector, try to resolve it automatically.
        // For finished buildings, assign owner; under construction will be set by builder.
        if (owner == null)
        {
            var bc = GetComponent<BuildingConstruction>();
            if (bc != null && !bc.isFinished)
            {
                // Defer owner assignment until construction finalizes.
                return;
            }

            // First try the canonical GameSystems object (player economy)
            var gs = GameObject.Find("GameSystems");
            if (gs != null)
                owner = gs.GetComponent<PlayerEconomy>();

            // If still no owner, look for PlayerEconomy that is the player economy
            if (owner == null)
            {
                var economies = FindObjectsOfType<PlayerEconomy>();
                foreach (var econ in economies)
                {
                    if (econ.isPlayer)
                    {
                        owner = econ;
                        break;
                    }
                }
            }
        }
    }


    /// <summary>
    /// Returns the best walkable node to use as a drop-off for a villager at <paramref name="fromPosition"/>.
    /// Preference order:
    /// 1) Walkable nodes adjacent to any unwalkable node (i.e. next to building footprint), closest to villager.
    /// 2) Closest walkable node by world distance to villager within searchRadius.
    /// 3) GridManager.GetClosestWalkableNode(center) as last resort.
    /// </summary>
    public Node GetNearestDropoffNode(Vector3 fromPosition, int searchRadius = 6)
    {
        // If configured to accept only bases, reject if this dropoff is not the base.
        if (onlyAcceptBaseAsDropoff)
        {
            var buildingComp = GetComponent<Building>();
            if (buildingComp == null)
            {
                // not a finished building -> cannot be primary dropoff
                return null;
            }

            if (buildingComp.data == null || string.IsNullOrEmpty(buildingComp.data.buildingName) ||
                !buildingComp.data.buildingName.ToLowerInvariant().Contains("base"))
            {
                // not a base-type building -> don't offer dropoff nodes
                return null;
            }
        }

        GridManager gm = GridManager.Instance;
        if (gm == null) return null;

        Node center = gm.NodeFromWorldPoint(transform.position);
        if (center == null) return null;

        Node bestAdjacent = null;
        float bestAdjSqr = float.MaxValue;

        // First pass: find walkable nodes that are adjacent to any unwalkable node (edge of building)
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                int nx = center.gridX + dx;
                int ny = center.gridY + dy;
                Node n = gm.GetNode(nx, ny);
                if (n == null) continue;
                if (!n.walkable) continue;

                // Check neighbours for unwalkable -> if any neighbour is unwalkable, this node is "edge"
                bool adjacentToUnwalkable = false;
                foreach (var nb in gm.GetNeighbours(n))
                {
                    if (nb == null) continue;
                    if (!nb.walkable)
                    {
                        adjacentToUnwalkable = true;
                        break;
                    }
                }

                if (!adjacentToUnwalkable) continue;

                float d = (n.centerPosition - fromPosition).sqrMagnitude;
                if (d < bestAdjSqr)
                {
                    bestAdjSqr = d;
                    bestAdjacent = n;
                }
            }
        }

        if (bestAdjacent != null)
            return bestAdjacent;

        // Second pass: fallback to the closest walkable node by distance to villager
        Node best = null;
        float bestSqr = float.MaxValue;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                int nx = center.gridX + dx;
                int ny = center.gridY + dy;
                Node n = gm.GetNode(nx, ny);
                if (n == null) continue;
                if (!n.walkable) continue;

                float d = (n.centerPosition - fromPosition).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = n;
                }
            }
        }

        if (best != null)
            return best;

        // Last resort: BFS / closest walkable node using GridManager helper
        return gm.GetClosestWalkableNode(center);
    }
}


