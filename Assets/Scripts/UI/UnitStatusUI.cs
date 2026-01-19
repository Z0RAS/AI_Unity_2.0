using System;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
[RequireComponent(typeof(UnitCombat))]
public class UnitStatusUI : MonoBehaviour
{
    [Header("References (optional - auto-find if empty)")]
    public Image healthFillImage;
    public Image moraleFillImage;

    [Header("Billboard")]
    public bool billboardToCamera = true;

    UnitCombat combat;
    MoraleComponent morale;

    void Awake()
    {
        combat = GetComponent<UnitCombat>();
        morale = GetComponent<MoraleComponent>();

        // try auto-find if inspector fields not set
        if (healthFillImage == null) healthFillImage = FindChildImageByName("Health_Fill");
        if (moraleFillImage == null) moraleFillImage = FindChildImageByName("Morale_Fill");

        // initialize visuals using current values if available
        if (combat != null)
            UpdateHealthFill(combat.CurrentHealth, combat.MaxHealth);
        if (morale != null)
            UpdateMoraleFill(morale.currentMorale);
    }

    void OnEnable()
    {
        if (combat != null)
            combat.OnHealthChanged += OnHealthChanged;
        if (morale != null)
            morale.OnMoraleChanged += OnMoraleChanged;
    }

    void OnDisable()
    {
        if (combat != null)
            combat.OnHealthChanged -= OnHealthChanged;
        if (morale != null)
            morale.OnMoraleChanged -= OnMoraleChanged;
    }

    void Update()
    {
        if (billboardToCamera && Camera.main != null)
        {
            // find the canvas root (Image parent) and rotate to camera
            Transform root = (healthFillImage != null) ? healthFillImage.transform.root : transform;
            if (root != null)
                root.rotation = Camera.main.transform.rotation;
        }
    }

    private void OnHealthChanged(float newHealth)
    {
        if (combat == null) return;
        UpdateHealthFill(newHealth, combat.MaxHealth);
    }

    private void OnMoraleChanged(float newMorale)
    {
        UpdateMoraleFill(newMorale);
    }

    private void UpdateHealthFill(float current, float max)
    {
        if (healthFillImage == null || max <= 0f) return;
        healthFillImage.fillAmount = Mathf.Clamp01(current / max);
    }

    private void UpdateMoraleFill(float moraleValue)
    {
        if (moraleFillImage == null) return;
        moraleFillImage.fillAmount = Mathf.Clamp01(moraleValue / 100f);
    }

    private Image FindChildImageByName(string childName)
    {
        var imgs = GetComponentsInChildren<Image>(true);
        foreach (var img in imgs)
        {
            if (img.gameObject.name.Equals(childName, StringComparison.Ordinal))
                return img;
        }
        return null;
    }
}