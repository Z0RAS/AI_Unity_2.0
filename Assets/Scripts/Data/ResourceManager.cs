using UnityEngine;

public class ResourceManager : MonoBehaviour
{
    public static ResourceManager Instance;

    // Standard tracked resources
    public int wood;
    public int iron;
    public int gold;
    public int food;
    public int population;
    public int populationCap;

    private void Awake()
    {
        Instance = this;
    }

    // TrySpend parameters: wood, iron, gold, food, population delta
    public bool TrySpend(int w = 0, int i = 0, int g = 0, int f = 0, int pop = 0)
    {
        if (wood < w || iron < i || gold < g || food < f || population + pop > populationCap)
            return false;

        wood -= w;
        iron -= i;
        gold -= g;
        food -= f;
        population += pop;
        return true;
    }

    // String-based adder (keeps compatibility)
    public void AddResource(string type, int amount)
    {
        switch (type.ToLowerInvariant())
        {
            case "wood": wood += amount; break;
            case "iron": iron += amount; break;
            case "gold": gold += amount; break;
            case "food": food += amount; break;
        }
    }

    public void AddPopulationCap(int amount)
    {
        populationCap += amount;
    }

    public void RemovePopulation(int amount)
    {
        population -= amount;
        if (population < 0) population = 0;
    }
}