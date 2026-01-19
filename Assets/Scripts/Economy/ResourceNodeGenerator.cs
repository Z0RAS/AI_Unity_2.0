using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[ExecuteAlways]
public class ResourceNodeGenerator : MonoBehaviour
{
    [Header("Prefabs")]
    [Tooltip("Assign one prefab per ResourceType (index must match ResourceType enum order). Each prefab must contain a ResourceNode component.")]
    public GameObject[] resourceNodePrefabs;

    [Header("Simple Generation")]
    [Range(0f, 1f)]
    [Tooltip("Fraction (0..1) of available walkable nodes to fill with trees.")]
    public float treeCoverage = 0.5f;

    [Tooltip("Number of iron resource nodes to place into gaps after trees")]
    public int ironNodeCount = 10;

    [Tooltip("Number of gold resource nodes to place into gaps after trees")]
    public int goldNodeCount = 8;

    [Tooltip("Number of wood resource nodes to place into gaps after trees")]
    public int woodNodeCount = 20;

    [Tooltip("Number of food resource nodes to place into gaps after trees")]
    public int foodNodeCount = 15;

    [Header("Count / Amount (single-type mode)")]
    public int nodesCount = 12;
    public int minAmount = 50;
    public int maxAmount = 200;

    [Tooltip("Exclude blocks/bands if they overlap buildings within this many grid nodes")]
    public int buildingExcludeRadiusNodes = 2;

    [Header("Start area (player / enemy)")]
    [Tooltip("If true, generator will reserve two empty circular areas for player and enemy start buildings")]
    public bool createStartClearings = true;
    [Tooltip("Radius (in grid nodes) of each start clearing")]
    public int startAreaRadiusNodes = 3;
    [Tooltip("Margin (in grid nodes) from the map edges to place the start clearings")]
    public int startAreaMarginNodes = 2;

    [Header("Placement / Misc")]
    [Tooltip("If true, generator will parent placed nodes under a root GameObject in the scene")]
    public bool parentUnderSceneRoot = true;
    [Tooltip("Name of the root container created under scene root when parenting nodes")]
    public string sceneRootContainerName = "ResourceNodes";
    [Tooltip("If true, existing child ResourceNode game objects will be removed before generation")]
    public bool clearExistingChildren = true;
    [Tooltip("If true, avoid nodes that are currently reserved by NodeReservationSystem")]
    public bool avoidReservedNodes = true;

    [Header("Temporary load")]
    [Tooltip("If true, spawned resources are marked DontSave (temporary) and skip expensive editor deletion on regenerate. Fast for editor play/unload.")]
    public bool temporaryOnLoad = true;

    [Header("Randomization")]
    public bool randomSeed = true;
    public int seed = 0;

    [ContextMenu("Generate Resource Nodes")]
    public void GenerateNodesContext() => GenerateNodes();

    private void Start()
    {
        if (Application.isPlaying)
            GenerateNodes();
    }

    // Minimal, deterministic generator:
    // 1) Optionally reserve two empty circular start areas
    // 2) Fill fraction of map with trees (ResourceType.Wood)
    // 3) Place requested counts of iron/gold/wood/food into remaining free nodes
    public void GenerateNodes()
    {
        GridManager gm = GridManager.Instance;
        if (gm == null)
        {
            Debug.LogWarning("[ResourceNodeGenerator] GridManager.Instance not found. Cannot place on grid.");
            return;
        }

        if (clearExistingChildren)
            RemovePreviouslyGeneratedNodes();

        var candidatesQuery = gm.AllNodes.Where(n => n != null && n.walkable);

        if (avoidReservedNodes && NodeReservationSystem.Instance != null)
            candidatesQuery = candidatesQuery.Where(n => !NodeReservationSystem.Instance.IsReserved(n));

        List<Node> candidates = candidatesQuery.ToList();
        if (candidates.Count == 0)
        {
            Debug.LogWarning("[ResourceNodeGenerator] No suitable candidate nodes available.");
            return;
        }

        // compute grid extents
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var n in candidates)
        {
            if (n.gridX < minX) minX = n.gridX;
            if (n.gridX > maxX) maxX = n.gridX;
            if (n.gridY < minY) minY = n.gridY;
            if (n.gridY > maxY) maxY = n.gridY;
        }

