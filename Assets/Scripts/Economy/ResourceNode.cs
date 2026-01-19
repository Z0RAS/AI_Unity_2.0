using UnityEngine;
using System.Collections.Generic;

public class ResourceNode : MonoBehaviour
{
    public ResourceType resourceType;
    public int amount = 1;

    // Lightweight global registry to avoid expensive FindObjectsOfType calls each villager frame.
    // Registered on enable/disable so TryFindWork can iterate a small cached collection.
    static readonly List<ResourceNode> allNodes = new List<ResourceNode>();
    public static IReadOnlyList<ResourceNode> AllNodes => allNodes;

    void OnEnable()
    {
        if (!allNodes.Contains(this))
        {
            allNodes.Add(this);
        }
    }

    void OnDisable()
    {
        if (allNodes.Remove(this))
        {
        }
    }

    void OnDestroy()
    {
        if (allNodes.Remove(this))
        {
        }
    }

    public int Harvest(int requested)
    {
        int harvested = Mathf.Min(requested, amount);
        amount -= harvested;

        if (amount <= 0)
        {
            // remove from registry before destroying so others don't iterate a destroyed object
            allNodes.Remove(this);
            Destroy(gameObject);
        }

        return harvested;
    }

    public List<Vector3> GetHarvestSpots(float radius = 0.8f)
    {
        List<Vector3> spots = new List<Vector3>();

        // 4 cardinal directions
        spots.Add(transform.position + new Vector3(radius, 0));
        spots.Add(transform.position + new Vector3(-radius, 0));
        spots.Add(transform.position + new Vector3(0, radius));
        spots.Add(transform.position + new Vector3(0, -radius));

        // 4 diagonals
        spots.Add(transform.position + new Vector3(radius, radius));
        spots.Add(transform.position + new Vector3(-radius, radius));
        spots.Add(transform.position + new Vector3(radius, -radius));
        spots.Add(transform.position + new Vector3(-radius, -radius));

        return spots;
    }

}