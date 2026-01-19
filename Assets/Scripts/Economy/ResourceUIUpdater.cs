using UnityEngine;
using TMPro;

public class ResourceUIUpdater : MonoBehaviour
{
    [Header("UI Texts (TextMeshPro)")]
    public TextMeshProUGUI foodText;
    public TextMeshProUGUI woodText;
    public TextMeshProUGUI goldText;
    public TextMeshProUGUI ironText;
    public TextMeshProUGUI populationText;

    [Header("Update")]
    [Tooltip("How often (seconds) to refresh the UI if events are not available")]
    public float updateInterval = 0.25f;

    PlayerEconomy playerEconomy;
    float timer;

    private void Start()
    {
        // Try to find PlayerEconomy via GameSystems object first (convention used elsewhere)
        GameObject gs = GameObject.Find("GameSystems");
        if (gs != null)
            playerEconomy = gs.GetComponent<PlayerEconomy>();

        if (playerEconomy == null)
            playerEconomy = FindObjectOfType<PlayerEconomy>();

        else
        {
            // Subscribe to immediate updates
            playerEconomy.OnResourcesChanged += RefreshUI;
            // Refresh once at start
            RefreshUI();
        }
    }

    private void OnDestroy()
    {
        if (playerEconomy != null)
            playerEconomy.OnResourcesChanged -= RefreshUI;
    }

    private void Update()
    {
        if (playerEconomy == null)
            return;

        // Event-driven is preferred; polling kept as fallback to catch any missed changes
        timer += Time.deltaTime;
        if (timer < updateInterval)
            return;

        timer = 0f;
        RefreshUI();
    }

    void RefreshUI()
    {
        if (playerEconomy == null) return;

        if (foodText != null)
            foodText.text = playerEconomy.food.ToString();
        if (woodText != null)
            woodText.text = playerEconomy.wood.ToString();
        if (goldText != null)
            goldText.text = playerEconomy.gold.ToString();
        if (ironText != null)
            ironText.text = playerEconomy.iron.ToString();
        if (populationText != null)
            populationText.text = $"{playerEconomy.population} / {playerEconomy.populationCap}";
    }
}