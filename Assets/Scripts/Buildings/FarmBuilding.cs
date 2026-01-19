using UnityEngine;

public class FarmBuilding : MonoBehaviour, IConstructionComplete
{
    [Tooltip("How many population cap this farm grants when built.")]
    public int populationIncrease = 10;

    public void OnConstructionComplete()
    {
        // Prefer explicit owner on Building or DropOff before falling back to GameSystems.
        PlayerEconomy pe = null;

        var building = GetComponent<Building>();
        if (building != null && building.owner != null)
            pe = building.owner;

        if (pe == null)
        {
            var drop = GetComponent<DropOffBuilding>();
            if (drop != null && drop.owner != null)
                pe = drop.owner;
        }

        // Fallback: canonical GameSystems PlayerEconomy, then any PlayerEconomy
        if (pe == null)
        {
            var gs = GameObject.Find("GameSystems");
            if (gs != null)
                pe = gs.GetComponent<PlayerEconomy>();
        }

        if (pe == null)
            pe = Object.FindObjectOfType<PlayerEconomy>();

        if (pe != null)
        {
            pe.AddPopulationCap(populationIncrease);
        }
    }
}
