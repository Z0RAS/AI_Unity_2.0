using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(UnitAgent))]
public class UnitCombat : MonoBehaviour
{
    public float maxHealth = 40f;
    public float armor = 0f;
    public float attackDamage = 5f;
    public float attackRange = 1.2f;
    public float attackCooldown = 1.5f;
    public bool isRanged = false;
    public GameObject projectilePrefab;
    public float projectileSpeed = 8f;

    public float aggroRadius = 4f;

    private float currentHealth;
    private float cooldownTimer;
    private UnitCombat currentTarget;
    private GameObject currentTargetObject;
    private UnitAgent agent;
    private bool initialized = false;

    private float autoAggroSuppressTimer = 0f;
    private UnitStateMachine stateMachine;

    // Throttles & diagnostics (reduce console spam and avoid re-evaluating every frame)
    private float lastHandleTime = 0f;
    private readonly float handleThrottle = 0.18f; // seconds between heavy HandleCombat evaluations
    private float lastDebugTime = 0f;
    private readonly float debugThrottle = 0.5f; // seconds between debug logs

    // Pursuit control to avoid infinite chasing
    private Vector3 pursuitOrigin = Vector3.zero;
    private float pursuitLimit = 0f; // world units
    private const int DefaultPursuitNodes = 6;

    // Engagement lock to avoid ping-pong re-evaluation
    private bool isEngaging = false;
    private float engageExpireTime = 0f;
    private GameObject engagedTargetObj = null;
    private readonly float engageDuration = 2.0f; // seconds to hold engagement before re-evaluating

    public float CurrentHealth => currentHealth;
    public float MaxHealth => maxHealth;
    public event Action<float> OnHealthChanged;

    private void Awake()
    {
        currentHealth = maxHealth;
        agent = GetComponent<UnitAgent>();
        stateMachine = GetComponent<UnitStateMachine>();
        OnHealthChanged?.Invoke(currentHealth);
        initialized = true;
    }

    private void Update()
    {
        if (!initialized) return;
        cooldownTimer -= Time.deltaTime;
        if (autoAggroSuppressTimer > 0f) autoAggroSuppressTimer -= Time.deltaTime;
        var morale = GetComponent<MoraleComponent>();
        bool isBroken = morale != null && morale.IsBroken();

        // Safety: clear null targets (in case referenced objects got destroyed)
        if (currentTarget != null && currentTarget.gameObject == null) currentTarget = null;
        if (currentTargetObject != null && currentTargetObject.gameObject == null) currentTargetObject = null;

        if ((currentTarget != null || currentTargetObject != null) && !isBroken) HandleCombat();
        else if (!isBroken) AutoAggro();
    }

    private void Start() => Invoke(nameof(EnableCombat), 0.1f);
    void EnableCombat() => initialized = true;
    public void SuppressAutoAggro(float seconds) => autoAggroSuppressTimer = Mathf.Max(autoAggroSuppressTimer, seconds);

    bool IsSameTeam(UnitCombat other)
    {
        if (other == null) return false;
        var myOwner = agent != null ? agent.owner : null;
        var otherOwner = other.agent != null ? other.agent.owner : null;
        return myOwner == otherOwner;
    }

    PlayerEconomy GetOwnerFromObject(GameObject obj)
    {
        if (obj == null) return null;
        var ua = obj.GetComponent<UnitAgent>();
        if (ua != null) return ua.owner;
        var b = obj.GetComponent<Building>();
        if (b != null) return b.owner;
        var bc = obj.GetComponent<BuildingConstruction>();
        if (bc != null)
        {
            if (bc.assignedOwner != null) return bc.assignedOwner;
            var b2 = obj.GetComponent<Building>();
            if (b2 != null) return b2.owner;
            return null;
        }
        var drop = obj.GetComponent<DropOffBuilding>();
        if (drop != null) return drop.owner;
        return null;
    }

    bool IsSameTeam(GameObject otherObj)
    {
        if (otherObj == null) return false;
        var myOwner = agent != null ? agent.owner : null;
        var otherOwner = GetOwnerFromObject(otherObj);
        return myOwner != null && otherOwner != null && myOwner == otherOwner;
    }

