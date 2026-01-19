public class LeaderUnit : UnitAgent
{
    // Use non-override methods because base class does not declare virtual methods.
    public void OnSelected()
    {
        UIController.Instance.ShowLeaderButtons();
    }

    public void OnDeselected()
    {
        UIController.Instance.HideLeaderButtons();
    }
}