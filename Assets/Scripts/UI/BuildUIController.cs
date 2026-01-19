using UnityEngine;

public class BuildUIController : MonoBehaviour
{
    public static BuildUIController Instance;

    [Header("Building data (assign on GameSystems)")]
    public BuildingData baseData;
    public BuildingData farmData;      // house and farm are the same — keep only this
    public BuildingData barracksData;

    private void Awake()
    {
        Instance = this;

    }

    // Backwards-compatible alias: if UI still calls OnBuildHouse it will place the farm.
    public void OnBuildHouse() => OnBuildFarm();

    public void OnBuildFarm()
    {
        BuildingPlacementController.Instance.StartPlacing(farmData);
    }

    public void OnBuildBase()
    {
        BuildingPlacementController.Instance.StartPlacing(baseData);
    }

    public void OnBuildBarracks()
    {
       
        BuildingPlacementController.Instance.StartPlacing(barracksData);
    }
}