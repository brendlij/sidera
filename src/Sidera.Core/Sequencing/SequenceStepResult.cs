namespace Sidera.Core.Sequencing;

/// <summary>
/// What one execution of one sequence step produced. Every execution creates its own instance;
/// the step definition itself keeps no result. The step's index and name are not repeated here,
/// the runner reports them alongside the result.
/// </summary>
public sealed class SequenceStepResult
{
    /// <param name="payload">What the step produced (for example a <see cref="Devices.CameraFrame"/>), or <c>null</c>.</param>
    public SequenceStepResult(object? payload = null)
    {
        Payload = payload;
    }

    public object? Payload { get; }
}
