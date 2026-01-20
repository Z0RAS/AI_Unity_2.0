using System;
using System.Collections.Generic;
using UnityEngine;

namespace Economy.Helpers
{
    public static class VillagerSearchHelper
    {
        // Stagger / occupancy cache settings
        private const int DefaultSearchSpreadTicks = 6; // spread TryFindWork calls across ticks
        private static readonly HashSet<long> occupiedCoords = new HashSet<long>();
        private static bool subscribedToTicks = false;
        private static int tickCounter = 0;
        private static int searchSpreadTicks = DefaultSearchSpreadTicks;

        // Maximum search radius (tiles) to avoid unlimited searches when callers pass 0.
        // Tunable if needed.
        private const int MaxSearchRadiusTiles = 24;

        // Ensure we update occupancy and tickCounter when TimeController exists.
        private static void EnsureSubscribed()
        {
            if (subscribedToTicks) return;
            if (TimeController.Instance == null) return;

            try
            {
                TimeController.OnTick += StaticOnTick;
                subscribedToTicks = true;
            }
            catch { subscribedToTicks = false; }
        }

        // Rebuild occupancy cache each tick (cheap grid coord set).
        private static void StaticOnTick(float dt)
        {
            tickCounter++;
            occupiedCoords.Clear();

            var gm = GridManager.Instance;
            if (gm == null) return;

            // Collect sim positions once per tick using the UnitAgent registry
            var allUnits = UnitAgent.AllAgents;
            if (allUnits != null)
            {
                for (int i = 0; i < allUnits.Count; i++)
                {
                    var ua = allUnits[i];
                    if (ua == null) continue;

                    try
                    {
                        var upos = ua.SimPosition;
                        Node uNode = gm.NodeFromWorldPoint(upos);
                        if (uNode != null)
                        {
                            long key = PackGridCoords(uNode.gridX, uNode.gridY);
                            occupiedCoords.Add(key);
                        }
                    }
                    catch { }
                }
            }
            else
            {
                // fallback (should be rare)
                UnitAgent[] allFound = UnityEngine.Object.FindObjectsOfType<UnitAgent>();
                for (int i = 0; i < allFound.Length; i++)
                {
                    var ua = allFound[i];
                    if (ua == null) continue;

                    try
                    {
                        var upos = ua.SimPosition;
                        Node uNode = gm.NodeFromWorldPoint(upos);
                        if (uNode != null)
                        {
                            long key = PackGridCoords(uNode.gridX, uNode.gridY);
                            occupiedCoords.Add(key);
                        }
                    }
                    catch { }
                }
            }
        }

