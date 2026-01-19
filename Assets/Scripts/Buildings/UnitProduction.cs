using System.Collections.Generic;
using UnityEngine;

public class UnitProduction : MonoBehaviour
{
    public UnitData[] producibleUnits;

    // Spawns up to `amount` units of `unitData` at this building position (instant).
    // Attempts to place units on nearest walkable grid nodes outside the building footprint.
    // Uses building's owner (Building.owner) PlayerEconomy to pay costs. Returns number spawned.
    public int SpawnUnitsImmediate(UnitData unitData, int amount)
    {
        if (unitData == null || amount <= 0) return 0;

        Building building = GetComponent<Building>();
        BuildingConstruction bc = GetComponent<BuildingConstruction>();

        // Resolve owner (prefer building.owner, fallback to GameSystems or any PlayerEconomy)
        PlayerEconomy owner = building != null ? building.owner : null;
        if (owner == null)
        {
            GameObject gs = GameObject.Find("GameSystems");
            if (gs != null) owner = gs.GetComponent<PlayerEconomy>();
            if (owner == null) owner = Object.FindObjectOfType<PlayerEconomy>();
        }

        // Enforce population cap: limit requested amount to available population slots
        if (owner != null)
        {
            int available = Mathf.Max(0, owner.populationCap - owner.population);
            if (available <= 0)
            {
                return 0;
            }
            amount = Mathf.Min(amount, available);
        }

        GridManager gm = GridManager.Instance;
        Node centerNode = gm != null ? gm.NodeFromWorldPoint(transform.position) : null;
        NodeReservationSystem reservation = NodeReservationSystem.Instance;

        // local set to avoid assigning same node twice within this spawn call
        HashSet<Node> taken = new HashSet<Node>();

        int spawned = 0;

        for (int i = 0; i < amount; i++)
        {
            // Deduct cost (include popDelta = 1 so TrySpend enforces population cap)
            int woodCost = unitData.woodCost;
            int ironCost = unitData.stoneCost; // legacy field used as iron
            int goldCost = unitData.goldCost;
            int foodCost = unitData.foodCost;

            bool paid = owner != null ? owner.TrySpend(woodCost, ironCost, goldCost, foodCost, 1) : false;
            if (!paid)
            {
                // not enough resources or pop cap hit → stop spawning
                break;
            }

            // Find spawn node (prefer nearest walkable node outside building footprint and not already taken/reserved)
            Node spawnNode = null;

            if (gm != null && centerNode != null)
            {
                int searchRadius = 8; // widen search to improve chance of finding free node
                int startX = int.MinValue, startY = int.MinValue, endXExclusive = int.MinValue, endYExclusive = int.MinValue;
                if (bc != null)
                {
                    int halfX = bc.size.x / 2;
                    int halfY = bc.size.y / 2;
                    startX = centerNode.gridX - halfX;
                    startY = centerNode.gridY - halfY;
                    endXExclusive = startX + bc.size.x;
                    endYExclusive = startY + bc.size.y;
                }

                float bestSqr = float.MaxValue;
                for (int dx = -searchRadius; dx <= searchRadius; dx++)
                {
                    for (int dy = -searchRadius; dy <= searchRadius; dy++)
                    {
                        int nx = centerNode.gridX + dx;
                        int ny = centerNode.gridY + dy;
                        Node n = gm.GetNode(nx, ny);
                        if (n == null) continue;
                        if (!n.walkable) continue;

                        // skip nodes inside footprint
                        if (bc != null)
                        {
                            if (!(n.gridX < startX || n.gridX >= endXExclusive || n.gridY < startY || n.gridY >= endYExclusive))
                                continue;
                        }

                        // skip already taken by this spawn call or reserved externally
                        if (taken.Contains(n)) continue;
                        if (reservation != null && reservation.IsReserved(n)) continue;

                        // avoid nodes that are currently occupied by unit colliders (physical overlap)
                        Collider2D[] hits = Physics2D.OverlapCircleAll(n.centerPosition, 0.3f);
                        bool occupied = false;
                        foreach (var h in hits)
                        {
                            if (h == null) continue;
                            if (h.GetComponent<UnitAgent>() != null)
                            {
                                occupied = true;
                                break;
                            }
                        }
                        if (occupied) continue;

                        float d = (n.centerPosition - transform.position).sqrMagnitude;
                        if (d < bestSqr)
                        {
                            bestSqr = d;
                            spawnNode = n;
                        }
                    }
                }

                // Last resort: try GridManager helper for any closest walkable, still ensure not reserved/taken and outside footprint
                if (spawnNode == null)
                {
                    Node fallback = gm.GetClosestWalkableNode(centerNode);
                    if (fallback != null)
                    {
                        bool insideFootprint = false;
                        if (bc != null)
                        {
                            int halfX = bc.size.x / 2;
                            int halfY = bc.size.y / 2;
                            int sX = centerNode.gridX - halfX;
                            int sY = centerNode.gridY - halfY;
                            int eX = sX + bc.size.x;
                            int eY = sY + bc.size.y;
                            if (!(fallback.gridX < sX || fallback.gridX >= eX || fallback.gridY < sY || fallback.gridY >= eY))
                                insideFootprint = true;
                        }

                        if (!insideFootprint && (reservation == null || !reservation.IsReserved(fallback)) && !taken.Contains(fallback))
                        {
                            // also check physical overlap
                            Collider2D[] hits = Physics2D.OverlapCircleAll(fallback.centerPosition, 0.3f);
                            bool occupied = false;
                            foreach (var h in hits)
                            {
                                if (h == null) continue;
                                if (h.GetComponent<UnitAgent>() != null)
                                {
                                    occupied = true;
                                    break;
                                }
                            }
                            if (!occupied)
                                spawnNode = fallback;
                        }
                    }
                }
            }

            if (spawnNode == null)
            {
                // Couldn't find a node — refund costs and population, then stop
                if (owner != null)
                {
                    owner.AddResource(ResourceType.Wood, woodCost);
                    owner.AddResource(ResourceType.Iron, ironCost);
                    owner.AddResource(ResourceType.Gold, goldCost);
                    owner.AddResource(ResourceType.Food, foodCost);

                    // revert population increment from TrySpend
                    owner.population = Mathf.Max(0, owner.population - 1);
                }
                break;
            }

            // Mark as taken locally to prevent duplicates during this batch
            taken.Add(spawnNode);

            // Instantiate at node center with small random jitter to avoid exact overlap
            if (unitData.prefab != null)
            {
                Vector2 jitter = Random.insideUnitCircle * 0.12f;
                Vector3 spawnPos = spawnNode.centerPosition + new Vector3(jitter.x, jitter.y, 0f);
                GameObject go = Instantiate(unitData.prefab, spawnPos, Quaternion.identity);

                // If spawned unit has UnitAgent, set its reservation and lastAssignedNode to prevent others taking same node
                UnitAgent ua = go.GetComponent<UnitAgent>();
                if (ua != null)
                {
                    ua.lastAssignedNode = spawnNode;

                    // set ownership so AI can distinguish units later
                    if (owner != null)
                        ua.owner = owner;

                    // Reserve the node in the global reservation system for this unit (best-effort).
                    if (reservation != null)
                    {
                        bool reserved = false;
                        try
                        {
                            reserved = reservation.ReserveNode(spawnNode, ua);
                        }
                        catch { reserved = false; }

                        if (!reserved)
                        {
                            // reservation failed — refund and destroy spawned unit, then stop spawning further units
                            if (owner != null)
                            {
                                owner.AddResource(ResourceType.Wood, woodCost);
                                owner.AddResource(ResourceType.Iron, ironCost);
                                owner.AddResource(ResourceType.Gold, goldCost);
                                owner.AddResource(ResourceType.Food, foodCost);
                                owner.population = Mathf.Max(0, owner.population - 1);
                            }

                            Destroy(go);
                            break;
                        }
                    }
                }
            }

            spawned++;
        }

        return spawned;
    }
}