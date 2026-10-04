using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Astrometry;

namespace Sidera.Runtime.Sequencing;

public sealed class PlateSolveAction(PlateSolveService service, Rig rig, DeviceId? mountId,
    TimeSpan exposure, PlateSolveDefaults defaults, AcquisitionIntent? intent = null) : IResourceAwareSequenceStep
{
    public string Name => "Plate Solve";
    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(rig.CameraId)];
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var result = await service.CaptureAndSolveWithLeaseAsync(rig, mountId, exposure, defaults, intent, cancellationToken: cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Message ?? "Plate solving failed.");
        return new SequenceStepResult(result);
    }
}
