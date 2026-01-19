using UnityEngine;

public class SelectionIndicator : MonoBehaviour
{
    public GameObject indicator;

    void Start()
    {
        if (indicator != null)
            indicator.SetActive(false);
    }

    public void Show()
    {
        if (indicator != null)
            indicator.SetActive(true);
    }

    public void Hide()
    {
        if (indicator != null)
            indicator.SetActive(false);
    }
}