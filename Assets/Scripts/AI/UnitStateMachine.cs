using System;
using UnityEngine;

// Lightweight state machine for UnitAgent + UnitCombat integration.
// Public API: EnterIdleState, EnterMoveToNode, EnterAttackState
[RequireComponent(typeof(UnitAgent))]
public class UnitStateMachine : MonoBehaviour
{
    internal UnitAgent agent;
    internal UnitCombat combat;
    internal MoraleComponent morale;
    private IUnitState currentState;

    [Header("Broken / Flee settings")]
    [Tooltip("How long the unit will flee immediately after morale breaks before selecting a broken behaviour.")]
    public float brokenInitialFleeDuration = 2f;

    private void Awake()
    {
        agent = GetComponent<UnitAgent>();
        combat = GetComponent<UnitCombat>();
        morale = GetComponent<MoraleComponent>();

        SetState(new IdleState(this));

        if (morale != null)
        {
            morale.OnBroken += OnMoraleBroken;
            morale.OnRecovered += OnMoraleRecovered;
        }
    }

    private void OnDestroy()
    {
        if (morale != null)
        {
            morale.OnBroken -= OnMoraleBroken;
            morale.OnRecovered -= OnMoraleRecovered;
        }
    }

    private void Update()
    {
        // If morale is broken ensure we are fleeing/acting accordingly (defensive check)
        if (morale != null && morale.IsBroken())
        {
            // if not already in a broken-state, OnMoraleBroken will have handled the switch.
        }

        currentState?.Tick();
    }

    private void OnMoraleBroken()
    {
        ClearCombatTarget();
        SetState(new InitialFleeState(this, brokenInitialFleeDuration));
    }

    private void OnMoraleRecovered()
    {
        EnterIdleState();
    }

    // Clear any existing combat target without notifying FSM (prevents recursion)
    private void ClearCombatTarget()
    {
        combat?.SetTargetNoNotify((GameObject)null);
    }

