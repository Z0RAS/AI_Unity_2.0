using System;
using UnityEngine;

/// <summary>
/// Ensure workers gather resources. Calls the controller's EnsureWorkersGather and completes immediately.
/// </summary>
public class GatherGoal : IGoal
{
    public string Name { get; private set; }
    private readonly EnemyKingController owner;
    private bool started;

    public GatherGoal(EnemyKingController owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Name = "GatherResources";
    }

    public void Start()
    {
        started = true;
        // Villagers automatically find and gather resources when idle via VillagerTaskSystem.TryFindWork()
    }

    public void Tick() { }

    public bool IsComplete => started; // immediate
    public void Abort() { }
}