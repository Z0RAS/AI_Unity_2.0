using UnityEngine;

public class Node : IHeapItem<Node>
{
    public bool walkable;
    public Vector3 worldPosition;   // kampas (jei nori)
    public Vector3 centerPosition;  // CENTRAS (naudojam path ir formacijoms)

    public int gridX;
    public int gridY;

    public int gCost;
    public int hCost;
    public Node parent;

    // Search/version marker used to avoid clearing all nodes every pathfinding call.
    // When a node.searchId != currentSearchId it means its costs/parent are stale.
    public int searchId = -1;

    int heapIndex;

    public Node(bool walkable, Vector3 worldPosition, int gridX, int gridY)
    {
        this.walkable = walkable;
        this.worldPosition = worldPosition;
        this.gridX = gridX;
        this.gridY = gridY;
        this.gCost = int.MaxValue;
    }

    public int fCost => gCost + hCost;

    public int HeapIndex
    {
        get => heapIndex;
        set => heapIndex = value;
    }

    public int CompareTo(Node other)
    {
        int compare = fCost.CompareTo(other.fCost);
        if (compare == 0)
            compare = hCost.CompareTo(other.hCost);

        return -compare;
    }
}