    // Called after the initial forced flee to choose the broken behaviour
    internal void ChooseBrokenBehaviour()
    {
        float r = UnityEngine.Random.value;
        if (r < 0.33f)
        {
            EnterFleeState(ComputeFleeDirection());
        }
        else if (r < 0.66f)
        {
            // try to attack a nearby enemy unit first, then building
            UnitCombat[] allUnits = UnityEngine.Object.FindObjectsOfType<UnitCombat>();
            UnitCombat pick = null;
            float best = float.MaxValue;
            foreach (var u in allUnits)
            {
                if (u == null) continue;
                // skip own-team
                var ua = u.GetComponent<UnitAgent>();
                if (ua != null && ua.owner == agent.owner) continue;
                float d = (u.transform.position - transform.position).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    pick = u;
                }
            }

            if (pick != null)
            {
                EnterAttackState(pick);
            }
            else
            {
                // find building
                var builds = UnityEngine.Object.FindObjectsOfType<Building>();
                GameObject pickB = null;
                best = float.MaxValue;
                foreach (var b in builds)
                {
                    if (b == null) continue;
                    if (b.owner == agent.owner) continue;
                    float d = (b.transform.position - transform.position).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        pickB = b.gameObject;
                    }
                }
                if (pickB != null)
                {
                    EnterGenericAttackState(pickB);
                }
                else
                {
                    ClearCombatTarget();
                    EnterWanderState();
                }
            }
        }
        else
        {
            ClearCombatTarget();
            EnterWanderState();
        }
    }

    public void SetState(IUnitState newState)
    {
        currentState?.Exit();
        currentState = newState;
        currentState?.Enter();
    }

    public void EnterIdleState() => SetState(new IdleState(this));
    public void EnterMoveToNode(Node node) => SetState(new MoveToNodeState(this, node));
    public void EnterAttackState(UnitCombat target) => SetState(new AttackState(this, target));
    public void EnterFleeState(Vector3 dir) => SetState(new FleeState(this, dir));

    // New helpers
    public void EnterGenericAttackState(GameObject target) => SetState(new GenericAttackState(this, target));
    public void EnterWanderState() => SetState(new WanderState(this));

    // Internal helpers used by states
    public void MoveToNode(Node node)
    {
        // If morale is broken, use the force-move variant which bypasses the SetDestination morale check.
        if (morale != null && morale.IsBroken())
            agent.ForceSetDestinationToNode(node);
        else
            agent.SetDestinationToNode(node);
    }
    public void Stop() => agent.Stop();

    // Important: FSM must set combat target without re-triggering FSM notifications.
    // Use the no-notify variant to avoid recursion between UnitCombat.SetTarget -> FSM.EnterAttackState -> FSM.AttackTarget -> UnitCombat.SetTarget
    public void AttackTarget(UnitCombat target) => combat?.SetTargetNoNotify(target);

    // Compute a safe flee direction away from nearest enemy or random if none
    private Vector3 ComputeFleeDirection()
    {
        float searchR = 6f;
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, searchR);

        Transform nearestEnemy = null;
        float best = float.MaxValue;

        var myOwner = agent != null ? agent.owner : null;

        foreach (var h in hits)
        {
            if (h == null) continue;
            var ua = h.GetComponent<UnitAgent>();
            if (ua == null) continue;
            if (ua.owner == myOwner) continue; // skip allies
            float d = (ua.transform.position - transform.position).sqrMagnitude;
            if (d < best)
            {
                best = d;
                nearestEnemy = ua.transform;
            }
        }

        if (nearestEnemy != null)
        {
            return transform.position - nearestEnemy.position;
        }

        // fallback random
        return UnityEngine.Random.insideUnitCircle.normalized;
    }

    // State interface
    public interface IUnitState
    {
        void Enter();
        void Tick();
        void Exit();
    }

    // Idle
    private class IdleState : IUnitState
    {
        protected UnitStateMachine sm;
        public IdleState(UnitStateMachine sm) { this.sm = sm; }
        public virtual void Enter() { /* idle entry */ }
        public virtual void Tick() { /* idle tick - extend if needed */ }
        public virtual void Exit() { }
    }

    // Move to node state
    private class MoveToNodeState : IdleState
    {
        private Node dest;
        public MoveToNodeState(UnitStateMachine sm, Node dest) : base(sm) { this.dest = dest; }
        public override void Enter()
        {
            if (dest != null) sm.MoveToNode(dest);
            else sm.EnterIdleState();
        }
        public override void Tick()
        {
            if (dest == null) { sm.EnterIdleState(); return; }
            float d = Vector2.Distance(sm.transform.position, dest.centerPosition);
            if (d < 0.4f) sm.EnterIdleState();
        }
    }

    // Attack state (unit targets)
    private class AttackState : IdleState
    {
        private UnitCombat target;
        public AttackState(UnitStateMachine sm, UnitCombat target) : base(sm) { this.target = target; }
        public override void Enter()
        {
            if (target == null) { sm.EnterIdleState(); return; }
            // set the combat target without causing the FSM notification loop
            sm.AttackTarget(target);
        }
        public override void Tick()
        {
            if (target == null) { sm.EnterIdleState(); return; }

            // If morale broken - switch to random broken behaviour (handled by morale callbacks)
            if (sm.morale != null && sm.morale.IsBroken())
            {
                sm.OnMoraleBroken();
                return;
            }

            // rely on UnitCombat to approach/attack; distance checks could be added
        }
    }

    // Generic attack state (GameObject targets: building/construction, etc.)
    private class GenericAttackState : IdleState
    {
        private GameObject targetObj;
        public GenericAttackState(UnitStateMachine sm, GameObject target) : base(sm) { this.targetObj = target; }
        public override void Enter()
        {
            if (targetObj == null) { sm.EnterIdleState(); return; }

            // set generic target without notify to avoid recursion
            sm.combat?.SetTargetNoNotify(targetObj);
        }
        public override void Tick()
        {
            if (targetObj == null) { sm.EnterIdleState(); return; }

            // If morale broken - switch behaviour
            if (sm.morale != null && sm.morale.IsBroken())
            {
                sm.OnMoraleBroken();
                return;
            }
        }
    }

    // Wander state - move to random nearby nodes while morale broken
    private class WanderState : IdleState
    {
        float timer = 0f;
        Vector3 lastTarget;
        public WanderState(UnitStateMachine sm) : base(sm) { }
        public override void Enter()
        {
            // clear any combat target so the unit stops attacking
            sm.combat?.SetTargetNoNotify((GameObject)null);
            PickNewWanderTarget();
        }
        public override void Tick()
        {
            if (sm.morale != null && !sm.morale.IsBroken())
            {
                sm.EnterIdleState();
                return;
            }

            timer -= Time.deltaTime;
            if (timer <= 0f)
            {
                PickNewWanderTarget();
            }
        }
        void PickNewWanderTarget()
        {
            timer = UnityEngine.Random.Range(2f, 5f);
            Vector2 rnd = UnityEngine.Random.insideUnitCircle * 3f;
            Vector3 pos = sm.transform.position + new Vector3(rnd.x, rnd.y, 0f);
            Node n = GridManager.Instance != null ? GridManager.Instance.NodeFromWorldPoint(pos) : null;
            if (n != null)
                sm.MoveToNode(n);
            lastTarget = pos;
        }
        public override void Exit()
        {
            // nothing special
        }
    }

    // Flee state (example)
    private class FleeState : IdleState
    {
        private Vector3 dir;
        public FleeState(UnitStateMachine sm, Vector3 dir) : base(sm) { this.dir = dir; }
        public override void Enter()
        {
            // clear any combat target so the unit stops attacking
            sm.combat?.SetTargetNoNotify((GameObject)null);

            var grid = GridManager.Instance;
            if (grid != null)
            {
                Node n = grid.NodeFromWorldPoint(sm.transform.position + dir.normalized * 3f);
                if (n != null) sm.MoveToNode(n);
                else sm.EnterIdleState();
            }
            else sm.EnterIdleState();
        }
        public override void Tick()
        {
            // while broken, keep fleeing; once recovered, return to idle
            if (sm.morale != null && !sm.morale.IsBroken())
            {
                sm.EnterIdleState();
            }
        }
    }

    // Initial forced flee state executed immediately when morale breaks.
    // After the timer expires, the FSM chooses one of the broken behaviours.
    private class InitialFleeState : IdleState
    {
        float timer;
        float duration;
        Vector3 fleeDir;

        public InitialFleeState(UnitStateMachine sm, float duration) : base(sm)
        {
            this.duration = Mathf.Max(0.1f, duration);
        }

        public override void Enter()
        {
            // clear combat target and start running away immediately
            sm.combat?.SetTargetNoNotify((GameObject)null);

            fleeDir = sm.ComputeFleeDirection();
            timer = duration;

            var grid = GridManager.Instance;
            if (grid != null)
            {
                // Try a few fallbacks to guarantee a flee destination:
                // 1) preferred node at a point in fleeDir
                Node n = grid.NodeFromWorldPoint(sm.transform.position + fleeDir.normalized * 3f);

                // 2) if returned node is null or not walkable, try GridManager helpers
                if (n == null)
                {
                    // try find closest world node around the flee point
                    Node fallbackCenter = grid.NodeFromWorldPoint(sm.transform.position + fleeDir.normalized * 2f);
                    if (fallbackCenter != null)
                        n = grid.GetClosestWalkableNode(fallbackCenter);
                }
                else if (!n.walkable)
                {
                    n = grid.GetClosestWalkableNode(n);
                }

                // 3) final fallback: search outward from current position to any walkable
                if (n == null)
                {
                    Node myNode = grid.NodeFromWorldPoint(sm.transform.position);
                    if (myNode != null)
                        n = grid.GetClosestWalkableNode(myNode);
                }

                if (n != null)
                {
                    // Use force-move API so morale checks do not block the flee order
                    sm.agent.ForceSetDestinationToNode(n);
                }
            }
        }

        public override void Tick()
        {
            // if unit recovered while fleeing, stop
            if (sm.morale != null && !sm.morale.IsBroken())
            {
                sm.EnterIdleState();
                return;
            }

            timer -= Time.deltaTime;
            if (timer <= 0f)
            {
                // after forced flee, pick one of the broken behaviours
                sm.ChooseBrokenBehaviour();
            }
        }

        public override void Exit()
        {
        }
    }
}