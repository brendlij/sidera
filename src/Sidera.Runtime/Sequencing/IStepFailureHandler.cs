using Sidera.Core.Sequencing;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Told by the runner when a step failed, before the failure goes up. It can hold the run where it is (the step that failed is still the current one, with what it holds) and say that the step is to
/// be run again, instead of the run failing. A failure that it lets go is passed on as it was.
/// </summary>
public interface IStepFailureHandler
{
    /// <summary>Returns true when the step is to be run again (after the handler waited for whoever decides), false when the failure goes on.</summary>
    /// <exception cref="OperationCanceledException">The run was cancelled while the handler waited.</exception>
    Task<bool> HandleAsync(ISequenceStep step, Exception exception, CancellationToken cancellationToken);
}

/// <summary>A failure that a handler has dealt with is marked, so that the steps above the failed one (a track, a group, a block) do not ask the handler again about the same failure.</summary>
public static class StepFailureHandling
{
    private const string Key = "Sidera.FailureHandled";

    public static void MarkHandled(Exception exception) => exception.Data[Key] = true;

    /// <summary>Whether the exception, or one it wraps, was handled already.</summary>
    public static bool IsHandled(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data.Contains(Key))
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsHandled))
            {
                return true;
            }
        }

        return false;
    }
}
