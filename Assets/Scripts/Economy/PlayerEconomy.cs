using System;
using UnityEngine;

public class PlayerEconomy : MonoBehaviour
{
    [Header("Ownership")]
    public bool isPlayer = true;

    [Header("Resources")]
    public int food;
    public int wood;
    public int gold;
    public int iron;
    public int population;
    public int populationCap;

    [Header("Starting (defaults applied when zero)")]
    [Tooltip("Default starting food assigned if food == 0 on Awake")]
    public int defaultStartFood = 300;
    [Tooltip("Default starting wood assigned if wood == 0 on Awake")]
    public int defaultStartWood = 300;
    [Tooltip("Default starting gold assigned if gold == 0 on Awake")]
    public int defaultStartGold = 75;
    [Tooltip("Default starting iron assigned if iron == 0 on Awake")]
    public int defaultStartIron = 50;
    [Tooltip("Default starting population cap assigned if populationCap == 0 on Awake")]
    public int defaultStartPopulationCap = 10;

    // Event fired when any resource or population values change.
    public event Action OnResourcesChanged;

    private void Awake()
    {
        // Ensure starting population is non-negative and does not exceed cap (if cap set).
        population = Mathf.Max(0, population);

        // If designer left fields at zero, apply balanced defaults.
        if (food <= 0) food = defaultStartFood;
        if (wood <= 0) wood = defaultStartWood;
        if (gold <= 0) gold = defaultStartGold;
        if (iron <= 0) iron = defaultStartIron;
        if (populationCap <= 0) populationCap = defaultStartPopulationCap;

        if (populationCap > 0)
            population = Mathf.Min(population, populationCap);

        // Notify listeners that resources are initialized
        OnResourcesChanged?.Invoke();
    }

    public void AddResource(ResourceType type, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        switch (type)
        {
            case ResourceType.Food: food += amount; break;
            case ResourceType.Wood: wood += amount; break;
            case ResourceType.Gold: gold += amount; break;
            case ResourceType.Iron: iron += amount; break;
        }

        OnResourcesChanged?.Invoke();
    }

    // Try to spend resources; returns true if successful and fires OnResourcesChanged.
    // popDelta allows increasing/decreasing population simultaneously with the spend.
    public bool TrySpend(int woodCost = 0, int ironCost = 0, int goldCost = 0, int foodCost = 0, int popDelta = 0)
    {
        if (wood < woodCost || iron < ironCost || gold < goldCost || food < foodCost || (popDelta > 0 && population + popDelta > populationCap))
            return false;

        wood -= woodCost;
        iron -= ironCost;
        gold -= goldCost;
        food -= foodCost;
        population += popDelta;

        OnResourcesChanged?.Invoke();
        return true;
    }

    // Increase population cap and notify UI
    public void AddPopulationCap(int amount)
    {
        populationCap += amount;
        OnResourcesChanged?.Invoke();
    }
}