using UnityEngine;

namespace Economy.Helpers
{
    /// <summary>
    /// Reservation helper utilities used by villager code.
    /// Kept in its own namespace/file so the logic is reusable and discoverable.
    /// </summary>
    public static class VillagerReservationHelper
    {
        // Attempt to reserve a target node or ask the reservation system to find & reserve a nearby node.
        // Returns the reserved Node or null.
        public static Node TryReserveOrFindAlt(UnitAgent agent, Node preferred, int nearbyRadius = 3)
        {
            var nrs = NodeReservationSystem.Instance;
            if (preferred == null || nrs == null || agent == null) return null;

            // If we already hold/track the reservation for this node, accept it.
            if (agent.reservedNode == preferred) return preferred;

            // Try to reserve the preferred node (succeeds if free or already reserved by this agent).
            try
            {
                if (nrs.ReserveNode(preferred, agent))
                    return preferred;
            }
            catch { }

            // Ask the reservation system to find and reserve a nearby suitable node.
            try
            {
                Node alt = nrs.FindAndReserveBestNode(preferred, agent, nearbyRadius);
                if (alt != null) return alt;
            }
            catch { }

            return null;
        }
    }
}