using System.Collections.Generic;
using UnityEngine;

public class Pathfinding : MonoBehaviour
{
    public static Pathfinding Instance;

    private GridManager grid;

    // Incremental search id used to lazily reset node state instead of clearing entire grid.
    private int currentSearchId = 0;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        grid = GridManager.Instance;
    }

    // ---------------------------------------------------------
    // PUBLIC API
    // ---------------------------------------------------------
    // NOTE: added optional requester parameter so callers that have a UnitAgent
    // can let Pathfinding allow targets that are non-walkable but reserved by that agent.
    public List<Vector3> FindPath(Vector3 startPos, Vector3 targetPos, UnitAgent requester = null)
    {
        if (grid == null) return null;

        Node startNode = grid.NodeFromWorldPoint(startPos);
        Node targetNode = grid.NodeFromWorldPoint(targetPos);

        if (startNode == null || targetNode == null) return null;

        // Allow non-walkable target only when requester owns the reservation on that node.
        if (!targetNode.walkable)
        {
            if (requester == null) return null;
            // Accept non-walkable target if the requester holds the reservation for it.
            if (requester.reservedNode != targetNode) return null;
        }

        // New search id for this run
        currentSearchId++;
        int sid = currentSearchId;

        // Lazily initialize startNode for this search
        startNode.searchId = sid;
        startNode.gCost = 0;
        startNode.hCost = GetDistance(startNode, targetNode);
        startNode.parent = null;

        Heap<Node> openSet = new Heap<Node>(grid.MaxSize);
        HashSet<Node> closedSet = new HashSet<Node>();

        openSet.Add(startNode);

        while (openSet.Count > 0)
        {
            Node currentNode = openSet.RemoveFirst();
            closedSet.Add(currentNode);

            if (currentNode == targetNode)
                return RetracePath(startNode, targetNode);

            foreach (Node neighbour in grid.GetNeighbours(currentNode))
            {
                if (neighbour.searchId != sid)
                {
                    neighbour.searchId = sid;
                    neighbour.gCost = int.MaxValue;
                    neighbour.hCost = 0;
                    neighbour.parent = null;
                }

                if (!neighbour.walkable || closedSet.Contains(neighbour))
                    continue;

                int newCost = currentNode.gCost + GetDistance(currentNode, neighbour);

                if (newCost < neighbour.gCost || !openSet.Contains(neighbour))
                {
                    neighbour.gCost = newCost;
                    neighbour.hCost = GetDistance(neighbour, targetNode);
                    neighbour.parent = currentNode;

                    if (!openSet.Contains(neighbour))
                        openSet.Add(neighbour);
                    else
                        openSet.UpdateItem(neighbour);
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------------
    // RETRACE PATH
    // ---------------------------------------------------------

    private List<Vector3> RetracePath(Node startNode, Node endNode)
    {
        List<Node> nodes = new List<Node>();
        Node current = endNode;

        while (current != startNode)
        {
            nodes.Add(current);
            current = current.parent;
        }

        nodes.Reverse();

        List<Vector3> path = new List<Vector3>();

        // Use centerPosition instead of worldPosition
        foreach (Node n in nodes)
            path.Add(n.centerPosition);

        return path;
    }

    // ---------------------------------------------------------
    // DISTANCE
    // ---------------------------------------------------------

    private int GetDistance(Node a, Node b)
    {
        int dx = Mathf.Abs(a.gridX - b.gridX);
        int dy = Mathf.Abs(a.gridY - b.gridY);

        // Diagonal movement cost = 14, straight = 10
        if (dx > dy)
            return 14 * dy + 10 * (dx - dy);
        return 14 * dx + 10 * (dy - dx);
    }
}