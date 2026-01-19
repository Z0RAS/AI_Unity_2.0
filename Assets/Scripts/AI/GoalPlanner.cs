using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Goal planner that supports planner-managed goals. Goals start immediately when enqueued and run
/// concurrently. The planner ticks all active goals each Update and removes completed ones.
/// </summary>
[DisallowMultipleComponent]
public class GoalPlanner : MonoBehaviour
{
    private readonly Queue<IGoal> pending = new Queue<IGoal>();
    private readonly List<IGoal> active = new List<IGoal>();

    public IReadOnlyList<IGoal> ActiveGoals => active.AsReadOnly();

    public void EnqueueGoal(IGoal goal)
    {
        if (goal == null) return;
        if (HasGoalByName(goal.Name)) return;

        try
        {
            goal.Start();
            active.Add(goal);
        }
        catch
        {
            // swallow start exceptions to keep planner robust
        }
    }

    public bool HasGoalByName(string name)
    {
        foreach (var g in active)
            if (g?.Name == name) return true;
        foreach (var g in pending)
            if (g?.Name == name) return true;
        return false;
    }

    public void ClearAllGoals()
    {
        foreach (var g in active.ToArray())
        {
            try { g.Abort(); } catch { }
        }
        active.Clear();
        pending.Clear();
    }

    private void Update()
    {
        // Tick all active goals
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var g = active[i];
            if (g == null)
            {
                active.RemoveAt(i);
                continue;
            }

            try { g.Tick(); } catch { }

            if (g.IsComplete)
            {
                try { active.RemoveAt(i); } catch { active.RemoveAt(i); }
            }
        }
    }
}