        // Public API: ctx is the villager; baseSearchRadius interpreted as tiles if GridManager exists.
        // This method is staggered across ticks and uses the per-tick occupancy cache.
        // 'force' bypasses stagger-slot gating and runs the search immediately.
        public static void TryFindWork(VillagerTaskSystem ctx, float baseSearchRadius, bool force = false)
        {
            if (ctx == null) return;

            // subscribe to ticks and maintain occupancy when possible
            EnsureSubscribed();

            var gm = GridManager.Instance;
            var nrs = NodeReservationSystem.Instance;
            Vector3 myPos = ctx.Position;

            // If we have a TimeController, stagger searches to reduce CPU spike:
            // NOTE: allow the very first few ticks (tickCounter == 0) to run without strict staggering
            // so villagers will start work quickly on startup. Once the occupancy cache starts updating
            // (tickCounter > 0) we enforce the spread unless 'force' is true.
            if (!force && TimeController.Instance != null && tickCounter > 0)
            {
                int slot = Math.Abs(ctx.GetInstanceID()) % Math.Max(1, searchSpreadTicks);
                if ((tickCounter % Math.Max(1, searchSpreadTicks)) != slot)
                    return; // not this villager's tick
            }

            // require a completed dropoff/base to deposit into
            var drop = ctx.FindCompletedDropoff();
            if (drop == null) return;

            var nodes = ResourceNode.AllNodes;
            if (nodes == null || nodes.Count == 0) return;

            // Determine world search radius (tiles -> world) and cap to avoid unlimited radius
            float worldSearchRadius;
            if (gm != null)
            {
                int tiles = (baseSearchRadius > 0f) ? Mathf.Max(1, Mathf.RoundToInt(baseSearchRadius)) : 12;
                tiles = Mathf.Min(tiles, MaxSearchRadiusTiles); // cap
                worldSearchRadius = tiles * gm.nodeDiameter;
            }
            else
            {
                // fallback: baseSearchRadius used directly but also capped
                float tilesOrWorld = (baseSearchRadius > 0f) ? baseSearchRadius : 12f;
                float cappedTiles = Mathf.Min(tilesOrWorld, MaxSearchRadiusTiles);
                worldSearchRadius = cappedTiles;
            }
            float worldSearchRadiusSqr = worldSearchRadius * worldSearchRadius;

            // We'll collect candidates and run multi-pass selection to avoid assigning villagers to occupied/reserved spots
            var candidates = new List<(ResourceNode resource, Vector3 spotWorld, Node walkNode, bool reserved, bool occupied, float dsq)>();

            // Cull resource nodes by distance first to avoid checking distant nodes/spots
            for (int i = 0; i < nodes.Count; i++)
            {
                var rn = nodes[i];
                if (rn == null) continue;
                if (rn.amount <= 0) continue;

                Vector3 rnCenter = rn.transform.position;
                float dsqToNode = (rnCenter - myPos).sqrMagnitude;
                if (dsqToNode > worldSearchRadiusSqr) continue; // skip distant nodes

                var spots = rn.GetHarvestSpots();
                if (spots == null || spots.Count == 0) continue;

                for (int si = 0; si < spots.Count; si++)
                {
                    var spot = spots[si];

                    Node node = null;
                    if (gm != null)
                    {
                        node = gm.NodeFromWorldPoint(spot);
                        if (node == null || !node.walkable)
                        {
                            node = gm.FindClosestWalkableNode(Mathf.RoundToInt(spot.x), Mathf.RoundToInt(spot.y));
                            if (node == null) continue;
                        }

                        // determine reservation and occupancy
                        bool reserved = (nrs != null && nrs.IsReserved(node));
                        long key = PackGridCoords(node.gridX, node.gridY);
                        bool occupied = occupiedCoords.Contains(key);

                        Vector3 candidatePos = node.centerPosition;
                        float dsq = (candidatePos - myPos).sqrMagnitude;

                        candidates.Add((rn, candidatePos, node, reserved, occupied, dsq));
                    }
                    else
                    {
                        // Fallback conservative check if no grid manager: small physics overlap but limited frequency due to staggering.
                        bool occupied = false;
                        try
                        {
                            float physRadius = 0.35f;
                            Collider2D[] hits = Physics2D.OverlapCircleAll(spot, physRadius);
                            for (int h = 0; h < hits.Length; h++)
                            {
                                var hh = hits[h];
                                if (hh == null) continue;
                                var ua = hh.GetComponent<UnitAgent>();
                                if (ua == null) continue;
                                if (ua == ctx.GetComponent<UnitAgent>()) continue;
                                occupied = true;
                                break;
                            }
                        }
                        catch { occupied = false; }

                        Vector3 candidatePos = spot;
                        float dsq = (candidatePos - myPos).sqrMagnitude;
                        // no reservation system available in this branch
                        candidates.Add((rn, candidatePos, null, false, occupied, dsq));
                    }
                }
            }

            if (candidates.Count == 0) return;

            // Helper to pick best candidate by dsq (lowest)
            (ResourceNode resource, Vector3 spotWorld, Node walkNode, bool reserved, bool occupied, float dsq) PickBest(List<(ResourceNode resource, Vector3 spotWorld, Node walkNode, bool reserved, bool occupied, float dsq)> list)
            {
                (ResourceNode resource, Vector3 spotWorld, Node walkNode, bool reserved, bool occupied, float dsq) best = (null, Vector3.zero, null, false, false, float.MaxValue);
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c.dsq < best.dsq)
                        best = c;
                }
                return best.resource != null ? best : (null, Vector3.zero, null, false, false, float.MaxValue);
            }

            // Pass 1: prefer not reserved AND not occupied
            var pass1 = new List<(ResourceNode resource, Vector3 spotWorld, Node walkNode, bool reserved, bool occupied, float dsq)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.reserved && !c.occupied) pass1.Add(c);
            }
            if (pass1.Count > 0)
            {
                var chosen = PickBest(pass1);
                try { ctx.CommandGather(chosen.resource, chosen.spotWorld); } catch { }
                return;
            }

            // Pass 2: prefer not reserved (allow occupied)
            var pass2 = new List<(ResourceNode resource, Vector3 spotWorld, Node walkNode, bool reserved, bool occupied, float dsq)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.reserved) pass2.Add(c);
            }
            if (pass2.Count > 0)
            {
                var chosen = PickBest(pass2);
                try { ctx.CommandGather(chosen.resource, chosen.spotWorld); } catch { }
                return;
            }

            // Pass 3: allow reserved (last resort)
            var pass3 = candidates;
            if (pass3.Count > 0)
            {
                var chosen = PickBest(pass3);
                try { ctx.CommandGather(chosen.resource, chosen.spotWorld); } catch { }
                return;
            }

            // If still nothing (shouldn't reach here) -> return
            return;
        }

        // Helpers
        private static long PackGridCoords(int gx, int gy) => ((long)gx << 32) ^ (uint)gy;
    }
}