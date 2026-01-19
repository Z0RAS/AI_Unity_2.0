using System;
using System.Linq;
using UnityEngine;

/// <summary>
/// Planner-managed detection goal. Discovers leader, bases and resources and notifies owner via public registration APIs.
/// </summary>
public class DetectGoal : IGoal
{
    public string Name { get; private set; } = "Detect";

    private readonly EnemyKingController owner;
    private readonly float detectRadius;
    private readonly float detectInterval;
    private float lastDetect;
    private bool started;
    private bool aborted;

    public DetectGoal(EnemyKingController owner, float detectRadius, float detectInterval)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.detectRadius = detectRadius;
        this.detectInterval = detectInterval;
    }

    public void Start()
    {
        started = true;
        lastDetect = Time.time - detectInterval;
    }

    public void Tick()
    {
        if (aborted) return;
        if (Time.time - lastDetect < detectInterval) return;
        lastDetect = Time.time;

        var econ = owner.EnemyEconomy;
        if (econ == null) return;

        var myUnits = UnityEngine.Object.FindObjectsOfType<UnitAgent>().Where(u => u != null && u.owner == econ).ToArray();
        foreach (var u in myUnits)
        {
            if (u == null) continue;
            Collider2D[] hits = Physics2D.OverlapCircleAll(u.transform.position, detectRadius);
            if (hits == null || hits.Length == 0) continue;

            foreach (var h in hits)
            {
                if (h == null) continue;

                var leader = h.GetComponent<LeaderUnit>();
                if (leader != null && leader.owner != econ && owner.DiscoveredLeader == null)
                {
                    owner.RegisterDiscoveredLeader(leader.gameObject);
                    EnemyDialogueController.Instance?.Speak($"Leader spotted: {leader.gameObject.name}", null, 2.2f);
                    AttackTarget(leader.gameObject);
                }

                var building = h.GetComponent<Building>();
                if (building != null && building.data != null && building.owner != econ && owner.DiscoveredPlayerBase == null)
                {
                    var bname = (building.data.buildingName ?? "").ToLowerInvariant();
                    if (bname.Contains("base") || bname.Contains("castle") || bname.Contains("town"))
                    {
                        owner.RegisterDiscoveredPlayerBase(building.gameObject);
                        EnemyDialogueController.Instance?.Speak($"Base located: {building.data.buildingName}", null, 2.2f);
                        if (owner.DiscoveredLeader != null) AttackTarget(owner.DiscoveredLeader);
                        else AttackTarget(building.gameObject);
                    }
                }

                var rn = h.GetComponent<ResourceNode>();
                if (rn != null && rn.amount > 0)
                {
                    // Register discovery with EnemyIntel (which handles one-time localized announcements).
                    owner.RegisterDiscoveredResource(rn);
                }

                if (owner.DiscoveredLeader != null && owner.DiscoveredPlayerBase != null) break;
            }

            if (owner.DiscoveredLeader != null && owner.DiscoveredPlayerBase != null) break;
        }
    }

    public bool IsComplete => aborted;

    public void Abort()
    {
        aborted = true;
    }

    private void AttackTarget(GameObject target)
    {
        if (target == null) return;
        
        // Find all enemy military units (units with UnitCombat)
        var allUnits = UnityEngine.Object.FindObjectsOfType<UnitAgent>();
        var militaryUnits = allUnits.Where(u => u != null && u.owner == owner.EnemyEconomy && u.GetComponent<UnitCombat>() != null);
        
        // Command all military units to attack the target
        foreach (var unit in militaryUnits)
        {
            var combat = unit.GetComponent<UnitCombat>();
            if (combat != null)
            {
                combat.SetTarget(target);
            }
        }
    }
}