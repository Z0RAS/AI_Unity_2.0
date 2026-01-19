using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Simple morale/emotion component.
/// - Morale 0..100
/// - Periodically evaluates nearby allies/enemies and decays/recoveries
/// - Exposes events: OnMoraleChanged, OnBroken (below flee threshold), OnRecovered (above rally threshold)
/// </summary>
[DisallowMultipleComponent]
public class MoraleComponent : MonoBehaviour
{
    [Header("Base values")]
    [Range(0f, 100f)] public float baseMorale = 50f;
    [Range(0f, 100f)] public float currentMorale = 50f;
    [Header("Thresholds")]
    [Range(0f, 100f)] public float fleeThreshold = 25f;      // when morale <= this -> broken/flee
    [Range(0f, 100f)] public float recoveryThreshold = 45f;  // when morale >= this -> recovered
    [Header("Dynamics")]
    public float naturalRecoveryPerSecond = 1.5f;
    public float nearbyInfluencePerUnitPerSecond = 2.0f; // allies +, enemies -
    public float damageImpactMultiplier = 0.5f; // how much morale drops per damage point
    public float deathNearbyImpact = 15f; // morale loss for nearby allies when an ally dies
    public float checkRadius = 6f;
    public float checkInterval = 1.0f;

    [Header("Low-health -> morale override")]
    [Tooltip("If unit health falls below this fraction (0..1), morale will be forced to abysmal.")]
    [Range(0f, 1f)] public float lowHealthPercentThreshold = 0.25f;
    [Tooltip("If true, when low health is detected morale is immediately forced low (invoke OnBroken).")]
    public bool forceAbysmalOnLowHealth = true;

    // Events
    public event Action<float> OnMoraleChanged;
    public event Action OnBroken;
    public event Action OnRecovered;

    bool broken = false;

    // internal state for low-health override
    bool lowHealthMoraleForced = false;
    UnitCombat ownerCombat;

    private void Start()
    {
        currentMorale = Mathf.Clamp(currentMorale, 0f, 100f);

        // subscribe to UnitCombat health updates so we can force morale drop when health is low
        ownerCombat = GetComponent<UnitCombat>();
        if (ownerCombat != null)
        {
            ownerCombat.OnHealthChanged += HandleOwnerHealthChanged;
        }

        StartCoroutine(PeriodicEvaluate());
    }

    private void OnDestroy()
    {
        if (ownerCombat != null)
            ownerCombat.OnHealthChanged -= HandleOwnerHealthChanged;
    }

    IEnumerator PeriodicEvaluate()
    {
        var wait = new WaitForSeconds(checkInterval);

        while (true)
        {
            EvaluateNearbyInfluence();
            NaturalRecoveryTick();
            ClampAndFireEvents();
            yield return wait;
        }
    }

    void EvaluateNearbyInfluence()
    {
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, checkRadius);
        int allies = 0, enemies = 0;

        var myOwner = GetComponent<UnitAgent>()?.owner;

        foreach (var h in hits)
        {
            if (h == null) continue;
            var ua = h.GetComponent<UnitAgent>();
            if (ua == null) continue;
            if (ua == GetComponent<UnitAgent>()) continue;

            if (ua.owner != null && myOwner != null && ua.owner == myOwner)
                allies++;
            else
                enemies++;
        }

        // Allies increase morale, enemies decrease it. Scale by interval -> convert per-second.
        float net = (allies - enemies) * nearbyInfluencePerUnitPerSecond * checkInterval;
        if (net != 0f)
            AdjustMorale(net);
    }

    void NaturalRecoveryTick()
    {
        // bias back toward base morale slowly
        if (currentMorale < baseMorale)
            AdjustMorale(naturalRecoveryPerSecond * checkInterval);
        else if (currentMorale > baseMorale)
            AdjustMorale(-naturalRecoveryPerSecond * checkInterval * 0.2f); // small decay if above base
    }

    void ClampAndFireEvents()
    {
        float clamped = Mathf.Clamp(currentMorale, 0f, 100f);
        if (!Mathf.Approximately(clamped, currentMorale))
            currentMorale = clamped;

        OnMoraleChanged?.Invoke(currentMorale);

        if (!broken && currentMorale <= fleeThreshold)
        {
            broken = true;
            OnBroken?.Invoke();
        }
        else if (broken && currentMorale >= recoveryThreshold)
        {
            broken = false;
            OnRecovered?.Invoke();
        }
    }

    // Public API ----------------------------------------------------------

    public void AdjustMorale(float delta)
    {
        currentMorale = Mathf.Clamp(currentMorale + delta, 0f, 100f);
        OnMoraleChanged?.Invoke(currentMorale);

        // trigger immediate events if thresholds crossed
        if (!broken && currentMorale <= fleeThreshold)
        {
            broken = true;
            OnBroken?.Invoke();
        }
        else if (broken && currentMorale >= recoveryThreshold)
        {
            broken = false;
            OnRecovered?.Invoke();
        }
    }

    public void OnDamageTaken(float damage)
    {
        if (damage <= 0f) return;
        float delta = -damage * damageImpactMultiplier;
        AdjustMorale(delta);
    }

    public void OnNearbyAllyDied()
    {
        AdjustMorale(-deathNearbyImpact);
    }

    public bool IsBroken() => broken;

    // -----------------------
    // Health-driven morale override
    // -----------------------
    void HandleOwnerHealthChanged(float newHealth)
    {
        if (!forceAbysmalOnLowHealth || ownerCombat == null)
            return;

        float max = ownerCombat.MaxHealth;
        if (max <= Mathf.Epsilon) return;

        float frac = newHealth / max;

        if (frac <= lowHealthPercentThreshold)
        {
            if (!lowHealthMoraleForced)
            {
                // force morale to abysmal (drop enough to go below flee threshold)
                lowHealthMoraleForced = true;
                AdjustMorale(-100f); // clamps to 0 and triggers OnBroken
            }
        }
        else
        {
            // health recovered above threshold: allow normal morale dynamics again
            if (lowHealthMoraleForced)
            {
                lowHealthMoraleForced = false;
                // do not forcibly restore morale here; natural recovery will take effect.
            }
        }
    }
}