        Transform containerTransform = null;
        GameObject container = null;
        if (parentUnderSceneRoot)
        {
            container = GameObject.Find(sceneRootContainerName);
            if (container == null)
            {
                container = new GameObject(sceneRootContainerName);
                container.transform.parent = null;

                // If temporaryOnLoad we mark the container as DontSave so Editor won't serialize these objects.
#if UNITY_EDITOR
                if (temporaryOnLoad)
                    container.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
                else
                    container.hideFlags = HideFlags.None;
#else
                if (temporaryOnLoad)
                    container.hideFlags = HideFlags.DontSave;
                else
                    container.hideFlags = HideFlags.None;
#endif
            }
            else
            {
                // ensure hideFlags consistent if container existed from a previous run
#if UNITY_EDITOR
                if (temporaryOnLoad)
                    container.hideFlags |= HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
#else
                if (temporaryOnLoad)
                    container.hideFlags |= HideFlags.DontSave;
#endif
            }

            containerTransform = container.transform;
        }

        System.Random rnd = randomSeed ? new System.Random() : new System.Random(seed);

        HashSet<Node> used = new HashSet<Node>();

        // 0) Reserve two start clearings (player bottom-left, enemy top-right)
        if (createStartClearings)
        {
            int r = Mathf.Max(0, startAreaRadiusNodes);
            int margin = Mathf.Max(0, startAreaMarginNodes);

            int playerCenterX = minX + margin + r;
            int playerCenterY = minY + margin + r;

            int enemyCenterX = maxX - margin - r;
            int enemyCenterY = maxY - margin - r;

            // defensive clamp to bounds
            playerCenterX = Mathf.Clamp(playerCenterX, minX, maxX);
            playerCenterY = Mathf.Clamp(playerCenterY, minY, maxY);
            enemyCenterX = Mathf.Clamp(enemyCenterX, minX, maxX);
            enemyCenterY = Mathf.Clamp(enemyCenterY, minY, maxY);

            // collect nodes in chebyshev radius and mark them used so generator leaves them empty
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    Node p = gm.GetNode(playerCenterX + dx, playerCenterY + dy);
                    if (p != null && p.walkable)
                        used.Add(p);

                    Node e = gm.GetNode(enemyCenterX + dx, enemyCenterY + dy);
                    if (e != null && e.walkable)
                        used.Add(e);
                }
            }
        }

        // 1) Trees (fill coverage)
        int treesToPlace = Mathf.Clamp(Mathf.RoundToInt(candidates.Count * Mathf.Clamp01(treeCoverage)), 0, candidates.Count);

        var shuffled = candidates.OrderBy(_ => rnd.Next()).ToList();
        int placedTrees = 0;
        int idx = 0;
        while (placedTrees < treesToPlace && idx < shuffled.Count)
        {
            Node n = shuffled[idx++];
            if (n == null) continue;
            if (!n.walkable) continue;
            if (used.Contains(n)) continue;
            if (avoidReservedNodes && NodeReservationSystem.Instance != null && NodeReservationSystem.Instance.IsReserved(n)) continue;

            PlaceResourceNode(n, ResourceType.Wood, used, containerTransform, temporaryOnLoad);
            placedTrees++;
        }

        // Prepare remaining free nodes
        var free = candidates.Where(n => !used.Contains(n)).OrderBy(_ => rnd.Next()).ToList();

        // 2) Place iron, gold, wood (additional), food in that order into gaps
        PlaceClustersOfType(free, ResourceType.Iron, ironNodeCount, used, containerTransform, rnd);
        free = candidates.Where(n => !used.Contains(n)).OrderBy(_ => rnd.Next()).ToList();

        // Generate gold clusters like iron/food
        PlaceClustersOfType(free, ResourceType.Gold, goldNodeCount, used, containerTransform, rnd);
        free = candidates.Where(n => !used.Contains(n)).OrderBy(_ => rnd.Next()).ToList();

        PlaceMultipleOfType(free, ResourceType.Wood, woodNodeCount, used, containerTransform, rnd);
        free = candidates.Where(n => !used.Contains(n)).OrderBy(_ => rnd.Next()).ToList();

        PlaceClustersOfType(free, ResourceType.Food, foodNodeCount, used, containerTransform, rnd);

    }

    private void PlaceMultipleOfType(List<Node> freeList, ResourceType type, int count, HashSet<Node> used, Transform containerTransform, System.Random rnd)
    {
        if (count <= 0 || freeList == null || freeList.Count == 0) return;
        int placed = 0;
        int i = 0;
        while (placed < count && i < freeList.Count)
        {
            Node n = freeList[i++];
            if (n == null) continue;
            if (!n.walkable) continue;
            if (used.Contains(n)) continue;
            if (avoidReservedNodes && NodeReservationSystem.Instance != null && NodeReservationSystem.Instance.IsReserved(n)) continue;

            PlaceResourceNode(n, type, used, containerTransform, temporaryOnLoad);
            used.Add(n);
            placed++;
        }
    }

    // NEW: place clustered square blocks for given type (size between 3 and 6 inclusive)
    // Each successful cluster occupies multiple nodes and each node in the cluster receives an amount between 100-200.
    private void PlaceClustersOfType(List<Node> freeList, ResourceType type, int clusterCount, HashSet<Node> used, Transform containerTransform, System.Random rnd)
    {
        if (clusterCount <= 0 || freeList == null || freeList.Count == 0) return;

        GridManager gm = GridManager.Instance;
        if (gm == null) return;

        int placedClusters = 0;
        int attempts = 0;
        int maxAttempts = Math.Max(200, freeList.Count * 5);

        while (placedClusters < clusterCount && attempts < maxAttempts)
        {
            attempts++;
            // pick random candidate center
            Node center = freeList[rnd.Next(freeList.Count)];
            if (center == null) continue;
            if (used.Contains(center)) continue;
            if (avoidReservedNodes && NodeReservationSystem.Instance != null && NodeReservationSystem.Instance.IsReserved(center)) continue;

            // choose square size 3..6
            int size = rnd.Next(3, 7); // upper bound exclusive -> 3..6
            // compute start coords (attempt to center cluster close to 'center' node)
            int startX = center.gridX - (size - 1) / 2;
            int startY = center.gridY - (size - 1) / 2;

            // collect and validate all nodes in square
            var clusterNodes = new List<Node>();
            bool ok = true;
            // Guard indices explicitly to avoid any out-of-range access in GridManager.GetNode.
            // This prevents IndexOutOfRangeExceptions when clusters near edges are attempted.
            for (int fx = 0; fx < size && ok; fx++)
            {
                for (int fy = 0; fy < size; fy++)
                {
                    int nx = startX + fx;
                    int ny = startY + fy;

                    // explicit bounds check using GridManager's exposed properties
                    if (nx < 0 || ny < 0 || nx >= gm.GridSizeX || ny >= gm.GridSizeY)
                    {
                        ok = false;
                        break;
                    }

                    Node n = null;
                    try
                    {
                        n = gm.GetNode(nx, ny);
                    }
                    catch
                    {
                        // Defensive: treat any exception as invalid node for this cluster attempt.
                        n = null;
                    }

                    if (n == null || !n.walkable) { ok = false; break; }
                    if (used.Contains(n)) { ok = false; break; }
                    if (avoidReservedNodes && NodeReservationSystem.Instance != null && NodeReservationSystem.Instance.IsReserved(n)) { ok = false; break; }
                    clusterNodes.Add(n);
                }
            }

            if (!ok) continue;

            // Success: place nodes in cluster. Each node gets 100..200 units and is placed at its center position.
            foreach (var n in clusterNodes)
            {
                if (n == null) continue;
                int amount = rnd.Next(100, 201); // 100..200 inclusive
                PlaceResourceNode(n, type, used, containerTransform, temporaryOnLoad, amount);
                used.Add(n);
            }

            placedClusters++;
        }
    }

    // core placement method (keeps existing ResourceNode initialization)
    // added optional amountOverride for cluster placement
    private void PlaceResourceNode(Node n, ResourceType type, HashSet<Node> usedNodes, Transform containerTransform, bool markTemporary, int? amountOverride = null)
    {
        if (n == null || usedNodes.Contains(n)) return;

        GameObject prefab = GetPrefabForType(type);
        GameObject go;
        if (prefab != null)
        {
            if (Application.isPlaying)
                go = Instantiate(prefab, n.centerPosition, Quaternion.identity);
            else
            {
#if UNITY_EDITOR
                go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, this.gameObject.scene) as GameObject;
                if (go != null) go.transform.position = n.centerPosition;
#else
                go = Instantiate(prefab, n.centerPosition, Quaternion.identity);
#endif
            }
        }
        else
        {
            go = new GameObject($"ResourceNode_{type}");
            go.transform.position = n.centerPosition;
            go.AddComponent<ResourceNode>();
        }

        if (go != null)
        {
            go.transform.localScale = Vector3.one;

            if (containerTransform != null)
                go.transform.SetParent(containerTransform, true);

            // mark temporary in editor/play to avoid serialization/unnecessary editor unload cost
            if (markTemporary)
            {
#if UNITY_EDITOR
                go.hideFlags |= HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
#else
                go.hideFlags |= HideFlags.DontSave;
#endif
            }
        }

        ResourceNode rn = go.GetComponent<ResourceNode>();
        if (rn != null)
        {
            rn.resourceType = type;
            if (amountOverride.HasValue)
                rn.amount = amountOverride.Value;
            else
                rn.amount = UnityEngine.Random.Range(minAmount, maxAmount + 1);
            rn.transform.position = n.centerPosition;
        }

        usedNodes.Add(n);
    }

    // Remove previously generated generated nodes parented under container or generator.
    // If temporaryOnLoad is true we skip expensive editor Undo deletions because objects are DontSave and will be cleaned automatically.
    void RemovePreviouslyGeneratedNodes()
    {
        if (!clearExistingChildren) return;

        // Fast helper that destroys ResourceNode children under a parent transform.
        void DestroyChildrenFast(Transform parent)
        {
            if (parent == null) return;

            // Collect first to avoid modifying collection while iterating
            var existing = parent.GetComponentsInChildren<ResourceNode>(true).ToList();

#if UNITY_EDITOR
            // In editor edit-mode use DestroyImmediate (synchronous). In play-mode use Destroy.
            if (Application.isPlaying)
            {
                foreach (var rn in existing)
                {
                    if (rn != null && rn.gameObject != null)
                        Destroy(rn.gameObject);
                }
            }
            else
            {
                foreach (var rn in existing)
                {
                    if (rn != null && rn.gameObject != null)
                        DestroyImmediate(rn.gameObject);
                }
            }
#else
            // Runtime: Destroy immediately if in play mode (as appropriate).
            if (Application.isPlaying)
            {
                foreach (var rn in existing)
                    if (rn != null && rn.gameObject != null)
                        Destroy(rn.gameObject);
            }
            else
            {
                foreach (var rn in existing)
                    if (rn != null && rn.gameObject != null)
                        DestroyImmediate(rn.gameObject);
            }
#endif
        }

        // If we parent under scene root, clear that container's children fast.
        if (parentUnderSceneRoot)
        {
            GameObject container = GameObject.Find(sceneRootContainerName);
            if (container != null)
                DestroyChildrenFast(container.transform);
        }

        // Also clear any local children under this generator object.
        DestroyChildrenFast(this.transform);
    }

    GameObject GetPrefabForType(ResourceType type)
    {
        int idx = (int)type;
        if (resourceNodePrefabs != null && idx >= 0 && idx < resourceNodePrefabs.Length)
            return resourceNodePrefabs[idx];
        return null;
    }
}