using UnityEngine;

public class SceneUnitDistributor : MonoBehaviour
{
    private void Start()
    {
        DistributeUnits();
    }

    void DistributeUnits()
    {
        var grid = GridManager.Instance;
        var nrs = NodeReservationSystem.Instance;

        if (grid == null)
        {
            Debug.LogWarning("[SceneUnitDistributor] GridManager.Instance is null - cannot distribute units.");
            return;
        }

        UnitAgent[] units = FindObjectsOfType<UnitAgent>();
        if (units == null || units.Length == 0) return;

        foreach (UnitAgent u in units)
        {
            if (u == null) continue;

            // try to find the best free node near the unit start position
            Node preferred = grid.NodeFromWorldPoint(u.transform.position);
            Node chosen = null;

            if (preferred != null)
            {
                // if preferred is walkable and free, try reserve it
                if (preferred.walkable)
                {
                    if (nrs != null)
                    {
                        if (nrs.ReserveNode(preferred, u))
                            chosen = preferred;
                    }
                    else
                    {
                        chosen = preferred;
                    }
                }
            }

            // if not reserved yet, ask reservation system to find & reserve a nearby best node
            if (chosen == null && nrs != null)
            {
                chosen = nrs.FindAndReserveBestNode(preferred ?? grid.FindClosestWalkableNode(0, 0), u, 6);
            }

            // final fallback: find any walkable node and try reserve / let reservation system find a nearby node
            if (chosen == null && grid != null)
            {
                Node any = grid.FindClosestWalkableNode(preferred != null ? preferred.gridX : 0, preferred != null ? preferred.gridY : 0);
                if (any != null)
                {
                    if (nrs != null)
                    {
                        if (nrs.ReserveNode(any, u))
                        {
                            chosen = any;
                        }
                        else
                        {
                            // let reservation system search around 'any' to find & reserve a suitable node
                            chosen = nrs.FindAndReserveBestNode(any, u, 6);
                        }
                    }
                    else
                    {
                        chosen = any;
                    }
                }
            }

            if (chosen == null) continue;

            // place unit exactly on node center and tell unit to hold reservation where appropriate
            try
            {
                // Snap simulation and transform to the chosen node center (keeps simPosition in sync).
                u.SnapToNodeCenter(chosen, true);

                // IMPORTANT: only mark holdReservation for villagers (workers).
                // Combat units (infantry/archers/etc.) should NOT hold their spawn reservation indefinitely,
                // because that prevents them from releasing it when commanded to move and can effectively block movement.
                // Villagers should keep holdReservation so recruiters/finalizer logic can rely on it.
                if (u.category == UnitCategory.Villager)
                    u.holdReservation = true;
                else
                    u.holdReservation = false;
            }
            catch
            {
            }
        }
    }
}