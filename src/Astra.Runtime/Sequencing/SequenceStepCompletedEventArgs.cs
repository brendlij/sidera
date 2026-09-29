using Astra.Core.Sequencing;

namespace Astra.Runtime.Sequencing;

public sealed class SequenceStepCompletedEventArgs(int stepIndex, string stepName, SequenceStepResult result)
    : EventArgs
{
    public int StepIndex { get; } = stepIndex;
    public string StepName { get; } = stepName;
    public SequenceStepResult Result { get; } = result;
}
