using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// Keeps track of discoveries (resources, leader, player base) and manages
/// one-time announcements via EnemyDialogueController. Designed to be attached
/// to the same GameObject as EnemyKingController and used as its intelligence service.
/// </summary>
public class EnemyIntel : MonoBehaviour
{
    public EnemyKingController Owner { get; private set; }

    private readonly Dictionary<ResourceType, HashSet<ResourceNode>> discoveredResourceNodes =
        new Dictionary<ResourceType, HashSet<ResourceNode>>()
        {
            { ResourceType.Food, new HashSet<ResourceNode>() },
            { ResourceType.Wood, new HashSet<ResourceNode>() },
            { ResourceType.Iron, new HashSet<ResourceNode>() },
            { ResourceType.Gold, new HashSet<ResourceNode>() }
        };

    private readonly HashSet<ResourceType> announcedResourceTypes = new HashSet<ResourceType>();
    private bool announcedLeader;
    private bool announcedPlayerBase;
    private bool resourceDiscoveryCompleted;

    public void Init(EnemyKingController owner)
    {
        Owner = owner;
    }

    // Helper: speak via EnemyDialogueController but tolerate missing controller (logs fallback)
    private void SpeakOrLog(string message, Sprite portrait = null, float duration = -1f)
    {
        var dlg = EnemyDialogueController.Instance;
        if (dlg == null)
            dlg = UnityEngine.Object.FindObjectOfType<EnemyDialogueController>();

        if (dlg != null)
        {
            try { dlg.Speak(message, portrait, duration); }
            catch { Debug.LogWarning("[EnemyIntel] Speak threw."); }
        }
        else
        {
            Debug.Log($"[EnemyIntel][NoUI] {message}");
        }
    }

    public void RegisterDiscoveredResource(ResourceNode rn)
    {
        if (rn == null || Owner == null) return;

        if (!discoveredResourceNodes.TryGetValue(rn.resourceType, out var set))
        {
            set = new HashSet<ResourceNode>();
            discoveredResourceNodes[rn.resourceType] = set;
        }

        if (!set.Contains(rn))
        {
            set.Add(rn);

            // Announce only on first discovery of that resource type
            if (!announcedResourceTypes.Contains(rn.resourceType))
            {
                announcedResourceTypes.Add(rn.resourceType);
                string lt = LocalizeResource(rn.resourceType);
                try
                {
                    SpeakOrLog($"Aptikta: {lt}", null, 3.0f);
                    Debug.Log($"[EnemyIntel] Discovered resource type: {rn.resourceType} ({lt}) at {rn.transform.position}");
                }
                catch { }
            }
        }

        if (!resourceDiscoveryCompleted && HaveDiscoveredAllResourceTypes())
        {
            resourceDiscoveryCompleted = true;
            Debug.Log("[EnemyIntel] Resource discovery completed for all types.");
        }
    }

    public void RegisterDiscoveredLeader(GameObject leader)
    {
        if (leader == null || Owner == null) return;
        if (!announcedLeader)
        {
            announcedLeader = true;
            try
            {
                SpeakOrLog($"Lyderis aptiktas: {leader.name}", null, 2.6f);
                Debug.Log($"[EnemyIntel] Leader discovered: {leader.name} at {leader.transform.position}");
            }
            catch { }
        }

        Owner.discoveredLeader = leader;
    }

    public void RegisterDiscoveredPlayerBase(GameObject b)
    {
        if (b == null || Owner == null) return;
        if (!announcedPlayerBase)
        {
            announcedPlayerBase = true;
            try
            {
                string bname = b.GetComponent<Building>()?.data?.buildingName ?? b.name;
                SpeakOrLog($"Bazė aptikta: {bname}", null, 2.6f);
                Debug.Log($"[EnemyIntel] Player base discovered: {bname} at {b.transform.position}");
            }
            catch { }
        }

        Owner.discoveredPlayerBase = b;
    }

    bool HaveDiscoveredAllResourceTypes()
    {
        foreach (ResourceType rt in new[] { ResourceType.Food, ResourceType.Wood, ResourceType.Iron, ResourceType.Gold })
        {
            if (!discoveredResourceNodes.TryGetValue(rt, out var set) || set == null || set.Count == 0)
                return false;
        }
        return true;
    }

    string LocalizeResource(ResourceType rt)
    {
        switch (rt)
        {
            case ResourceType.Wood: return "mediena";
            case ResourceType.Iron: return "geležis";
            case ResourceType.Gold: return "auksas";
            case ResourceType.Food: return "maistas";
            default: return rt.ToString().ToLowerInvariant();
        }
    }
}