using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public class GridManager : MonoBehaviour
{
    public static GridManager Instance;

    [Header("Grid Settings")]
    public float nodeRadius = 0.5f;
    public LayerMask unwalkableMask;

    public Vector2 gridWorldSize;

    Node[,] grid;

    public float nodeDiameter;
    int gridSizeX;
    int gridSizeY;

    public Grid unityGrid;
    public Tilemap tilemap;

    public int MaxSize => gridSizeX * gridSizeY;

    public int GridSizeX => gridSizeX;
    public int GridSizeY => gridSizeY;

    private void Awake()
    {
        Instance = this;

        if (tilemap == null)
            tilemap = FindFirstObjectByType<Tilemap>();

        BoundsInt cellBounds = tilemap.cellBounds;

        // Use cellBounds.min (inclusive) and cellBounds.max (exclusive) directly with CellToWorld.
        // cellBounds.max is the one-past-last cell; converting it gives the world boundary beyond the last cell.
        // This ensures gridWorldSize covers the entire tilemap area and avoids an omitted last row/column.
        Vector3 minCorner = tilemap.CellToWorld(cellBounds.min);
        Vector3 maxCorner = tilemap.CellToWorld(cellBounds.max);

        gridWorldSize = maxCorner - minCorner;

        transform.position = (minCorner + maxCorner) * 0.5f;

        nodeDiameter = nodeRadius * 2f;
        // Use Ceil so the grid covers the entire tilemap extent.
        // Round caused the last row/column to be potentially omitted on some maps.
        gridSizeX = Mathf.Max(1, Mathf.CeilToInt(gridWorldSize.x / nodeDiameter));
        gridSizeY = Mathf.Max(1, Mathf.CeilToInt(gridWorldSize.y / nodeDiameter));

        CreateGrid();

        if (unityGrid == null)
            unityGrid = GetComponent<Grid>();
    }

    // ---------------------------------------------------------
    // GRID GENERATION
    // ---------------------------------------------------------

    void CreateGrid()
    {
        grid = new Node[gridSizeX, gridSizeY];

        // ✅ Tikras Tilemap bottom-left kampas
        Vector3 worldBottomLeft =
            transform.position
            - Vector3.right * gridWorldSize.x / 2f
            - Vector3.up * gridWorldSize.y / 2f;

        for (int x = 0; x < gridSizeX; x++)
        {
            for (int y = 0; y < gridSizeY; y++)
            {
                Vector3 worldCorner =
                    worldBottomLeft
                    + Vector3.right * (x * nodeDiameter)
                    + Vector3.up * (y * nodeDiameter);

                // Use nodeDiameter to compute the center offset (was hardcoded 0.5f)
                Vector3 worldCenter = worldCorner + new Vector3(nodeDiameter * 0.5f, nodeDiameter * 0.5f, 0f);

                bool walkable =
                    !Physics2D.OverlapCircle(worldCenter, nodeRadius * 0.9f, unwalkableMask);

                Node n = new Node(walkable, worldCorner, x, y);
                n.centerPosition = worldCenter;

                grid[x, y] = n;
            }
        }
    }

    // ---------------------------------------------------------
    // WORLD → NODE
    // ---------------------------------------------------------

    public Node NodeFromWorldPoint(Vector3 worldPos)
    {

        float bottom = transform.position.y - gridWorldSize.y * 0.5f;
        float top = transform.position.y + gridWorldSize.y * 0.5f;

        float percentX = (worldPos.x - (transform.position.x - gridWorldSize.x * 0.5f)) / gridWorldSize.x;
        float percentY = (worldPos.y - bottom) / gridWorldSize.y;

        percentX = Mathf.Clamp01(percentX);
        percentY = Mathf.Clamp01(percentY);

        int x = Mathf.FloorToInt(gridSizeX * percentX);
        int y = Mathf.FloorToInt(gridSizeY * percentY);

        x = Mathf.Clamp(x, 0, gridSizeX - 1);
        y = Mathf.Clamp(y, 0, gridSizeY - 1);

        return grid[x, y];
    }

    // ---------------------------------------------------------
    // NEIGHBOURS
    // ---------------------------------------------------------

    public List<Node> GetNeighbours(Node node)
    {
        List<Node> neighbours = new List<Node>();

        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0)
                    continue;

                int checkX = node.gridX + x;
                int checkY = node.gridY + y;

                if (checkX >= 0 && checkX < gridSizeX &&
                    checkY >= 0 && checkY < gridSizeY)
                {
                    neighbours.Add(grid[checkX, checkY]);
                }
            }
        }

        return neighbours;
    }

    // Gizmos: visualize static unwalkable, reserved, physically occupied nodes
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(transform.position,
            new Vector3(gridWorldSize.x, gridWorldSize.y, 1));

        if (grid == null) return;

        var nrs = NodeReservationSystem.Instance;

        float drawSize = nodeDiameter * 0.9f;
        float physCheckRadius = nodeDiameter * 0.35f;

        foreach (Node n in grid)
        {
            if (n == null) continue;

            // Unwalkable should be visually distinct and clearly red
            if (!n.walkable)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawCube(n.centerPosition, Vector3.one * drawSize);
                // reserved outline still useful to show even for unwalkable nodes
                if (nrs != null && nrs.IsReserved(n))
                {
                    Gizmos.color = Color.blue;
                    Gizmos.DrawWireCube(n.centerPosition, Vector3.one * drawSize);
                }
                continue;
            }

            // check physical occupancy by units/other colliders
            Collider2D[] hits = Physics2D.OverlapCircleAll(n.centerPosition, physCheckRadius);
            bool hasUnit = false;
            bool hasBuilding = false;

            foreach (var h in hits)
            {
                if (h == null) continue;
                if (h.GetComponent<UnitAgent>() != null) { hasUnit = true; break; }

                // Only consider finished Building as a "building" occupancy that should be drawn
                var b = h.GetComponent<Building>();
                if (b != null) { hasBuilding = true; break; }

                // BuildingConstruction should only be treated as building occupancy if finished
                var bc = h.GetComponent<BuildingConstruction>();
                if (bc != null)
                {
                    if (bc.isFinished)
                    {
                        hasBuilding = true;
                        break;
                    }
                    else
                    {
                        // under-construction / ghost — do not treat as blocking for walkability visualization
                        continue;
                    }
                }
            }

            // Walkable nodes should be white by default
            Color nodeColor = Color.white;

            if (hasUnit) nodeColor = Color.magenta;
            else if (hasBuilding) nodeColor = new Color(1f, 0.35f, 0.1f, 0.95f); // finished building = orange
            else if (nrs != null && nrs.IsReserved(n)) nodeColor = Color.cyan;

            Gizmos.color = nodeColor;
            Gizmos.DrawCube(n.centerPosition, Vector3.one * drawSize);

            // reserved outline
            if (nrs != null && nrs.IsReserved(n))
            {
                Gizmos.color = Color.blue;
                Gizmos.DrawWireCube(n.centerPosition, Vector3.one * drawSize);
            }

            // small marker for physically occupied nodes
            if (hasUnit)
            {
                Gizmos.color = Color.black;
                Gizmos.DrawSphere(n.centerPosition + Vector3.up * (nodeDiameter * 0.1f), nodeDiameter * 0.08f);
            }
        }
    }

    // ---------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------

    public Node GetClosestWalkableNode(Node start)
    {
        Queue<Node> queue = new Queue<Node>();
        HashSet<Node> visited = new HashSet<Node>();

        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0)
        {
            Node current = queue.Dequeue();

            if (current.walkable)
                return current;

            foreach (Node n in GetNeighbours(current))
            {
                if (!visited.Contains(n))
                {
                    visited.Add(n);
                    queue.Enqueue(n);
                }
            }
        }

        return null;
    }

    public Node FindClosestWalkableNode(int startX, int startY)
    {
        int maxRadius = 6; // kiek toli ieškoti

        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    int nx = startX + dx;
                    int ny = startY + dy;

                    Node n = GetNode(nx, ny);
                    if (n != null && n.walkable)
                        return n;
                }
            }
        }

        return null;
    }


    public Node GetNode(int x, int y)
    {
        if (x < 0 || x >= gridSizeX || y < 0 || y >= gridSizeY)
        {
            Debug.Log($"[GridManager] Node ({x}, {y}) is out of bounds.");
            return null;
        }

        return grid[x, y];
    }

    public IEnumerable<Node> AllNodes
    {
        get
        {
            foreach (var n in grid)
                yield return n;
        }
    }

    // New helper: compute bottom-left grid indices for a footprint centered at worldCenter.
    // This handles odd/even sizes consistently by computing the world bottom-left corner
    // and mapping it to the nearest node.
    public Vector2Int GetStartIndicesForCenteredFootprint(Vector3 worldCenter, Vector2Int footprint)
    {
        // Robust centering: compute center node and subtract half the footprint using floor,
        // so even-sized footprints (e.g. 6) center to start = center - 3 and occupy exactly 6 cells.
        if (footprint.x <= 0 || footprint.y <= 0)
            return new Vector2Int(0, 0);

        Node centerNode = NodeFromWorldPoint(worldCenter);
        if (centerNode == null)
            return new Vector2Int(0, 0);

        int startX = centerNode.gridX - Mathf.FloorToInt(footprint.x * 0.5f);
        int startY = centerNode.gridY - Mathf.FloorToInt(footprint.y * 0.5f);

        // Clamp so we don't run off the grid and ensure footprint fits.
        startX = Mathf.Clamp(startX, 0, Mathf.Max(0, gridSizeX - footprint.x));
        startY = Mathf.Clamp(startY, 0, Mathf.Max(0, gridSizeY - footprint.y));

        return new Vector2Int(startX, startY);
    }

    public void OccupyCellsForBuilding(BuildingConstruction building)
    {
        Vector3 worldCenter = building.transform.position;

        Vector2Int start = GetStartIndicesForCenteredFootprint(worldCenter, building.size);

        for (int x = 0; x < building.size.x; x++)
        {
            for (int y = 0; y < building.size.y; y++)
            {
                Node n = GetNode(start.x + x, start.y + y);
                if (n != null)
                {
                    n.walkable = false;
                }
            }
        }
    }

    public void FreeCellsForBuilding(BuildingConstruction building)
    {
        if (building == null) return;

        Vector2Int start = GetStartIndicesForCenteredFootprint(building.transform.position, building.size);

        for (int x = 0; x < building.size.x; x++)
        {
            for (int y = 0; y < building.size.y; y++)
            {
                Node n = GetNode(start.x + x, start.y + y);
                if (n != null)
                {
                    n.walkable = true;
                }
            }
        }
    }
}