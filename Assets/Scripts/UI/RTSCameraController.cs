using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.InputSystem;
using System.Collections.Generic;


public class RTSCameraController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 20f;
    public float edgeSize = 20f;
    public float zoomSpeed = 5f;

    [Header("Zoom")]
    public float minZoom = 5f;
    public float maxZoom = 20f;

    [HideInInspector] public Vector2 minBounds;
    [HideInInspector] public Vector2 maxBounds;

    public Camera cam;

    [Header("Tilemap")]
    public Tilemap tilemap;

    [Header("UI margins (pixels) - reserve screen edges for UI panels")]
    public int uiTopPixels = 17;
    public int uiRightPixels = 230;
    public int uiBottomPixels = 16;
    public int uiLeftPixels = 20;

    [Tooltip("If true camera.rect is adjusted to shrink the camera viewport. If false (recommended for Screen Space - Overlay) only clamping is adjusted.")]
    public bool useViewportRect = false;

    private int lastScreenW;
    private int lastScreenH;

    void Awake()
    {
        if (cam == null) cam = Camera.main;

        if (tilemap != null) tilemap.CompressBounds();
        BoundsInt b = tilemap != null ? tilemap.cellBounds : new BoundsInt();

        Vector3 minCenter = tilemap != null ? tilemap.CellToWorld(b.min) : Vector3.zero;
        Vector3 maxCenter = tilemap != null ? tilemap.CellToWorld(new Vector3Int(b.max.x - 1, b.max.y - 1, 0)) : Vector3.zero;

        minBounds = (Vector2)minCenter;
        maxBounds = (Vector2)maxCenter;

        lastScreenW = Screen.width;
        lastScreenH = Screen.height;

        // initialize targetZoom
        if (cam != null) targetZoom = cam.orthographicSize;

        // apply once at start
        ApplyViewport();
    }

    void Update()
    {
        if (Screen.width != lastScreenW || Screen.height != lastScreenH)
        {
            ApplyViewport();
            lastScreenW = Screen.width;
            lastScreenH = Screen.height;
        }

        HandleKeyboardMovement();
        HandleZoom();
        ClampPosition();
    }

    void HandleKeyboardMovement()
    {
        Vector3 dir = Vector3.zero;

        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.upArrowKey.isPressed) dir += Vector3.up;
        if (kb.downArrowKey.isPressed) dir += Vector3.down;
        if (kb.leftArrowKey.isPressed) dir += Vector3.left;
        if (kb.rightArrowKey.isPressed) dir += Vector3.right;

        transform.position += dir * moveSpeed * Time.unscaledDeltaTime;
    }


    float targetZoom;

    void HandleZoom()
    {
        if (Mouse.current == null || cam == null) return;

        float scroll = Mouse.current.scroll.ReadValue().y;

        if (Mathf.Abs(scroll) > 0.01f)
        {
            targetZoom -= scroll * zoomSpeed * 0.1f;
            targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
        }

        cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, targetZoom, Time.deltaTime * 10f);
    }


    // ClampPosition now respects UI pixel margins without relying on cam.rect scaling.
    void ClampPosition()
    {
        if (cam == null) return;

        // world half-sizes (full-camera view)
        float halfHeight = cam.orthographicSize;
        float halfWidth = cam.orthographicSize * cam.aspect;

        float sw = Mathf.Max(1f, Screen.width);
        float sh = Mathf.Max(1f, Screen.height);

        // normalized UI fractions
        float leftFrac = Mathf.Clamp01(uiLeftPixels / sw);
        float rightFrac = Mathf.Clamp01(uiRightPixels / sw);
        float bottomFrac = Mathf.Clamp01(uiBottomPixels / sh);
        float topFrac = Mathf.Clamp01(uiTopPixels / sh);

        // For horizontal:
        // worldX(u) = camX - halfWidth + u * (2*halfWidth)
        // We need worldX(leftFrac) >= minBounds.x  => camX >= minBounds.x + halfWidth - leftFrac*2*halfWidth
        float minCamX = minBounds.x + halfWidth - (leftFrac * 2f * halfWidth);
        // For right: worldX(1 - rightFrac) <= maxBounds.x => camX <= maxBounds.x - halfWidth + (rightFrac * 2f * halfWidth)
        float maxCamX = maxBounds.x - halfWidth + (rightFrac * 2f * halfWidth);

        // For vertical: similar
        float minCamY = minBounds.y + halfHeight - (bottomFrac * 2f * halfHeight);
        float maxCamY = maxBounds.y - halfHeight + (topFrac * 2f * halfHeight);

        // Ensure min <= max (avoid invalid clamps when map smaller than view)
        if (minCamX > maxCamX) { float mid = (minCamX + maxCamX) * 0.5f; minCamX = maxCamX = mid; }
        if (minCamY > maxCamY) { float mid = (minCamY + maxCamY) * 0.5f; minCamY = maxCamY = mid; }

        Vector3 pos = transform.position;
        pos.x = Mathf.Clamp(pos.x, minCamX, maxCamX);
        pos.y = Mathf.Clamp(pos.y, minCamY, maxCamY);
        transform.position = pos;
    }

    // ApplyViewport optionally sets cam.rect (disabled by default for Screen Space - Overlay)
    private void ApplyViewport()
    {
        if (cam == null) return;

        float sw = Mathf.Max(1f, Screen.width);
        float sh = Mathf.Max(1f, Screen.height);

        float leftN = Mathf.Clamp01(uiLeftPixels / sw);
        float rightN = Mathf.Clamp01(uiRightPixels / sw);
        float topN = Mathf.Clamp01(uiTopPixels / sh);
        float bottomN = Mathf.Clamp01(uiBottomPixels / sh);

        if (useViewportRect)
        {
            float x = leftN;
            float y = bottomN;
            float w = Mathf.Clamp01(1f - leftN - rightN);
            float h = Mathf.Clamp01(1f - topN - bottomN);
            cam.rect = new Rect(x, y, w, h);

            // recompute maxZoom conservatively so zooming out won't expose out-of-bounds
            float mapWidth = maxBounds.x - minBounds.x;
            float mapHeight = maxBounds.y - minBounds.y;

            float maxByHeight = (h > 0f) ? (mapHeight / (2f * h)) : float.MaxValue;
            float maxByWidth = (w > 0f) ? (mapWidth / (2f * cam.aspect * w)) : float.MaxValue;

            maxZoom = Mathf.Min(maxByHeight, maxByWidth);
            targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
            cam.orthographicSize = Mathf.Clamp(cam.orthographicSize, minZoom, maxZoom);
        }
        else
        {
            // do not change cam.rect; keep full-screen camera but clamping uses pixel margins (above)
            // still recompute maxZoom using full-screen mapping
            float mapWidth = maxBounds.x - minBounds.x;
            float mapHeight = maxBounds.y - minBounds.y;
            float maxByHeight = mapHeight / 2f;
            float maxByWidth = mapWidth / (2f * cam.aspect);
            maxZoom = Mathf.Min(maxByHeight, maxByWidth);
            targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
            cam.orthographicSize = Mathf.Clamp(cam.orthographicSize, minZoom, maxZoom);
        }
    }

    // Updated helper: more conservative occupancy detection to prevent commanding onto nodes
    // occupied by units even if they moved since load (checks transform, sim, reserved/assigned).
    private bool IsNodePhysicallyOccupiedByOther(Node node, List<UnitAgent> ignoreList)
    {
        if (node == null) return false;

        var gm = GridManager.Instance;
        float physRadius = Mathf.Max(gm != null ? gm.nodeDiameter * 0.35f : 0.3f, 0.15f);

        // 1) Physics overlap check (fast common-case)
        Collider2D[] hits = Physics2D.OverlapCircleAll(node.centerPosition, physRadius);
        foreach (var h in hits)
        {
            if (h == null) continue;
            var ua = h.GetComponent<UnitAgent>();
            if (ua == null) continue;

            bool isIgnored = false;
            if (ignoreList != null)
            {
                for (int i = 0; i < ignoreList.Count; i++)
                {
                    if (ignoreList[i] == ua) { isIgnored = true; break; }
                }
            }
            if (!isIgnored) return true;
        }

        // 2) Conservative checks across all units (covers moved-from-onload cases):
        //    - reservedNode / lastAssignedNode
        //    - transform-mapped node (unit center)
        //    - simulation position (tick-driven sim)
        UnitAgent[] all = Object.FindObjectsOfType<UnitAgent>();
        foreach (var ua in all)
        {
            if (ua == null) continue;

            bool isIgnored = false;
            if (ignoreList != null)
            {
                for (int i = 0; i < ignoreList.Count; i++)
                {
                    if (ignoreList[i] == ua) { isIgnored = true; break; }
                }
            }
            if (isIgnored) continue;

            // reserved/assigned node claims
            try
            {
                if (ua.reservedNode != null && ua.reservedNode == node) return true;
                if (ua.lastAssignedNode != null && ua.lastAssignedNode == node) return true;
            }
            catch { }

            // transform center -> node mapping
            try
            {
                if (gm != null)
                {
                    var unitNode = gm.NodeFromWorldPoint(ua.transform.position);
                    if (unitNode != null && unitNode.gridX == node.gridX && unitNode.gridY == node.gridY)
                        return true;
                }
            }
            catch { }

            // simPosition (tick simulation) check - more robust during/after moves
            try
            {
                Vector3 sim = ua.SimPosition;
                float d2 = (new Vector2(sim.x - node.centerPosition.x, sim.y - node.centerPosition.y)).sqrMagnitude;
                if (d2 <= physRadius * physRadius) return true;
            }
            catch { }
        }

        return false;
    }

}