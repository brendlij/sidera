namespace Sidera.Core.Sequencing;

/// <summary>
/// Lets something outside the Repeat end it before its count: the conditions of a workflow. The Repeat asks before each iteration and tells it after. It holds no state of its own; whoever
/// implements it does (and is created for one run's worth of steps).
/// </summary>
public interface IRepeatControl
{
    /// <summary>The repetition is about to start.</summary>
    void Begin();

    /// <summary>Before iteration <paramref name="completed"/> + 1: whether to stop instead.</summary>
    bool ShouldStop(int completed);

    /// <summary>After an iteration: whether to stop now. <paramref name="completed"/> iterations are done.</summary>
    bool AfterIteration(int completed);

    /// <summary>The repetition is over, however it ended.</summary>
    void End(int completed);
}

/// <summary>Asked by a <see cref="SequenceGroup"/> before it runs a child, and told after it ran it.</summary>
public interface IGroupGuard
{
    /// <summary>Whether child <paramref name="index"/> may run. When not, the group ends there and normally.</summary>
    bool MayRun(int index, ISequenceStep child);

    /// <summary>Child <paramref name="index"/> ran to its end.</summary>
    void Ran(int index, ISequenceStep child);
}
