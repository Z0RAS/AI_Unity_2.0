using UnityEngine;

[CreateAssetMenu(menuName = "RTS/UnitData")]
public class UnitData : ScriptableObject
{
    public string unitName;
    public UnitCategory category;
    public Sprite icon;
    public GameObject prefab;

    [Header("Stats")]
    // Balanced sensible defaults — per-unit ScriptableObject assets can override these values in the editor.
    public float maxHealth = 100f;
    public float moveSpeed = 3.2f;
    public float attackDamage = 12f;
    public float attackRange = 1.2f;
    public float attackCooldown = 1.0f;
    public float armor = 0f;

    [Header("Costs & Train")]
    // Default costs kept modest; specific unit assets should set correct costs per role.
    public int foodCost = 0;
    public int woodCost = 0;
    public int goldCost = 0;
    public int stoneCost = 0; // used as iron in many systems (legacy naming)
    public float trainTime = 6f;
}