using System.Collections.Generic;
using UnityEngine;

namespace Economy.Helpers
{
    public static class VillagerSearchHelper
    {
        public static void TryFindWork(VillagerTaskSystem ctx, float baseSearchRadius)
        {
            if (ctx == null) return;
            // Only search when villager is idle
            if (!ctx.IsIdle()) return;

            // require a completed dropoff/base to deposit into
            var drop = ctx.FindCompletedDropoff();
            if (drop == null) return;

            var nodes = ResourceNode.AllNodes;
            if (nodes == null || nodes.Count == 0) return;

            var gm = GridManager.Instance;
            Vector3 myPos = ctx.Position;

            ResourceNode bestNode = null;
            float bestSqr = float.MaxValue;
            foreach (var rn in nodes)
            {
                if (rn == null) continue;
                float dsq = (rn.transform.position - myPos).sqrMagnitude;
                if (dsq < bestSqr)
                {
                    bestSqr = dsq;
                    bestNode = rn;
                }
            }

            if (bestNode == null) return;

            Vector3 chosenSpot = bestNode.transform.position;
            if (gm != null)
            {
                float bestSpotSqr = float.MaxValue;
                foreach (var spot in bestNode.GetHarvestSpots())
                {
                    Node n = gm.NodeFromWorldPoint(spot);
                    if (n == null || !n.walkable)
                    {
                        n = gm.FindClosestWalkableNode(Mathf.RoundToInt(spot.x), Mathf.RoundToInt(spot.y));
                        if (n == null) continue;
                    }

                    float dsq = (n.centerPosition - myPos).sqrMagnitude;
                    if (dsq < bestSpotSqr)
                    {
                        bestSpotSqr = dsq;
                        chosenSpot = n.centerPosition;
                    }
                }
            }

            try { ctx.CommandGather(bestNode, chosenSpot); } catch { }
        }
    }
}