    // ---------------------------------------------------------
    // CORE COMBAT: approach + attack
    // - Throttled to avoid per-frame churn
    // - Perform attacks only when attacker is settled at reserved node center.
    // - Don't clear reservation when agent has holdReservation (prevents ping-pong).
    // - Use an engagement lock so units do not constantly re-evaluate while approaching.
    // ---------------------------------------------------------
    void HandleCombat()
    {
        // throttle heavy work to avoid jitter and log spam
        if (Time.time - lastHandleTime < handleThrottle)
            return;
        lastHandleTime = Time.time;

        if (Time.time - lastDebugTime >= debugThrottle)
            lastDebugTime = Time.time;

        // Defensive: ensure agent reference exists
        if (agent == null) agent = GetComponent<UnitAgent>();
        if (agent == null) return;

        var gm = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;
        var pf = Pathfinding.Instance; // may be null in some test scenes — handle defensively

        // Resolve target transform; if neither target exists, nothing to do.
        Transform targetTransform = currentTarget != null ? currentTarget.transform :
                                    (currentTargetObject != null ? currentTargetObject.transform : null);
        if (targetTransform == null) return;
        if (gm == null) return;

        Node targetNode = gm.NodeFromWorldPoint(targetTransform.position);
        if (targetNode == null) return;

        var b = targetTransform.GetComponentInParent<Building>();
        var bc = targetTransform.GetComponentInParent<BuildingConstruction>();

        // If our reserved node is invalid/stale and we are not intentionally holding it, clear our reservation.
        try
        {
            if (agent != null && agent.reservedNode != null && !agent.holdReservation)
            {
                nrs?.ReleaseReservation(agent);
                agent.reservedNode = null;
            }
        }
        catch { }

        // If we have a unit target, enforce pursuit limit: stop chasing if target moved far away.
        if (currentTarget != null && pursuitLimit > 0f)
        {
            if (currentTarget.transform == null)
            {
                // target destroyed
                currentTarget = null;
                currentTargetObject = null;
                pursuitLimit = 0f;
                isEngaging = false;
                engagedTargetObj = null;
                return;
            }

            float distToTarget = Vector2.Distance(agent.transform.position, currentTarget.transform.position);
            if (distToTarget > pursuitLimit)
            {
                if (agent != null && !agent.holdReservation) agent.ClearCombatReservation();
                currentTarget = null;
                currentTargetObject = null;
                pursuitLimit = 0f;
                // also clear engagement
                isEngaging = false;
                engagedTargetObj = null;
                return;
            }
        }

        // If already holding and settled at reserved center, lock & attack and DO NOT re-evaluate.
        if (agent != null && agent.holdReservation && agent.reservedNode != null && agent.IsAtReservedNodeCenter(0.35f))
        {
            if (b != null || bc != null)
                currentTargetObject = targetTransform.gameObject;

            agent.HoldPositionAtReservedNode();

            if (cooldownTimer <= 0f)
            {
                cooldownTimer = attackCooldown;
                PerformAttack();
            }

            return;
        }

        // If engaging and still within engage window for the same target, don't reselect nodes; just wait for arrival.
        if (isEngaging)
        {
            if (Time.time < engageExpireTime && engagedTargetObj == targetTransform.gameObject)
                return;

            isEngaging = false;
            engagedTargetObj = null;
        }

        // BUILDING TARGET: reserve approach and only attack from reserved node center.
        if (b != null || bc != null)
        {
            Node chosen = null;

            // If we already have a reserved node, try to ensure ownership via ReserveNode (succeeds if already ours or free).
            if (agent != null && agent.reservedNode != null && nrs != null)
            {
                try
                {
                    if (nrs.ReserveNode(agent.reservedNode, agent))
                        chosen = agent.reservedNode;
                    else
                        chosen = null;
                }
                catch { chosen = null; }
            }

            if (chosen == null && nrs != null && targetNode != null)
            {
                // Ask reservation system to pick & reserve a good approach node.
                chosen = nrs.FindAndReserveBestNode(targetNode, agent, 6);
                if (chosen != null)
                    agent.holdReservation = true;
            }

            if (chosen == null && gm != null && targetNode != null)
                chosen = gm.FindClosestWalkableNode(targetNode.gridX, targetNode.gridY);

            if (chosen == null)
            {
                agent.Stop();
                currentTargetObject = targetTransform.gameObject;
                return;
            }

            // If chosen is not yet reserved for us (e.g. GridManager fallback), attempt to reserve it.
            if (nrs != null)
            {
                bool reservedForUs = false;
                try { reservedForUs = nrs.ReserveNode(chosen, agent); }
                catch { reservedForUs = false; }

                if (!reservedForUs)
                {
                    Node alt = null;
                    try { alt = nrs.FindAndReserveBestNode(chosen, agent, 3); } catch { alt = null; }
                    if (alt != null)
                    {
                        chosen = alt;
                        agent.holdReservation = true;
                    }
                    else
                    {
                        agent.SetDestinationToNode(chosen, true);
                        isEngaging = true;
                        engagedTargetObj = targetTransform.gameObject;
                        engageExpireTime = Time.time + engageDuration;
                        return;
                    }
                }
                else
                {
                    agent.holdReservation = true;
                }
            }
            else
            {
                agent.SetDestinationToNode(chosen, true);
                isEngaging = true;
                engagedTargetObj = targetTransform.gameObject;
                engageExpireTime = Time.time + engageDuration;
                return;
            }

            // require being settled at the reserved center to start attacking
            if (!agent.IsAtReservedNodeCenter(0.35f))
            {
                agent.SetDestinationToNode(chosen, true);
                isEngaging = true;
                engagedTargetObj = targetTransform.gameObject;
                engageExpireTime = Time.time + engageDuration;
                return;
            }

            // At center -> lock and attack
            currentTargetObject = targetTransform.gameObject;
            // avoid calling HoldPositionAtReservedNode here (UnitAgent already finished smooth snap that invoked this)
            agent.holdReservation = true;
            if (cooldownTimer <= 0f)
            {
                cooldownTimer = attackCooldown;
                PerformAttack();
            }
            return;
        }

        // UNIT target: reserve and attack from adjacent node center
        if (currentTarget != null)
        {
            if (currentTarget.transform == null)
            {
                currentTarget = null;
                return;
            }

            Node chosen = null;
            Node targetNodeUnit = gm.NodeFromWorldPoint(currentTarget.transform.position);
            if (targetNodeUnit == null) return;

            int tx = targetNodeUnit.gridX;
            int ty = targetNodeUnit.gridY;
            float bestLen = float.MaxValue;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    Node n = gm.GetNode(tx + dx, ty + dy);
                    if (n == null || !n.walkable) continue;
                    if (nrs != null && nrs.IsReserved(n)) continue;

                    // Defensive: require pathfinder instance
                    List<Vector3> path = null;
                    try
                    {
                        if (pf != null)
                            path = pf.FindPath(transform.position, n.centerPosition);
                    }
                    catch { path = null; }

                    if (path == null || path.Count == 0) continue;
                    float len = ComputePathLength(path);
                    if (len < bestLen) { bestLen = len; chosen = n; }
                }
            }

