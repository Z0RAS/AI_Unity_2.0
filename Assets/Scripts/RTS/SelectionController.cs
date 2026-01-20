using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

public class SelectionController : MonoBehaviour
{
    public static SelectionController Instance;

    public Camera mainCamera;
    public RectTransform selectionBoxUI;
    public Canvas canvas;
    public float clickThreshold = 5f;
    public bool debugLogs = false;

    [HideInInspector]
    public List<UnitAgent> selectedUnits = new List<UnitAgent>();

    PlayerEconomy canonicalPlayerEconomy;

    private Vector2 dragStart;
    private bool isDragging = false;
    private Coroutine hideBoxCoroutine;

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (selectionBoxUI != null) selectionBoxUI.gameObject.SetActive(false);
        var gs = GameObject.Find("GameSystems");
        if (gs != null) canonicalPlayerEconomy = gs.GetComponent<PlayerEconomy>();
        if (canonicalPlayerEconomy == null) canonicalPlayerEconomy = FindObjectOfType<PlayerEconomy>();
    }

    private void Update()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        HandleSelectionInput();
        UpdateSelectionBoxVisual();
    }

    void HandleSelectionInput()
    {
        bool pointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            if (pointerOverUI) { isDragging = false; return; }
            if (hideBoxCoroutine != null) { StopCoroutine(hideBoxCoroutine); hideBoxCoroutine = null; }
            dragStart = Mouse.current.position.ReadValue();
            isDragging = true;
        }

        if (Mouse.current.leftButton.wasReleasedThisFrame)
        {
            if (pointerOverUI)
            {
                isDragging = false;
                if (selectionBoxUI != null) selectionBoxUI.gameObject.SetActive(false);
                return;
            }

            Vector2 dragEnd = Mouse.current.position.ReadValue();
            bool wasClick = Vector2.Distance(dragStart, dragEnd) < clickThreshold;
            isDragging = false;

            if (selectionBoxUI != null)
            {
                if (wasClick)
                {
                    if (hideBoxCoroutine != null) StopCoroutine(hideBoxCoroutine);
                    hideBoxCoroutine = StartCoroutine(HideSelectionBoxDelayed(0.25f));
                }
                else
                {
                    if (hideBoxCoroutine != null) { StopCoroutine(hideBoxCoroutine); hideBoxCoroutine = null; }
                    selectionBoxUI.gameObject.SetActive(false);
                }
            }

            if (wasClick)
                SingleSelect(dragEnd);
            else
                MultiSelect(dragStart, dragEnd);
        }
    }

    void SingleSelect(Vector2 mousePos)
    {
        ClearSelection();

        Ray ray = mainCamera.ScreenPointToRay(mousePos);
        RaycastHit2D hit = Physics2D.GetRayIntersection(ray);

        if (hit.collider != null)
        {
            UnitAgent agent = hit.collider.GetComponent<UnitAgent>();
            if (agent != null && IsSelectableUnit(agent))
            {
                AddToSelection(agent);

                // Ensure single-select also triggers per-unit selection state (indicator + callbacks)
                agent.OnSelected();
                agent.selectionIndicator?.Show();
                agent.SetIdle(false);

                bool leaderSelected = selectedUnits.Any(u => u != null && (u.category == UnitCategory.Leader || u is LeaderUnit));
                if (leaderSelected) UIController.Instance?.ShowLeaderButtons(); else UIController.Instance?.HideLeaderButtons();
                return;
            }

            Building building = hit.collider.GetComponentInParent<Building>();
            if (building != null) { UIController.Instance?.ShowBuildingPanel(building); return; }
        }

        UIController.Instance?.HideBuildingPanel();
        UIController.Instance?.HideLeaderButtons();
    }

    void MultiSelect(Vector2 start, Vector2 end)
    {
        ClearSelection();

        Vector2 min = Vector2.Min(start, end);
        Vector2 max = Vector2.Max(start, end);

        UnitAgent[] allUnits = Object.FindObjectsByType<UnitAgent>(FindObjectsSortMode.None);

        foreach (UnitAgent agent in allUnits)
        {
            if (agent == null) continue;
            Vector3 screenPos = mainCamera.WorldToScreenPoint(agent.transform.position);
            bool inside = screenPos.x >= min.x && screenPos.x <= max.x && screenPos.y >= min.y && screenPos.y <= max.y;
            if (inside && IsSelectableUnit(agent))
                AddToSelection(agent);
        }

        bool leaderSelected = selectedUnits.Any(u => u != null && (u.category == UnitCategory.Leader || u is LeaderUnit));

        foreach (var agent in selectedUnits)
        {
            if (agent == null) continue;
            agent.OnSelected();
            agent.selectionIndicator?.Show();
            agent.SetIdle(false);
        }

        if (leaderSelected)
            StartCoroutine(DelayedShowLeader());
        else
            UIController.Instance?.HideLeaderButtons();

        UIController.Instance?.HideBuildingPanel();
    }

    bool IsSelectableUnit(UnitAgent agent)
    {
        if (agent == null) return false;
        if (agent.owner != null) return agent.owner == canonicalPlayerEconomy;
        if (agent is LeaderUnit) return true;
        return canonicalPlayerEconomy != null;
    }

    void AddToSelection(UnitAgent agent)
    {
        if (!selectedUnits.Contains(agent))
            selectedUnits.Add(agent);
    }

    System.Collections.IEnumerator DelayedShowLeader()
    {
        yield return null;
        UIController.Instance?.ShowLeaderButtons();
    }

    System.Collections.IEnumerator HideSelectionBoxDelayed(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (selectionBoxUI != null) selectionBoxUI.gameObject.SetActive(false);
        hideBoxCoroutine = null;
    }

    public void ClearSelection()
    {
        foreach (var unit in selectedUnits)
        {
            if (unit == null) continue;
            unit.OnDeselected();
            unit.selectionIndicator?.Hide();
        }

        selectedUnits.Clear();
        UIController.Instance?.HideBuildingPanel();
        UIController.Instance?.HideLeaderButtons();
    }

    void UpdateSelectionBoxVisual()
    {
        if (!isDragging || selectionBoxUI == null || canvas == null) return;
        if (!selectionBoxUI.gameObject.activeSelf) selectionBoxUI.gameObject.SetActive(true);

        Vector2 mousePos = Mouse.current.position.ReadValue();
        RectTransform canvasRect = canvas.transform as RectTransform;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, dragStart, null, out Vector2 startPos);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, mousePos, null, out Vector2 currentPos);

        Vector2 size = currentPos - startPos;
        selectionBoxUI.anchoredPosition = startPos + size / 2f;
        selectionBoxUI.sizeDelta = new Vector2(Mathf.Abs(size.x), Mathf.Abs(size.y));
    }

 
}