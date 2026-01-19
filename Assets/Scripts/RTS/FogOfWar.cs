using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple grid-aligned Fog of War for 2D RTS maps using the existing GridManager/Node layout.
/// - Creates a texture overlay that covers the GridManager.world bounds.
/// - Reveals cells around player units (owner == playerEconomy) using a configurable sight radius.
/// - Tracks explored vs currently visible cells (explored cells are dimmed, unseen cells are fully dark).
/// 
/// Usage:
/// - Add this component on a scene GameObject (for example an empty "GameSystems" object).
/// - It auto-finds GridManager and PlayerEconomy. Configure defaultSightRadius if your units don't provide per-unit vision.
/// - Optionally add a small `UnitVision` component to any unit with a custom `sightRadius` (class shown below).
/// </summary>
[ExecuteAlways]
public class FogOfWar : MonoBehaviour
{
    public static FogOfWar Instance { get; private set; }

    [Header("Settings")]
    [Tooltip("Fallback sight radius (world units) used when a unit doesn't provide its own vision component.")]
    public float defaultSightRadius = 4f;

    [Tooltip("Seconds between fog updates. Increase to reduce CPU cost.")]
    public float updateInterval = 0.12f;

    [Header("Appearance")]
    [Range(0f, 1f)]
    public float exploredAlpha = 0.6f; // dim for explored but not visible
    public int overlaySortingOrder = 5000;

    // internal grid bounds & maps
    Texture2D fogTexture;
    GameObject overlayObject;
    SpriteRenderer overlayRenderer;

    int minX = 0, minY = 0, width = 0, height = 0;
    Node[,] nodeIndex = null; // optional fast lookup if we rebuild a 2D array
    bool[,] visible;
    bool[,] explored;

    GridManager gm;
    PlayerEconomy playerEconomy;

    float nodeDiameter = 1f;
    Vector2 gridWorldSize;

