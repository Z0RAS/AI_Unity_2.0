using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(AutoBuildRecruiter), typeof(ConstructionFinalizer))]
public class BuildingConstruction : MonoBehaviour
{
    public BuildingData data;
    public float currentHealth = 1f;
    public Vector2Int size;
    public float buildSpeedPerVillager = 8f;
    public SpriteRenderer sprite;
    public Canvas progressCanvas;
    public UnityEngine.UI.Image progressFill;
    public bool isGhost = false;
    public bool isUnderConstruction = false;
    public bool isFinished = false;

    // Optional owner hint so only workers belonging to the same PlayerEconomy are asked to build.
    public PlayerEconomy assignedOwner;
    // Backwards-compatible property used by other systems
    public PlayerEconomy AssignedOwner => assignedOwner;

    // Backwards-compatible property expected by callers
    public Vector2Int Size => size;

    // track actual unique builders (prevents double-counting when villagers call AddBuilder each frame)
    private HashSet<UnitAgent> buildersSet = new HashSet<UnitAgent>();

    // Radius used to detect nearby builders automatically (world units)
    public float buildDetectionRadius = 1.6f;

    // optional multiplier you can use to speed up AI builds (1 = normal)
    public float aiBuildSpeedMultiplier = 1f;

    float transparency = 0.1f;

    // Exposed for helpers
    [HideInInspector] public Node buildingNode; // building center node
    // Backwards-compatible property expected by callers (ConstructionFinalizer, others)
    public Node BuildingNode { get => buildingNode; set => buildingNode = value; }

    // Auto-build recruitment settings (kept for compatibility; used by AutoBuildRecruiter)
    [Header("Auto-build recruitment")]
    [Tooltip("Max idle villagers to auto-recruit when construction appears (will be clamped by footprint).")]
    public int maxAutoBuilders = 4;
    [Tooltip("Delay (seconds) between recruit commands to spread CPU work")]
    public float recruitStagger = 0.06f;

    private AutoBuildRecruiter recruiter;
    private ConstructionFinalizer finalizer;

    // ghost delayed alert (tick-driven)
    private bool delayedAlertScheduled = false;

    // flash damage tick-driven
    private bool flashActive = false;
    private float flashElapsed = 0f;
    private float flashHold = 0.12f;

    private void Awake()
    {
        if (GridManager.Instance != null)
            buildingNode = GridManager.Instance.NodeFromWorldPoint(transform.position);

        recruiter = GetComponent<AutoBuildRecruiter>();
        if (recruiter == null) recruiter = gameObject.AddComponent<AutoBuildRecruiter>();

        finalizer = GetComponent<ConstructionFinalizer>();
        if (finalizer == null) finalizer = gameObject.AddComponent<ConstructionFinalizer>();
    }

    void OnEnable()
    {
        if (TimeController.Instance != null)
            TimeController.OnTick += OnTick;
    }

    void OnDisable()
    {
        if (TimeController.Instance != null)
            TimeController.OnTick -= OnTick;
    }

    void Start()
    {
        if (buildingNode == null && GridManager.Instance != null)
            buildingNode = GridManager.Instance.NodeFromWorldPoint(transform.position);

        if (sprite != null)
            sprite.color = new Color(1f, 1f, 1f, transparency);

        if (progressCanvas != null)
        {
            progressCanvas.enabled = true;
            float maxH = (data != null && data.maxHealth > 0f) ? data.maxHealth : 1f;
            progressFill.fillAmount = Mathf.Clamp01(currentHealth / maxH);
        }

        // If this instance is created as a ghost (placement preview), notify nearby idle villagers once.
        // Delay one tick so buildingNode and villagers have a chance to initialize.
        if (isGhost)
        {
            try { SetCollidersAsTrigger(true); } catch { }
            delayedAlertScheduled = true;
        }

        // If construction has already started prior to Start(), make sure colliders are non-blocking
        if (isUnderConstruction)
        {
            try { SetCollidersAsTrigger(true); } catch { }
        }
    }

