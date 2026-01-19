using UnityEngine;

[CreateAssetMenu(fileName = "BuildingData", menuName = "RTS/Building Data")]
public class BuildingData : ScriptableObject
{
    public string buildingName;
    public GameObject prefab;

    [Header("Durability / Defense")]
    // sensible defaults; individual BuildingData assets should override for specific building types
    public int maxHealth = 1200;
    public float armor = 0f;

    [Header("Costs")]
    // Add explicit costs so designers can tune build prices per building asset
    public int woodCost = 200;
    public int ironCost = 50;
    public int goldCost = 0;
    public int foodCost = 0;

    [Header("Build / Population")]
    [Tooltip("Seconds required to finish construction (for planner / UI)")]
    public float buildTime = 18f;
    [Tooltip("Population cap added when this building completed (houses, castle, etc.)")]
    public int populationBonus = 0;

    [Header("Placement")]
    [Tooltip("Footprint in grid nodes: (width, height). Used by placement/construct code.")]
    public Vector2Int size = new Vector2Int(1, 1);
}