using System.Collections.Generic;
using UnityEngine;


[RequireComponent(typeof(Rigidbody2D))]
public class UnitAgent : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 3.5f;
    public float acceleration = 10f;
    public float stoppingDistance = 0.05f;
    public float repulsionRadius = 0.5f;
    public float repulsionStrength = 1.0f;

    [Tooltip("Seconds to suppress auto-aggro after a player move command")]
    public float moveOrderAggroSuppress = 1.2f;

    private Rigidbody2D rb;
    private List<Vector3> path;
    private int pathIndex;
    private Vector2 velocity;
    private bool hasPath = false;

    public Node reservedNode;
    private Vector3 finalPathTarget;

    private Vector3 lastPosition;
    public Node lastAssignedNode;
    private float stuckTimer = 0f;
    private float stuckTimeThreshold = 0.25f;

    public PlayerEconomy owner;

    public UnitCategory category;
    public SelectionIndicator selectionIndicator;

    private Collider2D col;

    [Header("Path Line (visual)")]
    public bool showPathLines = true;
    public Color pathColor = new Color(0.2f, 0.8f, 1f, 0.9f);
    public float lineWidth = 0.06f;

    private LineRenderer lineRenderer;

    public int formationId = 0;
    public int formationSlot = -1;

    public bool holdReservation = false;

    private SpriteRenderer primarySprite;

    // simulation positions for tick/interpolation
    private Vector3 simPrevPosition;
    private Vector3 simPosition;

    // expose sim position for other systems (used in repulsion)
    public Vector3 SimPosition => simPosition;

    private bool isIdle = false;

    // smooth snap (tick-driven)
    private bool smoothActive = false;
    private Vector3 smoothStart;
    private Vector3 smoothTarget;
    private float smoothDuration;
    private float smoothElapsed;

    // damage flash (tick-driven visual)
    private bool flashActive = false;
    private Color flashColor;
    private float flashHold = 0.1f;
    private float flashFade = 0.15f;
    private float flashElapsed = 0f;
    private Color flashOrigColor;

    private bool _handlingArrival = false;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        col = GetComponent<Collider2D>();
        lastPosition = transform.position;

        if (rb != null)
        {
            rb.simulated = true;
            rb.WakeUp();
        }

        lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer == null)
            lineRenderer = gameObject.AddComponent<LineRenderer>();

        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.positionCount = 0;
        lineRenderer.startColor = pathColor;
        lineRenderer.endColor = pathColor;
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;
        lineRenderer.numCapVertices = 4;
        lineRenderer.sortingOrder = 3000;
        lineRenderer.enabled = showPathLines;

        primarySprite = GetComponent<SpriteRenderer>();
        if (primarySprite == null)
            primarySprite = GetComponentInChildren<SpriteRenderer>();
    }

    private void OnEnable()
    {
        // keep sim positions synced when enabled
        simPrevPosition = transform.position;
        simPosition = transform.position;

        // Ensure we are subscribed to the static tick event. Remove before add to avoid double-subscribe.
        try { TimeController.OnTick -= OnTick; } catch { }
        try { TimeController.OnTick += OnTick; } catch { }
    }

    private void OnDisable()
    {
        // unsubscribe to avoid dangling handlers
        try { TimeController.OnTick -= OnTick; } catch { }
    }

    private void OnDestroy()
    {
        // final cleanup: ensure unsubscribed and reservation released
        try { TimeController.OnTick -= OnTick; } catch { }
        try { NodeReservationSystem.Instance?.ReleaseReservation(this); } catch { }
    }

    private void Start()
    {
        var gm = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;

        if (gm != null && nrs != null)
        {
            Node current = gm.NodeFromWorldPoint(transform.position);
            if (current != null && current.walkable)
            {
                if (!nrs.IsReserved(current))
                {
                    nrs.ReserveNode(current, this);
                    reservedNode = current;
                    holdReservation = true;
                    // snap sim positions so unit is considered at node center immediately
                    SnapSimulationPositionsTo(current.centerPosition);
                }
            }
        }
    }

    // Helper: force-snap simulation & transform positions to a world position (used by distributor)
    public void SnapSimulationPositionsTo(Vector3 worldPos)
    {
        simPosition = worldPos;
        simPrevPosition = worldPos;
        transform.position = worldPos;
        lastPosition = simPosition;
        if (rb != null) { rb.position = new Vector2(worldPos.x, worldPos.y); rb.WakeUp(); rb.simulated = true; }
    }

    // New helper: snap unit to node center, optionally hold reservation
    public void SnapToNodeCenter(Node node, bool holdReservationFlag = true)
    {
        if (node == null) return;
        try { if (holdReservationFlag) holdReservation = true; } catch { }
        try { reservedNode = node; } catch { }
        SnapSimulationPositionsTo(node.centerPosition);
        // keep unit active for simulation
        SetIdle(false);
    }

    // tick callback - simulation updates happen here
    private void OnTick(float dt)
    {
        // snapshot previous simulation position for interpolation
        simPrevPosition = simPosition;

        if (isIdle)
        {
            if (rb != null) rb.simulated = !isIdle;
            // still run smoothing/flash ticks so visuals complete
            UpdateSmoothTick(dt);
            UpdateFlashTick(dt);
            return;
        }

        // path movement tick
        if (hasPath)
            FollowPathTick(dt);

        // smoothing (snap) tick
        UpdateSmoothTick(dt);

        // damage flash tick
        UpdateFlashTick(dt);

        // stuck detection uses simPosition
        CheckIfStuckTick(dt);

        // keep rb.position roughly in sync for physics readers if needed
        if (rb != null)
            rb.position = simPosition;
    }

    // Smooth snap control
    void BeginSmoothSnap(Vector3 target, float duration)
    {
        if (duration <= 0f)
        {
            simPosition = target;
            simPrevPosition = target;
            // arrival callbacks
            OnArrivedAtReservedNode();
            return;
        }

        smoothActive = true;
        smoothStart = simPosition;
        smoothTarget = target;
        smoothDuration = Mathf.Max(1e-6f, duration);
        smoothElapsed = 0f;
    }

    void UpdateSmoothTick(float dt)
    {
        if (!smoothActive) return;

        smoothElapsed += dt;
        float t = Mathf.Clamp01(smoothElapsed / smoothDuration);
        simPosition = Vector3.Lerp(smoothStart, smoothTarget, t);

        if (smoothElapsed >= smoothDuration)
        {
            smoothActive = false;
            simPosition = smoothTarget;
            OnArrivedAtReservedNode();
        }
    }

    void OnArrivedAtReservedNode()
    {
        // Prevent re-entrant calls which can happen when other components (e.g. UnitCombat)
        // call back into UnitAgent and trigger another smooth snap → stack overflow.
        if (_handlingArrival) return;
        _handlingArrival = true;

        try
        {
            var uc = GetComponent<UnitCombat>();
            if (uc != null)
            {
                try { uc.OnArrivedAtReservedNode(); } catch { }
            }

            var vt = GetComponent<VillagerTaskSystem>();
            if (vt != null)
            {
                try { vt.OnMoveCompleted(); } catch { }
            }

            // Only release reservation if we're not holding it (original behavior).
            if (!holdReservation && NodeReservationSystem.Instance != null && formationId == 0)
            {
                try { NodeReservationSystem.Instance.ReleaseReservation(this); } catch { }
            }
        }
        finally
        {
            _handlingArrival = false;
        }
    }

    public void SetDestinationToNode(Node node, bool suppressAutoAggro = false)
    {
        // Ensure the agent is active for simulation when given a movement order.
        SetIdle(false);

        var morale = GetComponent<MoraleComponent>();
        if (morale != null && morale.IsBroken())
            return;

        if (node == null)
        {
            hasPath = false;
            ClearPathLine();
            return;
        }

        // Avoid re-computing path/reservation if we already are heading to the same final target.
        // Use a small tolerance to compare world positions.
        if (hasPath)
        {
            float eps = 0.001f;
            if (Vector3.Distance(finalPathTarget, node.centerPosition) <= eps)
                return;
        }

        var uc = GetComponent<UnitCombat>();
        if (uc != null)
        {
            uc.SetTargetNoNotify((GameObject)null);
            if (suppressAutoAggro)
                uc.SuppressAutoAggro(moveOrderAggroSuppress);
        }

        // If we had a previous reservation and it's different from this target, release it (unless holdReservation).
        try
        {
            if (reservedNode != null && reservedNode != node && !holdReservation)
            {
                NodeReservationSystem.Instance?.ReleaseReservation(this);
                reservedNode = null;
            }
        }
        catch { }

        Node finalReserve = null;
        var nrs = NodeReservationSystem.Instance;
        if (nrs != null)
        {
            // Try to directly reserve the requested node using the new API.
            try
            {
                if (nrs.ReserveNode(node, this))
                {
                    finalReserve = node;
                }
                else
                {
                    finalReserve = nrs.FindAndReserveBestNode(node, this, 3);
                    if (finalReserve == null && nrs.debugReservations)
                        Debug.Log($"[NodeReservation] no alternative reserve found for target node {node.gridX},{node.gridY} for unit {this.name}");
                }
            }
            catch { finalReserve = null; }
        }

        if (finalReserve != null)
        {
            reservedNode = finalReserve;
            holdReservation = false;
        }
        else
        {
            reservedNode = null;
            holdReservation = false;
        }

        finalPathTarget = (finalReserve != null) ? finalReserve.centerPosition : node.centerPosition;

        List<Vector3> newPath = Pathfinding.Instance.FindPath(simPosition, finalPathTarget, this);

        if (newPath == null || newPath.Count == 0)
        {
            hasPath = false;
            ClearPathLine();
            return;
        }

        newPath[newPath.Count - 1] = finalPathTarget;
        path = newPath;
        pathIndex = 0;
        hasPath = true;

        // ensure physics simulation awake so tick moves unit
        if (rb != null) { rb.simulated = true; rb.WakeUp(); }

        UpdatePathLine();
    }

    public void ForceSetDestinationToNode(Node node)
    {
        // Ensure the agent is active for simulation when given a movement order.
        SetIdle(false);

        if (node == null)
        {
            hasPath = false;
            ClearPathLine();
            return;
        }

        var nrs = NodeReservationSystem.Instance;

        if (holdReservation && reservedNode != null)
        {
            finalPathTarget = reservedNode.centerPosition;
        }
        else
        {
            Node finalReserve = null;
            if (nrs != null)
            {
                try
                {
                    if (nrs.ReserveNode(node, this))
                    {
                        finalReserve = node;
                    }
                    else
                    {
                        finalReserve = nrs.FindAndReserveBestNode(node, this, 2);
                    }
                }
                catch { finalReserve = null; }
            }

            if (finalReserve != null)
            {
                reservedNode = finalReserve;
                holdReservation = true;
                finalPathTarget = finalReserve.centerPosition;
            }
            else
            {
                finalPathTarget = node.centerPosition;
            }
        }

        List<Vector3> newPath = Pathfinding.Instance.FindPath(simPosition, finalPathTarget, this);

        if (newPath == null || newPath.Count == 0)
        {
            hasPath = false;
            ClearPathLine();
            return;
        }

        newPath[newPath.Count - 1] = finalPathTarget;
        path = newPath;
        pathIndex = 0;
        hasPath = true;

        if (rb != null) { rb.simulated = true; rb.WakeUp(); }

        UpdatePathLine();
    }

    // New: direct-move API — sets path to node center without attempting any node reservation.
    // Clears agent local reservation so it won't later snap back to a held reserved node.
    public void MoveDirectToNode(Node node)
    {
        // Ensure the agent is active for simulation when given a movement order.
        SetIdle(false);

        if (node == null)
        {
            hasPath = false;
            ClearPathLine();
            return;
        }

        // Release any global reservation and clear local reservation reference so we won't snap back.
        try
        {
            var nrs = NodeReservationSystem.Instance;
            if (nrs != null) nrs.ReleaseReservation(this);
        }
        catch { }
        reservedNode = null;
        holdReservation = false;

        // Set final target & compute path (no requester so pathfinder treats target normally).
        finalPathTarget = node.centerPosition;
        List<Vector3> newPath = null;
        try
        {
            newPath = Pathfinding.Instance.FindPath(simPosition, finalPathTarget, null);
        }
        catch { newPath = null; }

        if (newPath == null || newPath.Count == 0)
        {
            hasPath = false;
            ClearPathLine();
            return;
        }

        newPath[newPath.Count - 1] = finalPathTarget;
        path = newPath;
        pathIndex = 0;
        hasPath = true;

        // ensure physics simulation awake so tick moves unit
        if (rb != null) { rb.simulated = true; rb.WakeUp(); }

        SetIdle(false);
        UpdatePathLine();
    }

    public void SetIdle(bool idle)
    {
        isIdle = idle;
        if (rb != null) rb.simulated = !idle;
    }

    public bool HasPath()
    {
        return hasPath;
    }

    public bool TrySetDestinationToNode(Node node, bool suppressAutoAggro = false)
    {
        SetDestinationToNode(node, suppressAutoAggro);
        return HasPath();
    }

    public bool DebugIsIdle => isIdle;
    public bool DebugHasPath => hasPath;
    public Node DebugReservedNode => reservedNode;

    private void FollowPathTick(float dt)
    {
        if (path == null || pathIndex >= path.Count)
        {
            hasPath = false;
            velocity = Vector2.zero;
            SetIdle(false);
            ClearPathLine();

            if (reservedNode != null)
                BeginSmoothSnap(reservedNode.centerPosition, 0f);
            else
                SnapToNearestNodeCenter();

            return;
        }

        var gm = GridManager.Instance;
        float nodeDiameter = (gm != null) ? gm.nodeDiameter : 1f;
        // Use a larger, grid-aware acceptance radius to advance waypoints reliably on long paths.
        float acceptanceRadius = Mathf.Max(stoppingDistance, nodeDiameter * 0.25f);
        float finalSnapRadius = Mathf.Max(nodeDiameter * 0.18f, 0.1f);

        Vector3 target = path[pathIndex];
        Vector2 sim2 = new Vector2(simPosition.x, simPosition.y);
        Vector2 target2 = new Vector2(target.x, target.y);
        Vector2 dir = target2 - sim2;
        float dist = dir.magnitude;

        // stuck recovery / replan (unchanged behavior) but when accepting new path, snap to nearest waypoint
        if (stuckTimer > stuckTimeThreshold && dist > Mathf.Max(stoppingDistance * 2f, 0.2f))
        {
            var pf = Pathfinding.Instance;
            var nrs = NodeReservationSystem.Instance;
            if (pf != null)
            {
                List<Vector3> retry = pf.FindPath(simPosition, finalPathTarget, this);
                if (retry != null && retry.Count > 0)
                {
                    retry[retry.Count - 1] = finalPathTarget;
                    // Find nearest waypoint index on the new path and continue from there to avoid moving backwards.
                    int nearestIdx = 0;
                    float bestSqr = float.MaxValue;
                    for (int i = 0; i < retry.Count; i++)
                    {
                        float sq = (new Vector2(retry[i].x, retry[i].y) - sim2).sqrMagnitude;
                        if (sq < bestSqr) { bestSqr = sq; nearestIdx = i; }
                    }
                    path = retry;
                    pathIndex = Mathf.Clamp(nearestIdx, 0, path.Count - 1);
                    UpdatePathLine();

                    if (reservedNode == null && nrs != null && gm != null)
                    {
                        Node targetNode = gm.NodeFromWorldPoint(finalPathTarget);
                        if (targetNode != null)
                            nrs.FindAndReserveBestNode(targetNode, this, 3);
                    }
                }
            }
            stuckTimer = 0f;
        }

        // If we're close enough to advance the waypoint, do so using acceptanceRadius (prevents micro-oscillation).
        if (dist <= acceptanceRadius)
        {
            if (pathIndex == path.Count - 1)
            {
                // final node: snap and finish
                Vector3 snapTarget = finalPathTarget;
                if (reservedNode != null) snapTarget = reservedNode.centerPosition;

                hasPath = false;
                velocity = Vector2.zero;
                ClearPathLine();

                simPosition = snapTarget;
                BeginSmoothSnap(snapTarget, 0f);
                return;
            }

            pathIndex++;
            UpdatePathLine();
            return;
        }

        // Normal movement
        Vector2 move = Vector2.zero;
        if (dir.sqrMagnitude > 0.0001f)
            move = dir.normalized * moveSpeed;

        // Reduce repulsion magnitude slightly on long moves to avoid reversing direction in narrow corridors
        Vector2 repulsion = ComputeRepulsion() * 0.6f;
        Vector2 combined = move + repulsion;
        if (combined.sqrMagnitude > 0.000001f)
            combined = combined.normalized * moveSpeed;

        velocity = combined;
        Vector2 newPos = sim2 + combined * dt;
        simPosition = new Vector3(newPos.x, newPos.y, simPosition.z);
        UpdatePathLine();
    }

    private void CheckIfStuckTick(float dt)
    {
        float moved = Vector2.Distance(new Vector2(simPosition.x, simPosition.y), new Vector2(lastPosition.x, lastPosition.y));
        if (moved < 0.01f)
            stuckTimer += dt;
        else
            stuckTimer = 0f;

        lastPosition = simPosition;
    }

    private Vector2 ComputeRepulsion()
    {
        if (!hasPath) return Vector2.zero;

        Vector2 sim2 = new Vector2(simPosition.x, simPosition.y);
        float distToGoal = Vector2.Distance(sim2, new Vector2(finalPathTarget.x, finalPathTarget.y));
        if (distToGoal < 0.6f) return Vector2.zero;

        // Use simPosition for overlap query to avoid mismatch between tick sim and frame transform
        Collider2D[] hits = Physics2D.OverlapCircleAll(sim2, repulsionRadius);
        Vector2 rep = Vector2.zero;

        foreach (var hit in hits)
        {
            if (hit == null) continue;
            var otherRb = hit.attachedRigidbody;
            if (otherRb == rb) continue;

            UnitAgent other = hit.GetComponent<UnitAgent>();
            if (other == null) continue;

            // Prefer using other.SimPosition so repulsion is calculated in simulation space
            Vector2 otherPos = new Vector2(other.SimPosition.x, other.SimPosition.y);
            Vector2 away = sim2 - otherPos;
            float d = away.magnitude;

            if (d > 0.001f && d < repulsionRadius)
            {
                float force = repulsionStrength * (1f - d / repulsionRadius);
                rep += away.normalized * force;
            }
        }

        return rep;
    }

    // Damage flash (tick-driven)
    public void BeginDamageFlash(Color? flash = null, float hold = 0.1f, float fade = 0.15f)
    {
        if (primarySprite == null) primarySprite = GetComponent<SpriteRenderer>() ?? GetComponentInChildren<SpriteRenderer>();
        if (primarySprite == null) return;

        flashActive = true;
        flashColor = flash ?? Color.red;
        flashHold = Mathf.Max(0f, hold);
        flashFade = Mathf.Max(0f, fade);
        flashElapsed = 0f;
        flashOrigColor = primarySprite.color;
        ApplyTint(primarySprite, new Color(flashColor.r, flashColor.g, flashOrigColor.a));
    }

    private void UpdateFlashTick(float dt)
    {
        if (!flashActive) return;

        flashElapsed += dt;
        if (flashElapsed < flashHold)
        {
            // still holding
            return;
        }

        float fadeElapsed = flashElapsed - flashHold;
        if (fadeElapsed >= flashFade)
        {
            // finished
            ApplyTint(primarySprite, flashOrigColor);
            flashActive = false;
            return;
        }

        float t = Mathf.Clamp01(fadeElapsed / flashFade);
        Color c = Color.Lerp(new Color(flashColor.r, flashColor.g, flashOrigColor.a), flashOrigColor, t);
        ApplyTint(primarySprite, c);
    }

    private void Update()
    {
        // Interpolate visual position between last simulated tick and current simulated position.
        if (TimeController.Instance != null)
        {
            float alpha = TimeController.Instance.InterpolationAlpha;
            transform.position = Vector3.Lerp(simPrevPosition, simPosition, alpha);
        }
        else
        {
            transform.position = simPosition;
        }

        // Do NOT forcibly set rb.position every frame here — keep physics sync on tick only.
        // (rb.position will be set during OnTick when simPosition updates.)
    }

    public void Stop()
    {
        hasPath = false;
        velocity = Vector2.zero;
        if (NodeReservationSystem.Instance != null && !holdReservation)
            NodeReservationSystem.Instance.ReleaseReservation(this);
        ClearPathLine();

        if (reservedNode != null)
        {
            BeginSmoothSnap(reservedNode.centerPosition, 0f);
        }
        else
        {
            SnapToNearestNodeCenter();
        }
    }

    public int GetFormationPriority()
    {
        switch (category)
        {
            case UnitCategory.Leader: return 100;
            case UnitCategory.Infantry: return 0;
            case UnitCategory.Cavalry: return 1;
            case UnitCategory.Siege: return 2;
            case UnitCategory.Archer: return 3;
            case UnitCategory.Villager: return 4;
        }

        return 10;
    }

    void UpdatePathLine()
    {
        if (!showPathLines || lineRenderer == null)
        {
            if (lineRenderer != null) lineRenderer.enabled = false;
            return;
        }

        if (path == null || pathIndex >= path.Count)
        {
            ClearPathLine();
            return;
        }

        int remaining = path.Count - pathIndex;
        lineRenderer.positionCount = remaining + 1;
        lineRenderer.enabled = true;

        lineRenderer.SetPosition(0, transform.position);
        for (int i = 0; i < remaining; i++)
        {
            lineRenderer.SetPosition(i + 1, path[pathIndex + i]);
        }
    }

    void ClearPathLine()
    {
        if (lineRenderer == null) return;
        lineRenderer.positionCount = 0;
        lineRenderer.enabled = false;
    }

    void SnapToNearestNodeCenter()
    {
        var gm = GridManager.Instance;
        if (gm == null) return;

        Node n = gm.NodeFromWorldPoint(simPosition);
        if (n == null) return;

        Vector3 center = n.centerPosition;
        BeginSmoothSnap(center, 0f);
    }

    public void ClearFormationAssignment()
    {
        formationId = 0;
        formationSlot = -1;
    }

    public void ClearCombatReservation()
    {
        holdReservation = false;
        if (NodeReservationSystem.Instance != null)
            NodeReservationSystem.Instance.ReleaseReservation(this);
    }

    public void HoldPositionAtReservedNode()
    {
        if (reservedNode == null) return;

        hasPath = false;
        path = null;
        pathIndex = 0;
        velocity = Vector2.zero;
        ClearPathLine();

        holdReservation = true;

        BeginSmoothSnap(reservedNode.centerPosition, 0f);
    }

    private void ApplyTint(SpriteRenderer sr, Color color)
    {
        if (sr == null) return;
        sr.color = color;

        var mat = sr.material;
        if (mat != null)
        {
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
        }
    }

    public bool IsAtReservedNodeCenter(float toleranceMultiplier = 0.18f)
    {
        if (reservedNode == null) return false;
        var gm = GridManager.Instance;
        if (gm == null) return false;
        float tol = gm.nodeDiameter * toleranceMultiplier;
        float d = Vector2.Distance(transform.position, reservedNode.centerPosition);
        return d <= tol;
    }

    public virtual void OnSelected()
    {
        // show selection indicator (kept simple; SelectionController also calls Show())
        selectionIndicator?.Show();
    }

    public virtual void OnDeselected()
    {
        // hide selection indicator
        selectionIndicator?.Hide();
    }
}