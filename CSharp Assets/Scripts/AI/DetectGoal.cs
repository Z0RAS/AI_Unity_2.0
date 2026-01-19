using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Scanning/detection goal used by EnemyKingController. Detects player leader, player base and resource nodes
/// within scanning cells and registers discoveries with the owning EnemyKingController.
/// Note: UI/dialogue announcements are managed centrally by EnemyKingController.RegisterDiscoveredResource / RegisterDiscoveredLeader etc.
/// to avoid spamming repeated announcements.
/// </summary>
public class DetectGoal : IGoal
{
    public string Name { get; private set; } = "Detect";

    private readonly EnemyKingController owner;
    private readonly float radius;
    private readonly float interval;
    private readonly float cellCooldown;
    private float lastRun;
    private bool aborted;

    public DetectGoal(EnemyKingController owner, float radius = 6f, float interval = 0.6f)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.radius = radius;
        this.interval = Math.Max(0.01f, interval);
    }

    public void Start()
    {
        lastRun = Time.time - interval;
    }

    public void Tick()
    {
        if (aborted) return;
        if (Time.time - lastRun < interval) return;
        lastRun = Time.time;

        if (owner == null || owner.CastleInstance == null) return;

        var econ = owner.EnemyEconomy;
        var origin = owner.CastleInstance.transform.position;

        // simple overlap scan around castle (cheap)
        Collider2D[] hits = Physics2D.OverlapCircleAll(origin, radius);
        if (hits == null || hits.Length == 0) return;

        foreach (var h in hits)
        {
            if (h == null) continue;

            // detect player leader / hero unit
            var leader = h.GetComponent<LeaderUnit>();
            if (leader != null && leader.gameObject != null && leader.gameObject.GetComponent<UnitAgent>()?.owner != econ)
            {
                // register discovery with owner; owner will manage announcement (once) and behavior transition
                owner.RegisterDiscoveredLeader(leader.gameObject);
                owner.InstructAttackOn(leader.gameObject);
            }

            // detect player's base/building
            var building = h.GetComponent<Building>();
            if (building != null && building.data != null && building.owner != econ && owner.DiscoveredPlayerBase == null)
            {
                var bname = (building.data.buildingName ?? "").ToLowerInvariant();
                if (bname.Contains("base") || bname.Contains("castle") || bname.Contains("town"))
                {
                    // register discovery only; owner will announce once and take action
                    owner.RegisterDiscoveredPlayerBase(building.gameObject);
                    if (owner.DiscoveredLeader != null) owner.InstructAttackOn(owner.DiscoveredLeader);
                    else owner.InstructAttackOn(building.gameObject);
                }
            }

            // detect resource nodes: register discovery only (announcements are controlled in EnemyKingController to avoid spam)
            var rn = h.GetComponent<ResourceNode>();
            if (rn != null && rn.amount > 0)
            {
                owner.RegisterDiscoveredResource(rn);
            }

            if (owner.DiscoveredLeader != null && owner.DiscoveredPlayerBase != null) break;
        }
    }

    public bool IsComplete => aborted;

    public void Abort()
    {
        aborted = true;
    }
}