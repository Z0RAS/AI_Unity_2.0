using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Improved scout: soldiers (non-villagers) perform exploration biased toward map center (0,0).
/// Villagers are NOT used as scouts (they remain for economy/build tasks).
/// Keeps lightweight visited maps to avoid repeats and a movement watchdog so stalled/idle scouts get reassigned.
/// </summary>
public class ScoutGoal : IGoal
{
    public string Name { get; private set; } = "Scout";

    private readonly EnemyKingController owner;
    private readonly int minScouts;
    private readonly int maxScouts;
    private readonly float radius;
    private readonly float moveInterval;
    private readonly float timeout;
    private readonly Func<bool> stopCondition;

    private float lastRun;
    private float startTime;
    private bool started;
    private bool aborted;

    // Grid cells we've visited (so exploration doesn't revisit same tiles)
    private static readonly HashSet<Vector2Int> visitedCells = new HashSet<Vector2Int>();

    // Movement watchdog state per unit (by instance id)
    private static readonly Dictionary<int, Vector3> lastPosition = new Dictionary<int, Vector3>();
    private static readonly Dictionary<int, float> stillTimer = new Dictionary<int, float>();
    private static readonly Dictionary<int, float> lastCheckTime = new Dictionary<int, float>();
    private const float stillTimeout = 3f; // seconds of little movement before we consider stuck
    private const float stillMoveThreshold = 0.04f; // world units considered movement

    public ScoutGoal(EnemyKingController owner, int minScouts = 1, int maxScouts = 3, float radius = 12f, float moveInterval = 8f, float timeout = 60f, Func<bool> stopCondition = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.minScouts = Math.Max(0, minScouts);
        this.maxScouts = Math.Max(this.minScouts, maxScouts);
        this.radius = Math.Max(1f, radius);
        this.moveInterval = Math.Max(0.1f, moveInterval);
        this.timeout = Math.Max(0f, timeout);
        this.stopCondition = stopCondition;
    }

    public void Start()
    {
        started = true;
        startTime = Time.time;
        lastRun = Time.time - moveInterval;
    }

    public void Tick()
    {
        if (aborted || !started || owner == null || owner.CastleInstance == null) return;

        if (timeout > 0f && Time.time - startTime > timeout) { aborted = true; return; }
        if (stopCondition != null && stopCondition()) { aborted = true; return; }

        if (Time.time - lastRun < moveInterval) return;
        lastRun = Time.time;

        var econ = owner.EnemyEconomy;
        var castle = owner.CastleInstance;
        var gm = GridManager.Instance;
        if (castle == null) return;

        // If castle is still under construction, allow soldiers to scout but avoid sending villagers away
        var castleBc = castle.GetComponent<BuildingConstruction>();
        bool castleUnderConstruction = (castleBc != null && !castleBc.isFinished);

        // Soldier exploration (non-villagers): pick progressive targets toward map center (0,0)
        var allUnits = UnityEngine.Object.FindObjectsOfType<UnitAgent>();
        var soldierCandidates = allUnits
            .Where(u => u != null && u.owner == econ && u.category != UnitCategory.Villager)
            .OrderBy(u => (u.transform.position - castle.transform.position).sqrMagnitude)
            .ToList();

        int willSendSoldiers = Math.Min(maxScouts, soldierCandidates.Count);
        for (int i = 0; i < willSendSoldiers; i++)
        {
            var ua = soldierCandidates[i];
            if (ua == null) continue;

            // If unit is still moving and not stuck, skip reassigning this tick.
            if (!ShouldAssignNewTarget(ua)) continue;

            Node destNode = FindExplorationNodeForSoldier(ua, gm, (int)radius);
            if (destNode != null)
            {
                visitedCells.Add(new Vector2Int(destNode.gridX, destNode.gridY));
                TrySendSoldierToNode(ua, destNode);
                continue;
            }

            // fallback: random point toward center
            Vector3 fallback = GetRandomPointTowardCenter(castle.transform.position, radius);
            Node fbNode = gm != null ? gm.NodeFromWorldPoint(fallback) : null;
            if (fbNode != null)
            {
                visitedCells.Add(new Vector2Int(fbNode.gridX, fbNode.gridY));
                TrySendSoldierToNode(ua, fbNode);
            }
        }

        // NOTE: Villagers are intentionally NOT used as scouts. They remain available for gather/build tasks.
    }