            var nrs2 = nrs;
            if (chosen == null && nrs2 != null)
                chosen = nrs2.FindAndReserveBestNode(targetNodeUnit, agent, 4);

            if (chosen == null)
                chosen = gm.FindClosestWalkableNode(targetNodeUnit.gridX, targetNodeUnit.gridY);

            if (chosen == null) return;

            if (nrs2 != null)
            {
                bool got = false;
                try { got = nrs2.ReserveNode(chosen, agent); } catch { got = false; }

                if (!got)
                {
                    Node alt = null;
                    try { alt = nrs2.FindAndReserveBestNode(chosen, agent, 3); } catch { alt = null; }
                    if (alt != null)
                    {
                        chosen = alt;
                        agent.holdReservation = true;
                    }
                    else
                    {
                        agent.SetDestinationToNode(chosen, true);
                        isEngaging = true;
                        // defensive assignment: target may be destroyed between checks
                        engagedTargetObj = (currentTarget != null && currentTarget.gameObject != null) ? currentTarget.gameObject : null;
                        engageExpireTime = Time.time + engageDuration;
                        return;
                    }
                }
                else
                {
                    agent.holdReservation = true;
                }
            }
            else
            {
                agent.SetDestinationToNode(chosen, true);
                isEngaging = true;
                // defensive assignment: target may be destroyed between checks
                engagedTargetObj = (currentTarget != null && currentTarget.gameObject != null) ? currentTarget.gameObject : null;
                engageExpireTime = Time.time + engageDuration;
                return;
            }

