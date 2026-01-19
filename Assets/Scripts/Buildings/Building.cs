using System.Collections.Generic;
using UnityEngine;

public class Building : MonoBehaviour
{
    public BuildingData data;
    public PlayerEconomy owner;

    public float currentHealth;

    // Damage flash
    private Coroutine damageFlashCoroutine;
    private List<SpriteRenderer> flashRenderers;
    private Color[] flashOriginalColors;

    private void Awake()
    {
        if (data != null)
            currentHealth = data.maxHealth;

        damageFlashCoroutine = null;
    }

    public void Init(BuildingData buildingData, PlayerEconomy buildingOwner = null)
    {
        data = buildingData;
        if (buildingOwner != null)
            owner = buildingOwner;
        else
        {
            GameObject gsObj = GameObject.Find("GameSystems");
            if (gsObj != null) owner = gsObj.GetComponent<PlayerEconomy>();
            if (owner == null)
            {
                var economies = FindObjectsOfType<PlayerEconomy>();
                foreach (var econ in economies)
                {
                    if (econ.isPlayer)
                    {
                        owner = econ;
                        break;
                    }
                }
            }
        }

        currentHealth = data != null ? data.maxHealth : 0f;
    }

    void Start()
    {
        // Assign owner if not set
        if (owner == null)
        {
            GameObject gsObj = GameObject.Find("GameSystems");
            if (gsObj != null) owner = gsObj.GetComponent<PlayerEconomy>();
            if (owner == null)
            {
                var economies = FindObjectsOfType<PlayerEconomy>();
                foreach (var econ in economies)
                {
                    if (econ.isPlayer)
                    {
                        owner = econ;
                        break;
                    }
                }
            }
        }
    }

    public void TakeDamage(float amount)
    {
        float before = currentHealth;
        float effective = Mathf.Max(1, amount - (data != null ? data.armor : 0f));
        currentHealth -= effective;

        

        Color blueTint = new Color(0.4f, 0.7f, 1f);
        FlashDamage(blueTint, 0.18f);

        if (currentHealth <= 0)
        {
            Die();
        }
    }

    void Die()
    {
        // animacija, debris, ir t.t.
        // Notify the enemy-king dialogue system so he can taunt/react
        try
        {
            string bname = (data != null && !string.IsNullOrEmpty(data.buildingName)) ? data.buildingName : gameObject.name;
            EnemyDialogueController.Instance?.SpeakBuildingDestroyed(bname);
        }
        catch
        {
            // swallow errors to avoid breaking destruction
        }

        Destroy(gameObject);
    }

    // -------------------------
    // Damage flash helpers
    // -------------------------
    public void FlashDamage(Color? flashColor = null, float duration = 0.12f)
    {
        Color fc = flashColor ?? Color.red;

        // Stop previous flash and restore
        if (damageFlashCoroutine != null)
        {
            StopCoroutine(damageFlashCoroutine);
            if (flashRenderers != null && flashOriginalColors != null)
            {
                for (int i = 0; i < flashRenderers.Count; i++)
                {
                    if (flashRenderers[i] == null) continue;
                    flashRenderers[i].color = flashOriginalColors[i];
                    var m = flashRenderers[i].material;
                    if (m != null)
                    {
                        if (m.HasProperty("_Color"))
                            m.SetColor("_Color", flashOriginalColors[i]);
                        if (m.HasProperty("_BaseColor"))
                            m.SetColor("_BaseColor", flashOriginalColors[i]);
                    }
                }
            }
            damageFlashCoroutine = null;
        }

        // Gather sprite renderers now (includes children)
        var allRenderers = new List<SpriteRenderer>(GetComponentsInChildren<SpriteRenderer>(true));
        var list = new List<SpriteRenderer>();
        foreach (var sr in allRenderers)
        {
            if (sr == null) continue;

            // skip UI overlays
            if (sr.GetComponentInParent<Canvas>() != null) continue;

            list.Add(sr);
        }

        if (list.Count == 0) return;

        flashRenderers = list;
        flashOriginalColors = new Color[flashRenderers.Count];
        for (int i = 0; i < flashRenderers.Count; i++)
            flashOriginalColors[i] = flashRenderers[i].color;

        damageFlashCoroutine = StartCoroutine(DamageFlashRoutine(fc, duration));
    }

    System.Collections.IEnumerator DamageFlashRoutine(Color flashColor, float duration)
    {
        if (flashRenderers == null || flashRenderers.Count == 0)
            yield break;

        for (int i = 0; i < flashRenderers.Count; i++)
        {
            var sr = flashRenderers[i];
            if (sr == null) continue;
            Color orig = flashOriginalColors[i];
            Color tint = new Color(flashColor.r, flashColor.g, flashColor.b, orig.a);
            sr.color = tint;
            var m = sr.material;
            if (m != null)
            {
                if (m.HasProperty("_Color"))
                    m.SetColor("_Color", tint);
                if (m.HasProperty("_BaseColor"))
                    m.SetColor("_BaseColor", tint);
            }
        }

        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            yield return null;
        }

        for (int i = 0; i < flashRenderers.Count; i++)
        {
            var sr = flashRenderers[i];
            if (sr == null) continue;
            sr.color = flashOriginalColors[i];
            var m = sr.material;
            if (m != null)
            {
                if (m.HasProperty("_Color"))
                    m.SetColor("_Color", flashOriginalColors[i]);
                if (m.HasProperty("_BaseColor"))
                    m.SetColor("_BaseColor", flashOriginalColors[i]);
            }
        }

        flashRenderers = null;
        flashOriginalColors = null;
        damageFlashCoroutine = null;
    }
}