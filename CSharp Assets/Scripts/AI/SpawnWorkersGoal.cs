using System;
using UnityEngine;

/// <summary>
/// Planner-managed spawn workers goal: periodically attempts to spawn workers until aborted.
/// Uses owner to access spawn helpers.
/// </summary>
public class SpawnWorkersGoal : IGoal
{
    public string Name { get; private set; } = "SpawnWorkers";

    private readonly EnemyKingController owner;
    private readonly float spawnInterval;
    private bool started;
    private bool aborted;
    private float lastSpawnTime;

    public SpawnWorkersGoal(EnemyKingController owner, float spawnInterval)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.spawnInterval = Math.Max(0.1f, spawnInterval);
    }

    public void Start()
    {
        started = true;
        lastSpawnTime = Time.time - spawnInterval;
    }

    public void Tick()
    {
        if (aborted) return;
        if (Time.time - lastSpawnTime < spawnInterval) return;
        lastSpawnTime = Time.time;

        var castle = owner.CastleInstance;
        var econ = owner.EnemyEconomy;
        if (castle == null || econ == null) return;
        if (econ.population >= econ.populationCap) return;

        int resourceNodes = ResourceNode.AllNodes?.Count ?? 0;
        int desiredPerNode = 1;
        int desiredWorkers = Mathf.Clamp(Mathf.Max(owner.InitialWorkers, resourceNodes * desiredPerNode), 1, econ.populationCap);

        if (owner.Workers.Count >= desiredWorkers) return;

        Node spawnNode = owner.FindSpawnNodeNearCastle();
        if (spawnNode == null) return;

        int woodCost = owner.WorkerUnitData != null ? owner.WorkerUnitData.woodCost : 0;
        int ironCost = owner.WorkerUnitData != null ? owner.WorkerUnitData.stoneCost : 0;
        int goldCost = owner.WorkerUnitData != null ? owner.WorkerUnitData.goldCost : 0;
        int foodCost = owner.WorkerUnitData != null ? owner.WorkerUnitData.foodCost : 0;

        if (econ.TrySpend(woodCost, ironCost, goldCost, foodCost, 1))
        {
            owner.SpawnWorkerAtNode(spawnNode, woodCost, ironCost, goldCost, foodCost);
        }
    }

    public bool IsComplete => aborted;

    public void Abort()
    {
        aborted = true;
    }
}