            // require settled at center before attacking
            if (!agent.IsAtReservedNodeCenter(0.35f))
            {
                agent.SetDestinationToNode(chosen, true);
                isEngaging = true;
                // defensive assignment: target may be destroyed between checks
                engagedTargetObj = (currentTarget != null && currentTarget.gameObject != null) ? currentTarget.gameObject : null;
                engageExpireTime = Time.time + engageDuration;
                return;
            }

            // Avoid invoking HoldPositionAtReservedNode here (UnitAgent.OnArrivedAtReservedNode already handled snapping).
            agent.holdReservation = true;
            if (cooldownTimer <= 0f)
            {
                cooldownTimer = attackCooldown;
                PerformAttack();
            }
            return;
        }
    }

    // Called by UnitAgent when smooth snap to reserved node finishes.
    public void OnArrivedAtReservedNode()
    {
        // defensive retrieval of agent
        if (agent == null) agent = GetComponent<UnitAgent>();
        if (agent == null) return;
        if (agent.reservedNode == null) return;
        var nrs = NodeReservationSystem.Instance;

        // best-effort: ensure reservation actually belongs to us by attempting ReserveNode (safe)
        if (nrs != null)
        {
            try
            {
                if (!nrs.ReserveNode(agent.reservedNode, agent))
                    return;
            }
            catch { return; }
        }

        if (currentTarget == null && currentTargetObject == null) return;

        // Do not call HoldPositionAtReservedNode to avoid re-entrant smooth-snap loops.
        // Just mark the agent as holding reservation so combat logic treats it as settled.
        agent.holdReservation = true;

        // clear engage lock
        isEngaging = false;
        engagedTargetObj = null;
        engageExpireTime = 0f;

        if (cooldownTimer <= 0f)
        {
            cooldownTimer = attackCooldown;
            PerformAttack();
        }
    }

    private float ComputePathLength(List<Vector3> path)
    {
        if (path == null || path.Count < 2) return float.MaxValue;
        float len = 0f;
        for (int i = 1; i < path.Count; i++)
            len += Vector3.Distance(path[i - 1], path[i]);
        return len;
    }

    void PerformAttack()
    {
        // Defensive: ensure we have a valid target at time of firing / melee
        if (currentTarget == null && currentTargetObject == null) return;

        if (currentTarget != null)
        {
            if (IsSameTeam(currentTarget)) return;

            if (isRanged && projectilePrefab != null)
            {
                if (currentTarget.transform != null)
                {
                    GameObject proj = Instantiate(projectilePrefab, transform.position, Quaternion.identity);
                    Projectile p = proj.GetComponent<Projectile>();
                    p.Init(currentTarget.transform, attackDamage, projectileSpeed);
                }
            }
            else
            {
                currentTarget.TakeDamage(attackDamage);
            }

            return;
        }

        if (currentTargetObject != null)
        {
            UnitCombat uc = currentTargetObject.GetComponent<UnitCombat>() ?? currentTargetObject.GetComponentInParent<UnitCombat>();
            if (uc != null)
            {
                if (IsSameTeam(uc)) return;

                if (isRanged && projectilePrefab != null)
                {
                    if (uc.transform != null)
                    {
                        GameObject proj = Instantiate(projectilePrefab, transform.position, Quaternion.identity);
                        Projectile p = proj.GetComponent<Projectile>();
                        p.Init(uc.transform, attackDamage, projectileSpeed);
                    }
                }
                else
                {
                    uc.TakeDamage(attackDamage);
                }
            }
        }
    }

    private void AutoAggro()
    {
        // Defensive: ensure agent reference
        if (agent == null) agent = GetComponent<UnitAgent>();
        if (agent == null) return;

        // Find nearest enemy UnitCombat within aggroRadius
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, aggroRadius);
        float bestSqr = float.MaxValue;
        UnitCombat bestTarget = null;

        foreach (var hit in hits)
        {
            if (hit == null) continue;
            var uc = hit.GetComponent<UnitCombat>() ?? hit.GetComponentInParent<UnitCombat>();
            if (uc == null || uc == this) continue;

            // Skip same-team units
            try { if (IsSameTeam(uc)) continue; } catch { continue; }

            float dsq = (uc.transform.position - transform.position).sqrMagnitude;
            if (dsq < bestSqr)
            {
                bestSqr = dsq;
                bestTarget = uc;
            }
        }

        if (bestTarget != null)
        {
            currentTarget = bestTarget;
            currentTargetObject = null;
            // set a reasonable pursuit limit so units don't chase forever
            pursuitOrigin = transform.position;
            pursuitLimit = Mathf.Max(4f, aggroRadius * 2f);
        }
    }

    public void TakeDamage(float amount)
    {
        // Defensive: ensure agent reference
        if (agent == null) agent = GetComponent<UnitAgent>();
        if (amount <= 0f) return;

        // Apply armor reduction (simple flat subtraction)
        float actual = Mathf.Max(0f, amount - armor);

        currentHealth -= actual;
        OnHealthChanged?.Invoke(currentHealth);

        // Optional: brief hit feedback could go here (particles/sound/etc.)

        // Death handling
        if (currentHealth <= 0f)
        {
            // Clear reservations / combat state so other systems don't hold stale refs.
            try { agent?.ClearCombatReservation(); } catch { }
            try { NodeReservationSystem.Instance?.ReleaseReservation(agent); } catch { }

            // Ensure unit is removed / cleaned up.
            try { Destroy(agent != null ? agent.gameObject : this.gameObject); }
            catch { Destroy(this.gameObject); }
        }
    }

    // Add these methods inside the UnitCombat class (near other public command APIs)

    public void SetTarget(UnitCombat uc)
    {
        // Defensive assignment for unit-target
        currentTarget = uc;
        currentTargetObject = null;

        // Reset engage state so HandleCombat will evaluate this target immediately.
        isEngaging = false;
        engagedTargetObj = null;
        engageExpireTime = 0f;

        // Pursuit safeguard and ensure agent active
        pursuitOrigin = transform.position;
        pursuitLimit = Mathf.Max(4f, aggroRadius * 2f);

        if (agent == null) agent = GetComponent<UnitAgent>();
        if (agent != null) agent.SetIdle(false);
    }

    public void SetTarget(GameObject target)
    {
        // Defensive assignment for object-target
        currentTargetObject = target;
        currentTarget = null;

        // Reset engage State
        isEngaging = false;
        engagedTargetObj = null;
        engageExpireTime = 0f;

        pursuitOrigin = transform.position;
        pursuitLimit = Mathf.Max(4f, aggroRadius * 2f);

        if (agent == null) agent = GetComponent<UnitAgent>();
        if (agent != null) agent.SetIdle(false);
    }

    public void SetTargetKeepReservation(UnitCombat uc)
    {
        if (uc == null)
        {
            currentTarget = null;
            return;
        }

        currentTarget = uc;
        currentTargetObject = null;

        // Do not change reservation state here — caller expects to keep reservation semantics.
        // Reset engagement so HandleCombat evaluates the new target promptly.
        isEngaging = false;
        engagedTargetObj = null;
        engageExpireTime = 0f;

        pursuitOrigin = transform.position;
        pursuitLimit = Mathf.Max(4f, aggroRadius * 2f);

        if (agent == null) agent = GetComponent<UnitAgent>();
        if (agent != null) agent.SetIdle(false);
    }

    public void SetTargetKeepReservation(GameObject target)
    {
        if (target == null)
        {
            currentTargetObject = null;
            return;
        }

        currentTargetObject = target;
        currentTarget = null;

        isEngaging = false;
        engagedTargetObj = null;
        engageExpireTime = 0f;

        pursuitOrigin = transform.position;
        pursuitLimit = Mathf.Max(4f, aggroRadius * 2f);

        if (agent == null) agent = GetComponent<UnitAgent>();
        if (agent != null) agent.SetIdle(false);
    }

    // Add these overloads inside the UnitCombat class (near other SetTarget methods).
    public void SetTargetNoNotify(UnitCombat uc)
    {
        // Assign target quietly without changing reservation/engagement state.
        currentTarget = uc;
        currentTargetObject = null;
        // Keep existing engage/reservation state intact.
    }

    public void SetTargetNoNotify(GameObject target)
    {
        // Assign target quietly without changing reservation/engagement state.
        currentTargetObject = target;
        currentTarget = null;
        // Keep existing engage/reservation state intact.
    }
}