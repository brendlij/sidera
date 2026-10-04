using System.Globalization;

namespace Sidera.Core.Sequencing;

/// <summary>Waits for a fixed time. Needs no devices, so it lives next to the other generic sequencing steps.</summary>
public sealed class DelayAction : ISequenceStep
{
    public DelayAction(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        Duration = duration;
    }

    public TimeSpan Duration { get; }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Wait {Duration.TotalSeconds:0.##}s");

    /// <summary>Waits with <see cref="Task.Delay(TimeSpan, CancellationToken)"/>; cancellation throws and reports no result.</summary>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(Duration, cancellationToken);
        return new SequenceStepResult();
    }
}