    // tick callback - use TimeController ticks for build progress and visual timers
    private void OnTick(float dt)
    {
        // delayed one-tick ghost alert
        if (delayedAlertScheduled)
        {
            delayedAlertScheduled = false;
            try { AlertNearbyIdleVillagers(); } catch { }
        }

        // flash visual update
        if (flashActive)
        {
            flashElapsed += dt;
            if (flashElapsed >= flashHold)
            {
                // restore sprite color (white) and stop flash
                if (sprite != null) sprite.color = Color.white;
                flashActive = false;
            }
        }

        // building progress
        if (isFinished) return;
        if (!isUnderConstruction) return;

        if (buildingNode == null && GridManager.Instance != null)
            buildingNode = GridManager.Instance.NodeFromWorldPoint(transform.position);

        Vector3 center = buildingNode != null ? buildingNode.centerPosition : transform.position;

        var gm = GridManager.Instance;

        // compute footprint start indices & bounds early so we can exclude inside-footprint units
        int innerMinX = int.MinValue, innerMinY = int.MinValue, innerMaxX = int.MaxValue, innerMaxY = int.MaxValue;
        if (gm != null)
        {
            Vector2Int start = gm.GetStartIndicesForCenteredFootprint(transform.position, Size);
            innerMinX = start.x;
            innerMinY = start.y;
            innerMaxX = start.x + Size.x - 1;
            innerMaxY = start.y + Size.y - 1;
        }

        // auto-detect nearby owner-matching villagers (legacy behavior removed for center counting).
        // Only consider villagers that are outside the footprint (perimeter/approach nodes) or explicitly assigned.
        Collider2D[] nearby = Physics2D.OverlapCircleAll(center, buildDetectionRadius);
        var nearbyAgents = new HashSet<UnitAgent>();
        if (nearby != null && nearby.Length > 0)
        {
            foreach (var c in nearby)
            {
                if (c == null) continue;
                UnitAgent ua = c.GetComponent<UnitAgent>();
                if (ua == null) continue;

                // IMPORTANT: only count Villagers as builders
                if (ua.category != UnitCategory.Villager) continue;

                if (assignedOwner != null && ua.owner != assignedOwner)
                    continue;

                // If the unit's current node is inside the footprint, skip it.
                bool insideFootprint = false;
                try
                {
                    if (gm != null)
                    {
                        var node = gm.NodeFromWorldPoint(ua.transform.position);
                        if (node != null)
                        {
                            if (node.gridX >= innerMinX && node.gridX <= innerMaxX && node.gridY >= innerMinY && node.gridY <= innerMaxY)
                                insideFootprint = true;
                        }
                    }
                }
                catch { insideFootprint = false; }

                if (insideFootprint)
                {
                    // Do NOT count villagers standing inside the footprint/center as active builders.
                    // Those units may be moving through or were not properly reserved — recruiter reserves outside nodes.
                    continue;
                }

                // unit is outside footprint and near building => consider it as a nearby agent (perimeter)
                nearbyAgents.Add(ua);

                if (!buildersSet.Contains(ua))
                    buildersSet.Add(ua);
            }
        }

        // --- Also include villagers that are holding/reserved perimeter approach nodes ---
        // and villagers explicitly assigned to this construction (player or recruiter).
        try
        {
            if (gm != null)
            {
                // margin: expand sufficiently to include recruiter/reserved approach nodes
                int margin = Mathf.Clamp(Mathf.Max(Size.x, Size.y) + 4, 1, 8);

                // Use registered villager list (cheaper & deterministic than FindObjects in heavy scenes)
                var allVillagers = VillagerTaskSystem.AllRegisteredVillagers;
                if (allVillagers != null)
                {
                    foreach (var vill in allVillagers)
                    {
                        if (vill == null) continue;
                        // only consider villagers assigned to this construction OR villagers with a reservation near footprint
                        bool assignedThis = (vill.AssignedConstruction == this);
                        var ua = vill.GetComponent<UnitAgent>();
                        if (ua == null) continue;
                        if (ua.category != UnitCategory.Villager) continue;
                        if (assignedOwner != null && ua.owner != assignedOwner) continue;

                        bool counted = false;

                        // 1) If villager explicitly assigned to this construction, prefer their reserved node center presence
                        if (assignedThis)
                        {
                            if (ua.reservedNode != null && ua.holdReservation)
                            {
                                bool atReservedCenter = false;
                                try { atReservedCenter = ua.IsAtReservedNodeCenter(0.25f); } catch { atReservedCenter = false; }

                                if (!atReservedCenter)
                                {
                                    try
                                    {
                                        float dist = Vector2.Distance(ua.transform.position, ua.reservedNode.centerPosition);
                                        float tol = (gm != null) ? gm.nodeDiameter * 0.9f : 0.9f;
                                        if (dist <= tol) atReservedCenter = true;
                                    }
                                    catch { }
                                }

                                if (atReservedCenter)
                                {
                                    try { AddBuilder(ua); } catch { }
                                    nearbyAgents.Add(ua);
                                    counted = true;
                                }
                            }
                            else
                            {
                                // If assigned but no reservation, count them only if they stand outside footprint near the building edge
                                try
                                {
                                    float distToCenter = Vector2.Distance(ua.transform.position, center);
                                    float halfFootprint = Mathf.Max(Size.x, Size.y) * 0.5f * gm.nodeDiameter;
                                    float edgeTol = gm.nodeDiameter * 1.2f;
                                    if (distToCenter <= halfFootprint + edgeTol && !IsPositionInsideFootprint(ua.transform.position, gm, innerMinX, innerMinY, innerMaxX, innerMaxY))
                                    {
                                        try { AddBuilder(ua); } catch { }
                                        nearbyAgents.Add(ua);
                                        counted = true;
                                    }
                                }
                                catch { }
                            }
                        }

                        if (counted) continue;

                        // 2) If villager has reservedNode near footprint perimeter and is at reserved center -> count
                        var rn = ua.reservedNode;
                        if (rn != null)
                        {
                            if (rn.gridX >= innerMinX - margin && rn.gridX <= innerMaxX + margin &&
                                rn.gridY >= innerMinY - margin && rn.gridY <= innerMaxY + margin)
                            {
                                bool inside = (rn.gridX >= innerMinX && rn.gridX <= innerMaxX &&
                                               rn.gridY >= innerMinY && rn.gridY <= innerMaxY);
                                if (!inside)
                                {
                                    bool atReservedCenter = false;
                                    try { atReservedCenter = ua.IsAtReservedNodeCenter(0.25f) && ua.holdReservation; } catch { atReservedCenter = false; }

                                    if (!atReservedCenter && ua.reservedNode != null)
                                    {
                                        try
                                        {
                                            float tol2 = gm.nodeDiameter * 1.0f;
                                            float dist2 = Vector2.Distance(ua.transform.position, ua.reservedNode.centerPosition);
                                            if (dist2 <= tol2 && (ua.holdReservation || ua.reservedNode != null))
                                                atReservedCenter = true;
                                        }
                                        catch { }
                                    }

                                    if (atReservedCenter)
                                    {
                                        try { AddBuilder(ua); } catch { }
                                        nearbyAgents.Add(ua);
                                        continue;
                                    }

                                    // permissive fallback: if villager is near building edge (outside footprint) count them
                                    try
                                    {
                                        float edgeTol = gm.nodeDiameter * 1.2f;
                                        float distToCenter = Vector2.Distance(ua.transform.position, center);
                                        float halfFootprint = Mathf.Max(Size.x, Size.y) * 0.5f * gm.nodeDiameter;
                                        if (distToCenter <= halfFootprint + edgeTol && !IsPositionInsideFootprint(ua.transform.position, gm, innerMinX, innerMinY, innerMaxX, innerMaxY))
                                        {
                                            try { AddBuilder(ua); } catch { }
                                            nearbyAgents.Add(ua);
                                            continue;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { /* best-effort only */ }

        // Remove builders that moved away (keep those still present in nearbyAgents)
        var toRemove = new List<UnitAgent>();
        foreach (var b in buildersSet)
            if (!nearbyAgents.Contains(b))
                toRemove.Add(b);

        foreach (var r in toRemove)
            buildersSet.Remove(r);

        int builderCount = buildersSet.Count;
        float perWorkerHealthPerSecond = buildSpeedPerVillager;
        if (data != null && data.buildTime > 0f && data.maxHealth > 0)
        {
            perWorkerHealthPerSecond = (float)data.maxHealth / data.buildTime;
        }

        if (builderCount > 0)
        {
            float delta = builderCount * perWorkerHealthPerSecond * aiBuildSpeedMultiplier * dt;
            currentHealth += delta;
        }

        float t = 1f;
        if (data != null && data.maxHealth > 0f)
            t = currentHealth / data.maxHealth;

        if (progressCanvas != null)
            progressFill.fillAmount = Mathf.Clamp01(t);

        if (sprite != null)
        {
            transparency = Mathf.Lerp(0.1f, 1f, Mathf.Clamp01(t));
            sprite.color = new Color(1f, 1f, 1f, transparency);
        }

        // Always check for completion, even if no builders are present
        if (data != null && currentHealth >= data.maxHealth)
            finalizer.StartFinalization();
        else if (data == null && currentHealth >= data.maxHealth)
            finalizer.StartFinalization();
    }

    // Toggle all child Collider2D components to be triggers while under construction/ghost.
    // This prevents the construction/ghost from physically blocking units while they are moved out.
    public void SetCollidersAsTrigger(bool makeTrigger)
    {
        var cols = GetComponentsInChildren<Collider2D>(true);
        if (cols == null) return;
        foreach (var c in cols)
        {
            try { c.isTrigger = makeTrigger; } catch { }
        }
    }

    // idempotent builder registration (kept for compatibility with explicit villager calls)
    public void AddBuilder(UnitAgent agent)
    {
        if (agent == null) return;
        if (!isUnderConstruction) return;

        if (!buildersSet.Contains(agent))
        {
            buildersSet.Add(agent);

            try
            {
                // Ensure the agent holds its reservation and is snapped to the reserved node center.
                // Prefer the agent's reservedNode if present; otherwise try to give it a reservation near the building.
                try
                {
                    var nrs = NodeReservationSystem.Instance;
                    if (nrs != null && agent.reservedNode == null)
                    {
                        // attempt to claim a nearby outside node (best-effort)
                        var gm = GridManager.Instance;
                        if (gm != null)
                        {
                            Vector2Int start = gm.GetStartIndicesForCenteredFootprint(transform.position, Size);
                            Node centerNode = gm.GetNode(start.x + Size.x / 2, start.y + Size.y / 2);
                            if (centerNode != null)
                            {
                                var pref = nrs.FindAndReserveBestNode(centerNode, agent, Mathf.Max(3, Size.x));
                                if (pref != null)
                                    agent.reservedNode = pref;
                            }
                        }
                    }
                }
                catch { }

                // remember reserved node as lastAssignedNode (used by finalizer)
                try { agent.lastAssignedNode = agent.reservedNode; } catch { }
                // ensure agent holds the reservation and snaps to center
                try { agent.holdReservation = true; } catch { }
                try { agent.HoldPositionAtReservedNode(); } catch { }
            }
            catch { }
        }
    }

    public void RemoveBuilder(UnitAgent agent)
    {
        if (agent == null) return;

        if (buildersSet.Remove(agent))
        {
        }
    }

    // Public facade retained so external callers can still trigger recruitment.
    public void AlertNearbyIdleVillagers()
    {
        if (recruiter != null) recruiter.AlertNearbyIdleVillagers();
    }

    // Called by finalizer
    public void ClearBuilders()
    {
        buildersSet.Clear();
    }

    // Expose current builders as a snapshot list for external callers (finalizer uses this).
    public List<UnitAgent> GetBuilders()
    {
        return new List<UnitAgent>(buildersSet);
    }

    // Begin building (used by AI spawn and placement controllers)
    public void BeginConstruction()
    {
        isGhost = false;
        isUnderConstruction = true;
        isFinished = false;

        if (recruiter != null)
        {
            try { recruiter.ResetRecruitment(); } catch { }
            try { recruiter.AlertNearbyIdleVillagers(); } catch { }
        }

        if (sprite != null)
            sprite.color = new Color(1f, 1f, 1f, 0.1f);

        if (progressCanvas != null)
            progressCanvas.enabled = true;

        currentHealth = 0f;
        try { SetCollidersAsTrigger(true); } catch { }
    }

    // Apply damage to an in-progress construction (used by combat/projectiles).
    public void TakeDamage(float amount)
    {
        float effective = Mathf.Max(1f, amount);
        currentHealth -= effective;

        if (progressFill != null)
        {
            float maxH = (data != null && data.maxHealth > 0f) ? data.maxHealth : 1f;
            progressFill.fillAmount = Mathf.Clamp01(currentHealth / maxH);
        }

        // Visual feedback: tint sprite briefly (simple, non-blocking)
        if (sprite != null)
            BeginFlashConstructionDamage();

        if (currentHealth <= 0f)
        {
            Destroy(gameObject);
        }
    }

    public void BeginFlashConstructionDamage()
    {
        if (sprite == null) return;
        flashActive = true;
        flashElapsed = 0f;
        flashHold = 0.12f;
        sprite.color = Color.red;
    }

    // Helper to determine if a world position falls inside the current footprint grid rect
    private bool IsPositionInsideFootprint(Vector3 worldPos, GridManager gm, int minX, int minY, int maxX, int maxY)
    {
        if (gm == null) return false;
        try
        {
            var n = gm.NodeFromWorldPoint(worldPos);
            if (n == null) return false;
            return (n.gridX >= minX && n.gridX <= maxX && n.gridY >= minY && n.gridY <= maxY);
        }
        catch { return false; }
    }
}