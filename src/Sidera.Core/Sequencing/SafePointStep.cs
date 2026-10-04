namespace Sidera.Core.Sequencing;

/// <summary>
/// Marks a place where a branch is safe to pause for a coordinated operation. It does no work itself:
/// it continues at once unless another branch has asked for a coordinated operation, in which case it
/// waits here until that operation is done. Outside a coordination group it does nothing.
/// </summary>
public sealed class SafePointStep : ISequenceStep
{
    public string Name => "Safe Point";

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        await context.ReachSafePointAsync(cancellationToken);
        return new SequenceStepResult();
    }
}
