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

    private readonly HashSet<ResourceType> announcedResourceTypes = new HashSet<ResourceType>( );
    private bool announcedLeader;
    private bool announcedPlayerBase;
    private bool resourceDiscoveryCompleted;

    public void Init(EnemyKingController owner)
    {
        Owner = owner;
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
                    EnemyDialogueController.Instance?.Speak($"Aptikta: {lt}", null, 3.0f);
                }
                catch { }
            }
        }

        if (!resourceDiscoveryCompleted && HaveDiscoveredAllResourceTypes())
        {
            resourceDiscoveryCompleted = true;
            // Resource discovery completion is handled by setting the flag above
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
                EnemyDialogueController.Instance?.Speak($"Lyderis aptiktas: {leader.name}", null, 2.6f);
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
                EnemyDialogueController.Instance?.Speak($"Bazė aptikta: {bname}", null, 2.6f);
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