    float timer = 0f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
            Destroy(this);
        else
            Instance = this;
    }

    private IEnumerator Start()
    {
        // Wait for GridManager to initialize
        yield return new WaitUntil(() => GridManager.Instance != null);

        gm = GridManager.Instance;
        gridWorldSize = gm.gridWorldSize;
        nodeDiameter = gm.nodeRadius * 2f;

        // Try find canonical PlayerEconomy (same approach used elsewhere)
        var gs = GameObject.Find("GameSystems");
        if (gs != null)
            playerEconomy = gs.GetComponent<PlayerEconomy>();

        if (playerEconomy == null)
            playerEconomy = FindObjectOfType<PlayerEconomy>();

        SetupGridMapping();
        CreateOverlayObject();
        ForceUpdateFog();
    }

    void SetupGridMapping()
    {
        // Determine grid extents by enumerating nodes (defensive)
        int maxX = int.MinValue, maxY = int.MinValue;
        minX = int.MaxValue; minY = int.MaxValue;

        List<Node> nodes = new List<Node>();
        foreach (var n in gm.AllNodes)
        {
            if (n == null) continue;
            nodes.Add(n);
            if (n.gridX < minX) minX = n.gridX;
            if (n.gridY < minY) minY = n.gridY;
            if (n.gridX > maxX) maxX = n.gridX;
            if (n.gridY > maxY) maxY = n.gridY;
        }

        // fallback to 0.. if enumeration failed
        if (minX == int.MaxValue || minY == int.MaxValue)
        {
            minX = 0;
            minY = 0;
            // best-effort: attempt to compute width/height from world size/nodeDiameter
            width = Mathf.Max(1, Mathf.RoundToInt(gridWorldSize.x / nodeDiameter));
            height = Mathf.Max(1, Mathf.RoundToInt(gridWorldSize.y / nodeDiameter));
        }
        else
        {
            width = maxX - minX + 1;
            height = maxY - minY + 1;

            // Build a small lookup 2D array for quick access (optional)
            nodeIndex = new Node[width, height];
            foreach (var n in nodes)
            {
                int ix = n.gridX - minX;
                int iy = n.gridY - minY;
                if (ix >= 0 && ix < width && iy >= 0 && iy < height)
                    nodeIndex[ix, iy] = n;
            }
        }

        visible = new bool[width, height];
        explored = new bool[width, height];
    }

    void CreateOverlayObject()
    {
        if (fogTexture != null) DestroyImmediate(fogTexture);
        fogTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        fogTexture.filterMode = FilterMode.Point;
        fogTexture.wrapMode = TextureWrapMode.Clamp;

        // Fill with full darkness initially
        Color[] initial = new Color[width * height];
        for (int i = 0; i < initial.Length; i++)
            initial[i] = Color.black;
        fogTexture.SetPixels(initial);
        fogTexture.Apply();

        // Create sprite so that texture covers gridWorldSize exactly
        float ppuX = (float)width / Mathf.Max(0.0001f, gridWorldSize.x);
        float ppuY = (float)height / Mathf.Max(0.0001f, gridWorldSize.y);
        // Use average to avoid non-square pixels
        float pixelsPerUnit = (ppuX + ppuY) * 0.5f;
        Sprite s = Sprite.Create(fogTexture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), pixelsPerUnit);

        if (overlayObject != null) DestroyImmediate(overlayObject);
        overlayObject = new GameObject("FogOfWar_Overlay");
        overlayObject.transform.SetParent(transform, false);
        overlayRenderer = overlayObject.AddComponent<SpriteRenderer>();
        overlayRenderer.sprite = s;
        overlayRenderer.sortingOrder = overlaySortingOrder;
        overlayRenderer.drawMode = SpriteDrawMode.Simple;
        overlayRenderer.transform.position = gm.transform.position; // center aligned with grid manager
    }

    void Update()
    {
        if (gm == null) return;

        timer += Time.deltaTime;
        if (timer >= updateInterval)
        {
            timer = 0f;
            UpdateFog();
        }
    }

    void ForceUpdateFog()
    {
        timer = updateInterval;
        Update();
    }

    void UpdateFog()
    {
        // Reset visibility map
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                visible[x, y] = false;

        // Find all player units and reveal around them
        UnitAgent[] allUnits = FindObjectsOfType<UnitAgent>();
        foreach (var u in allUnits)
        {
            if (u == null) continue;
            // Reveal only units that belong to playerEconomy (null playerEconomy => reveal all)
            if (playerEconomy != null && u.owner != playerEconomy) continue;

            float sight = defaultSightRadius;

            // Optional custom vision component: if unit has UnitVision, use it
            if (u.TryGetComponent<UnitVision>(out var uv))
            {
                sight = Mathf.Max(0.01f, uv.sightRadius);
            }

            RevealAtPosition(u.transform.position, sight);
        }

        // Build pixel array and apply to texture in one batch
        Color[] pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (visible[x, y])
                {
                    // currently visible -> fully transparent
                    pixels[idx] = new Color(0f, 0f, 0f, 0f);
                    explored[x, y] = true;
                }
                else if (explored[x, y])
                {
                    // explored but not currently visible -> dim
                    pixels[idx] = new Color(0f, 0f, 0f, exploredAlpha);
                }
                else
                {
                    // never seen -> fully black
                    pixels[idx] = Color.black;
                }
            }
        }

        fogTexture.SetPixels(pixels);
        fogTexture.Apply();
    }

    void RevealAtPosition(Vector3 worldPos, float radius)
    {
        Node n = gm.NodeFromWorldPoint(worldPos);
        if (n == null) return;

        int centerX = n.gridX - minX;
        int centerY = n.gridY - minY;
        if (centerX < 0 || centerY < 0 || centerX >= width || centerY >= height) return;

        int radiusInNodes = Mathf.CeilToInt(radius / nodeDiameter);

        int x0 = Mathf.Max(0, centerX - radiusInNodes);
        int x1 = Mathf.Min(width - 1, centerX + radiusInNodes);
        int y0 = Mathf.Max(0, centerY - radiusInNodes);
        int y1 = Mathf.Min(height - 1, centerY + radiusInNodes);

        // world center position for the center node (use nodeIndex if available)
        Vector3 centerWorld = (nodeIndex != null && nodeIndex[centerX, centerY] != null) ? nodeIndex[centerX, centerY].centerPosition : n.centerPosition;

        float radiusSqr = radius * radius;

        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                Node node = (nodeIndex != null) ? nodeIndex[x, y] : gm.GetNode(x + minX, y + minY);
                if (node == null) continue;

                float d = (node.centerPosition - worldPos).sqrMagnitude;
                if (d <= radiusSqr)
                {
                    visible[x, y] = true;
                    explored[x, y] = true;
                }
            }
        }
    }

    // Optional public helpers:
    public void RevealCircle(Vector3 worldPos, float radius) => RevealAtPosition(worldPos, radius);

    // Editor-friendly reset
    public void ResetExplored()
    {
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                visible[x, y] = false;
                explored[x, y] = false;
            }

        ForceUpdateFog();
    }

    // Public query API so gameplay systems can check whether a world position is visible/explored.
    public bool IsReady => gm != null && visible != null;

    public bool IsVisibleAtWorldPos(Vector3 worldPos)
    {
        if (!IsReady) return false;
        Node n = gm.NodeFromWorldPoint(worldPos);
        if (n == null) return false;
        int cx = n.gridX - minX;
        int cy = n.gridY - minY;
        if (cx < 0 || cy < 0 || cx >= width || cy >= height) return false;
        return visible[cx, cy];
    }

    public bool IsExploredAtWorldPos(Vector3 worldPos)
    {
        if (!IsReady) return false;
        Node n = gm.NodeFromWorldPoint(worldPos);
        if (n == null) return false;
        int cx = n.gridX - minX;
        int cy = n.gridY - minY;
        if (cx < 0 || cy < 0 || cx >= width || cy >= height) return false;
        return explored[cx, cy];
    }
}

/// <summary>
/// Optional small component to give individual units a custom sight radius.
/// Add this to prefabs to override FogOfWar.defaultSightRadius for that unit.
/// </summary>
public class UnitVision : MonoBehaviour
{
    public float sightRadius = 4f;
}