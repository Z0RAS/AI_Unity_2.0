using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Planner-managed resource scout that periodically sends scouts to discover resource nodes.
/// This goal runs until aborted or until haveDiscoveredAllFunc returns true and the owner decides to stop.
/// </summary>
public class ResourceScoutGoal : IGoal
{
    public string Name { get; private set; } = "ResourceScout";

    private readonly EnemyKingController owner;
    private readonly int resourceScoutCount;
    private readonly float resourceScoutInterval;
    private readonly Func<bool> haveDiscoveredAllFunc;
    private float lastRunTime;
    private bool started;
    private bool aborted;

    public ResourceScoutGoal(EnemyKingController owner, int resourceScoutCount, float resourceScoutInterval, Func<bool> haveDiscoveredAllFunc)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.resourceScoutCount = Math.Max(0, resourceScoutCount);
        this.resourceScoutInterval = Math.Max(0.1f, resourceScoutInterval);
        this.haveDiscoveredAllFunc = haveDiscoveredAllFunc;
    }

    public void Start()
    {
        started = true;
        lastRunTime = Time.time - resourceScoutInterval;
    }

    public void Tick()
    {
        if (aborted) return;
        if (Time.time - lastRunTime < resourceScoutInterval) return;
        lastRunTime = Time.time;

        if (haveDiscoveredAllFunc != null && haveDiscoveredAllFunc())
        {
            // if discovered, wait a longer interval (handled implicitly by interval)
            return;
        }

        SendResourceScouts();
    }

    private void SendResourceScouts()
    {
        var castle = owner.CastleInstance;
        var econ = owner.EnemyEconomy;
        if (castle == null || econ == null) return;

        // If castle is still under construction, do not send villagers to scout/gather — allow builders to finish first.
        var castleBc = castle.GetComponent<BuildingConstruction>();
            if (castleBc != null && !castleBc.isFinished)
            {
                return;
            }

        const int VILLAGERS_PER_NODE = 2;   // tune in-code or promote to config if needed
        const int SOLDIERS_PER_NODE = 2;

        // prefer soldiers for scouting/discovery
        var soldierCandidates = UnityEngine.Object.FindObjectsOfType<UnitAgent>()
            .Where(u => u != null && u.owner == econ && u.category != UnitCategory.Villager)
            .OrderBy(u => (u.transform.position - castle.transform.position).sqrMagnitude)
            .ToList();

        var allNodes = ResourceNode.AllNodes;
        List<ResourceNode> pool = (allNodes != null && allNodes.Count > 0)
            ? allNodes.Where(n => n != null && n.amount > 0).ToList()
            : UnityEngine.Object.FindObjectsOfType<ResourceNode>().Where(n => n != null && n.amount > 0).ToList();

        if (pool.Count == 0) return;

        var undiscovered = pool; // owner/intel will manage global announcements
        var usedNodes = new HashSet<ResourceNode>();
        int assigned = 0;

        // Primary: send soldier scouts to check high-value nodes
        foreach (var soldier in soldierCandidates)
        {
            if (assigned >= resourceScoutCount) break;
            if (soldier == null) continue;
            ResourceNode pick = null;
            float best = float.MaxValue;
            foreach (var rn in undiscovered)
            {
                if (rn == null || usedNodes.Contains(rn)) continue;
                float d = (rn.transform.position - soldier.transform.position).sqrMagnitude;
                if (d < best) { best = d; pick = rn; }
            }
            if (pick == null) continue;
            usedNodes.Add(pick);
            assigned++;

            // move soldier to a nearby harvest spot (so it reveals & secures area)
            var spots = pick.GetHarvestSpots();
            Vector3 dest = (spots != null && spots.Count > 0) ? spots.OrderBy(s => (s - soldier.transform.position).sqrMagnitude).First() : pick.transform.position;
            Node destNode = GridManager.Instance != null ? GridManager.Instance.NodeFromWorldPoint(dest) : null;
            if (destNode != null) try { soldier.SetDestinationToNode(destNode); } catch { }
            else try { soldier.SetDestinationToNode(GridManager.Instance.NodeFromWorldPoint(pick.transform.position)); } catch { }
        }

        if (assigned >= resourceScoutCount) return;

        // Fallback: use idle villagers to move toward undiscovered nodes (so they can discover + later gather)
        var villCandidates = UnityEngine.Object.FindObjectsOfType<UnitAgent>()
            .Where(u => u != null && u.owner == econ && u.category == UnitCategory.Villager)
            .OrderBy(u => (u.transform.position - castle.transform.position).sqrMagnitude)
            .ToList();

        foreach (var vill in villCandidates)
        {
            if (assigned >= resourceScoutCount) break;
            if (vill == null) continue;
            var vt = vill.GetComponent<VillagerTaskSystem>();
            if (vt == null || !vt.IsIdle()) continue;

            ResourceNode pick = null;
            float best = float.MaxValue;
            foreach (var rn in undiscovered)
            {
                if (rn == null || usedNodes.Contains(rn)) continue;
                float d = (rn.transform.position - vill.transform.position).sqrMagnitude;
                if (d < best) { best = d; pick = rn; }
            }
            if (pick == null) continue;
            usedNodes.Add(pick);
            assigned++;

            // move villager toward best harvest spot; when they arrive VillagerTaskSystem will convert to Idle/Gather appropriately
            var spots = pick.GetHarvestSpots();
            Vector3 dest = (spots != null && spots.Count > 0) ? spots.OrderBy(s => (s - vill.transform.position).sqrMagnitude).First() : pick.transform.position;
            Node destNode = GridManager.Instance != null ? GridManager.Instance.NodeFromWorldPoint(dest) : null;

            if (vt != null && destNode != null)
            {
                try { vt.CommandMove(destNode.centerPosition); }
                catch { try { vill.SetDestinationToNode(destNode); } catch { } }
            }
            else if (destNode != null)
            {
                try { vill.SetDestinationToNode(destNode); } catch { }
            }

            // Immediately after sending the villager, attempt to assign additional nearby idle villagers to actually gather
            // (best-effort small team).
            var nearbyVillagers = UnityEngine.Object.FindObjectsOfType<UnitAgent>()
                .Where(u => u != null && u.owner == econ && u.category == UnitCategory.Villager)
                .OrderBy(u => (u.transform.position - pick.transform.position).sqrMagnitude)
                .ToList();

            int gatherAssigned = 0;
            foreach (var nv in nearbyVillagers)
            {
                if (gatherAssigned >= VILLAGERS_PER_NODE) break;
                if (nv == null) continue;
                var nvt = nv.GetComponent<VillagerTaskSystem>();
                if (nvt == null) continue;
                // respect committed builders; CommandGather will be ignored for committed builders
                Vector3 chosenSpot = pick.transform.position;
                if (spots != null && spots.Count > 0)
                    chosenSpot = spots.OrderBy(s => (s - nv.transform.position).sqrMagnitude).First();
                try { nvt.CommandGather(pick, chosenSpot); gatherAssigned++; }
                catch { /* ignore failures */ }
            }

            // If node is high-value (Gold/Iron), nudge nearby soldiers to hold patrol points around it
            if (pick.resourceType == ResourceType.Gold || pick.resourceType == ResourceType.Iron)
            {
                var nearbySoldiers = UnityEngine.Object.FindObjectsOfType<UnitAgent>()
                    .Where(u => u != null && u.owner == econ && u.category != UnitCategory.Villager)
                    .OrderBy(u => (u.transform.position - pick.transform.position).sqrMagnitude)
                    .Take(SOLDIERS_PER_NODE)
                    .ToList();

                if (nearbySoldiers.Count > 0)
                {
                    int si = 0;
                    foreach (var s in nearbySoldiers)
                    {
                        if (s == null) continue;
                        var spot = (spots != null && spots.Count > 0) ? spots[si % spots.Count] : pick.transform.position;
                        si++;
                        Node sNode = GridManager.Instance != null ? GridManager.Instance.NodeFromWorldPoint(spot) : null;
                        if (sNode != null)
                        {
                            try
                            {
                                // updated API usage: release reservation by unit
                                NodeReservationSystem.Instance?.ReleaseReservation(s);
                                s.ClearCombatReservation();
                                s.SetDestinationToNode(sNode);
                            }
                            catch { }
                        }
                    }
                }
            }
        }
    }

    public bool IsComplete => aborted;

    public void Abort()
    {
        aborted = true;
    }
}