    // decide if unit should get a new target: if it has no path OR has been nearly stationary for stillTimeout
    private bool ShouldAssignNewTarget(UnitAgent ua)
    {
        if (ua == null) return false;
        int id = ua.GetInstanceID();
        float now = Time.time;

        // if there's no active path, assign immediately
        bool hasPath = false;
        try { hasPath = ua.HasPath(); } catch { hasPath = false; }
        if (!hasPath) return true;

        // otherwise update movement watchdog
        Vector3 pos = ua.transform.position;
        if (!lastPosition.ContainsKey(id)) lastPosition[id] = pos;
        if (!lastCheckTime.ContainsKey(id)) lastCheckTime[id] = now;

        float dt = now - lastCheckTime[id];
        lastCheckTime[id] = now;

        float moved = Vector3.Distance(pos, lastPosition[id]);
        lastPosition[id] = pos;

        if (!stillTimer.ContainsKey(id)) stillTimer[id] = 0f;
        if (moved < stillMoveThreshold)
            stillTimer[id] += dt;
        else
            stillTimer[id] = 0f;

        // if still for too long, consider stuck and reassign
        if (stillTimer[id] >= stillTimeout)
        {
            stillTimer[id] = 0f; // reset so we don't spam reassign
            return true;
        }

        return false;
    }

    private Node FindExplorationNodeForSoldier(UnitAgent ua, GridManager gm, int maxRadius)
    {
        if (ua == null || gm == null) return null;

        // pick a point biased toward world center (0,0)
        Vector3 center = Vector3.zero;
        Vector3 toCenter = (center - ua.transform.position);
        if (toCenter.sqrMagnitude < 0.0001f)
        {
            Node near = gm.FindClosestWalkableNode(Mathf.RoundToInt(ua.transform.position.x), Mathf.RoundToInt(ua.transform.position.y));
            return near;
        }

        float baseAngle = Mathf.Atan2(toCenter.y, toCenter.x);
        float jitter = UnityEngine.Random.Range(-Mathf.PI / 6f, Mathf.PI / 6f); // +-30 degrees
        float ang = baseAngle + jitter;
        float dist = UnityEngine.Random.Range(maxRadius * 0.4f, maxRadius);

        Vector3 candidate = center + new Vector3(Mathf.Cos(ang) * dist, Mathf.Sin(ang) * dist, 0f);
        Node node = gm.NodeFromWorldPoint(candidate);
        if (node == null || !node.walkable)
        {
            node = gm.FindClosestWalkableNode(Mathf.RoundToInt(candidate.x), Mathf.RoundToInt(candidate.y));
        }

        if (node != null)
        {
            var key = new Vector2Int(node.gridX, node.gridY);
            if (visitedCells.Contains(key))
            {
                // try small local spiral to find adjacent unvisited
                for (int r = 1; r <= 4; r++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        for (int dy = -r; dy <= r; dy++)
                        {
                            Node n = gm.GetNode(node.gridX + dx, node.gridY + dy);
                            if (n == null || !n.walkable) continue;
                            var k = new Vector2Int(n.gridX, n.gridY);
                            if (visitedCells.Contains(k)) continue;
                            return n;
                        }
                    }
                }
            }
            return node;
        }

        return null;
    }

    private Vector3 GetRandomPointTowardCenter(Vector3 origin, float radius)
    {
        Vector3 center = Vector3.zero;
        Vector3 dir = (center - origin).normalized;
        float ang = Mathf.Atan2(dir.y, dir.x);
        float jitter = UnityEngine.Random.Range(-Mathf.PI / 3f, Mathf.PI / 3f);
        float r = UnityEngine.Random.Range(radius * 0.4f, radius);
        return center + new Vector3(Mathf.Cos(ang + jitter) * r, Mathf.Sin(ang + jitter) * r, 0f);
    }

    // Replace TrySendSoldierToNode with this version that clears possible reservations
    // so soldiers are free to move and won't get stuck holding a reserved node.
    private void TrySendSoldierToNode(UnitAgent ua, Node node)
    {
        if (ua == null || node == null) return;
        try
        {
            // Release any held combat/reservation so scout can move freely.
            try
            {
                // Prefer explicit reservation release + clear combat reservation if available.
                NodeReservationSystem.Instance?.ReleaseReservation(ua);
                ua.ClearCombatReservation();
            }
            catch { /* ignore */ }

            ua.SetDestinationToNode(node);
        }
        catch { }
    }

    public bool IsComplete => aborted;

    public void Abort() { aborted = true; }
}