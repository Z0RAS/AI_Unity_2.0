using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class UIController : MonoBehaviour
{
    public static UIController Instance;

    public GameObject leaderPanel;

    [Header("Building UI")]
    public GameObject buildingPanel; // panel with destroy button + production button
    public Button destroyButton;
    public Button productionToggleButton;

    [Header("Production UI")]
    public GameObject productionPanel; // container shown when production toggled
    public Transform productionListParent; // parent where item buttons are instantiated
    public GameObject productionItemPrefab; // prefab with Button + Image + TMP text
    public GameObject quantityPanel; // panel with qty controls
    public TextMeshProUGUI quantityText;
    public Button qtyPlus;
    public Button qtyMinus;
    public Button qtyConfirm;

    Building selectedBuilding;
    UnitProduction selectedProduction;
    UnitData selectedUnitData;
    int selectedQuantity = 1;

    void Awake()
    {
        Instance = this;
    }

    public void ShowLeaderButtons()
    {

        // If leaderPanel is not under an active root Canvas, reparent it to the first suitable Canvas in scene.
        Canvas currentParentCanvas = leaderPanel.GetComponentInParent<Canvas>();
        if (currentParentCanvas == null || !currentParentCanvas.gameObject.activeInHierarchy)
        {
            // prefer an active root canvas
            Canvas[] canvases = FindObjectsOfType<Canvas>();
            Canvas target = null;
            foreach (var c in canvases)
            {
                if (!c.gameObject.activeInHierarchy) continue;
                // prefer root canvases (no parent Canvas) but accept any active canvas
                if (c.isRootCanvas)
                {
                    target = c;
                    break;
                }
                if (target == null) target = c;
            }

            if (target != null)
            {
                // reparent while preserving local layout
                leaderPanel.transform.SetParent(target.transform, false);
            }
        }

        // Activate panel
        leaderPanel.SetActive(true);

        // Bring panel to top of its canvas
        var rt = leaderPanel.transform as RectTransform;
        if (rt != null)
            rt.SetAsLastSibling();

        // Ensure parent Canvas is visible and on top
        Canvas parentCanvas = leaderPanel.GetComponentInParent<Canvas>();
        if (parentCanvas != null)
        {
            parentCanvas.overrideSorting = true;
            parentCanvas.sortingOrder = Mathf.Max(parentCanvas.sortingOrder, 1000);
        }

        // Ensure CanvasGroup visibility/interactivity
        CanvasGroup cg = leaderPanel.GetComponent<CanvasGroup>();
        if (cg != null)
        {
            cg.alpha = 1f;
            cg.interactable = true;
            cg.blocksRaycasts = true;
        }

        // Debug info
        string parentChain = leaderPanel.name;
        Transform t = leaderPanel.transform.parent;
        while (t != null)
        {
            parentChain = t.name + "/" + parentChain;
            t = t.parent;
        }

        RectTransform rtr = leaderPanel.GetComponent<RectTransform>();
        Vector2 anchored = rtr != null ? rtr.anchoredPosition : Vector2.zero;
        Vector2 size = rtr != null ? rtr.sizeDelta : Vector2.zero;
        Vector3 localScale = leaderPanel.transform.localScale;

    }

    public void HideLeaderButtons()
    {
        if (leaderPanel == null)
        {
            // no panel assigned — nothing to hide
            return;
        }

        // Optionally clear visibility state but keep Canvas sorting intact
        leaderPanel.SetActive(false);
    }

    // Building selection UI ------------------------------------------------

    public void ShowBuildingPanel(Building b)
    {
        selectedBuilding = b;


        if (buildingPanel != null)
            buildingPanel.SetActive(true);

        if (destroyButton != null)
        {
            destroyButton.onClick.RemoveAllListeners();
            destroyButton.onClick.AddListener(DestroySelectedBuilding);
        }

        // Setup production toggle depending on unit production component
        selectedProduction = b.GetComponent<UnitProduction>();

        if (productionToggleButton != null)
        {
            productionToggleButton.onClick.RemoveAllListeners();
            productionToggleButton.onClick.AddListener(ToggleProductionPanel);
            productionToggleButton.gameObject.SetActive(selectedProduction != null);
            productionToggleButton.interactable = selectedProduction != null;
        }

        // Hide/close production UI by default
        if (productionPanel != null) productionPanel.SetActive(false);
        if (quantityPanel != null) quantityPanel.SetActive(false);
    }

    public void HideBuildingPanel()
    {
        selectedBuilding = null;
        selectedProduction = null;
        if (buildingPanel != null)
            buildingPanel.SetActive(false);

        if (destroyButton != null)
            destroyButton.onClick.RemoveAllListeners();

        if (productionToggleButton != null)
        {
            productionToggleButton.onClick.RemoveAllListeners();
            productionToggleButton.gameObject.SetActive(false);
        }

        if (productionPanel != null)
            productionPanel.SetActive(false);

        if (quantityPanel != null)
            quantityPanel.SetActive(false);
    }

    public void DestroySelectedBuilding()
    {
        if (selectedBuilding == null) return;

        var bc = selectedBuilding.GetComponent<BuildingConstruction>();
        if (bc != null)
        {
            GridManager.Instance.FreeCellsForBuilding(bc);
        }

        Destroy(selectedBuilding.gameObject);
        HideBuildingPanel();
    }

    // Production UI -------------------------------------------------------

    void ToggleProductionPanel()
    {
        if (productionPanel == null)
        {
            return;
        }

        if (selectedProduction == null)
        {
            return;
        }

        bool show = !productionPanel.activeSelf;
        productionPanel.SetActive(show);
        if (show) PopulateProductionList();
        else ClearProductionList();
    }

    void PopulateProductionList()
    {
        ClearProductionList();

        if (selectedProduction == null)
        {
            return;
        }

        if (productionItemPrefab == null || productionListParent == null)
        {
            return;
        }

        foreach (var ud in selectedProduction.producibleUnits)
        {
            if (ud == null) continue;

            GameObject item = Instantiate(productionItemPrefab, productionListParent);
            // Try to find Button on root or children
            Button btn = item.GetComponent<Button>();
            if (btn == null)
                btn = item.GetComponentInChildren<Button>();

            TMP_Text label = item.GetComponentInChildren<TMP_Text>();
            Image img = item.GetComponentInChildren<Image>();

            if (label != null) label.text = ud.unitName;
            if (img != null && ud.icon != null) img.sprite = ud.icon;

            if (btn != null)
            {
                // capture local variable for closure
                UnitData captured = ud;
                btn.onClick.AddListener(() =>
                {
                    OnProductionItemClicked(captured);
                });
            }
        }
    }

    void ClearProductionList()
    {
        if (productionListParent == null) return;
        for (int i = productionListParent.childCount - 1; i >= 0; i--)
            Destroy(productionListParent.GetChild(i).gameObject);
    }

    void OnProductionItemClicked(UnitData ud)
    {
        selectedUnitData = ud;
        selectedQuantity = 1;

        // Ensure quantity UI references exist (fallback auto-find)
        EnsureQuantityUIAvailable();


        quantityPanel.SetActive(true);
        UpdateQuantityText();

        if (qtyPlus != null)
        {
            qtyPlus.onClick.RemoveAllListeners();
            qtyPlus.onClick.AddListener(() => { selectedQuantity++; UpdateQuantityText(); });
        }
        if (qtyMinus != null)
        {
            qtyMinus.onClick.RemoveAllListeners();
            qtyMinus.onClick.AddListener(() => { selectedQuantity = Mathf.Max(1, selectedQuantity - 1); UpdateQuantityText(); });
        }
        if (qtyConfirm != null)
        {
            qtyConfirm.onClick.RemoveAllListeners();
            qtyConfirm.onClick.AddListener(OnQuantityConfirm);
        }
    }

    void UpdateQuantityText()
    {
        if (quantityText == null)
        {
            // try to auto-find it if missing
            EnsureQuantityUIAvailable();
        }

        if (quantityText != null)
            quantityText.text = selectedQuantity.ToString();
    }

    void OnQuantityConfirm()
    {
        if (selectedProduction == null || selectedUnitData == null) return;

        int spawned = selectedProduction.SpawnUnitsImmediate(selectedUnitData, selectedQuantity);

        // optionally show feedback, then close quantity panel
        if (quantityPanel != null) quantityPanel.SetActive(false);
        if (productionPanel != null) productionPanel.SetActive(false);
    }

    // Try to resolve quantity UI references automatically if inspector wiring is missing.
    void EnsureQuantityUIAvailable()
    {
        if (quantityPanel == null)
        {
            // try find by name in scene
            var found = GameObject.Find("QuantityPanel");
            if (found != null)
                quantityPanel = found;
        }

        if (quantityPanel != null)
        {
            if (quantityText == null)
                quantityText = quantityPanel.GetComponentInChildren<TextMeshProUGUI>();

            if (qtyPlus == null || qtyMinus == null || qtyConfirm == null)
            {
                var buttons = quantityPanel.GetComponentsInChildren<Button>();
                foreach (var b in buttons)
                {
                    string name = b.gameObject.name.ToLowerInvariant();
                    if (qtyPlus == null && name.Contains("plus")) qtyPlus = b;
                    if (qtyMinus == null && name.Contains("minus")) qtyMinus = b;
                if (qtyConfirm == null && (name.Contains("confirm") || name.Contains("ok") || name.Contains("spawn"))) qtyConfirm = b;
                }
            }
        }

        
    }
}