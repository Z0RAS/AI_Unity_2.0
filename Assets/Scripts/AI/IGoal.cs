public interface IGoal
{
    string Name { get; }
    void Start();
    void Tick();
    bool IsComplete { get; }
    void Abort();
}