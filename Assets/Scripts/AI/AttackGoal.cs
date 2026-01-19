using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Issue attack orders for owner's soldiers toward a nearest player target (one-shot).
/// </summary>
public class AttackGoal : IGoal
{
    public string Name { get; private set; }
    private readonly EnemyKingController owner;
    private bool started;

    public AttackGoal(EnemyKingController owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Name = "AttackTarget";
    }

    public void Start()
    {
        started = true;
        
        // Find all enemy military units (units with UnitCombat)
        var allUnits = UnityEngine.Object.FindObjectsOfType<UnitAgent>();
        var militaryUnits = new List<UnitAgent>();
        
        foreach (var unit in allUnits)
        {
            if (unit.owner == owner.EnemyEconomy && unit.GetComponent<UnitCombat>() != null)
            {
                militaryUnits.Add(unit);
            }
        }
        
        if (militaryUnits.Count == 0) return;
        
        // Find nearest player target
        GameObject target = FindNearestPlayerTarget();
        if (target == null) return;
        
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

    private GameObject FindNearestPlayerTarget()
    {
        // Find all player units and buildings
        var allUnits = UnityEngine.Object.FindObjectsOfType<UnitAgent>();
        var playerUnits = new List<UnitAgent>();
        
        foreach (var unit in allUnits)
        {
            if (unit.owner != owner.EnemyEconomy) // Not enemy units
            {
                playerUnits.Add(unit);
            }
        }
        
        // Also find player buildings
        var allBuildings = UnityEngine.Object.FindObjectsOfType<Building>();
        var playerBuildings = new List<Building>();
        
        foreach (var building in allBuildings)
        {
            if (building.owner != owner.EnemyEconomy)
            {
                playerBuildings.Add(building);
            }
        }
        
        // Find nearest target
        GameObject nearestTarget = null;
        float nearestDistance = float.MaxValue;
        
        // Check units
        foreach (var unit in playerUnits)
        {
            float distance = Vector3.Distance(owner.transform.position, unit.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestTarget = unit.gameObject;
            }
        }
        
        // Check buildings
        foreach (var building in playerBuildings)
        {
            float distance = Vector3.Distance(owner.transform.position, building.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestTarget = building.gameObject;
            }
        }
        
        return nearestTarget;
    }

    public void Tick() { }

    public bool IsComplete => started;
    public void Abort() { }
}