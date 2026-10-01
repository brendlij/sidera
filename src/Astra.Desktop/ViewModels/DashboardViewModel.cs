namespace Astra.Desktop.ViewModels;

/// <summary>
/// The overview page. It owns nothing: it points at the view models that hold the real state, so the summary
/// can never disagree with the pages behind it.
/// </summary>
public sealed class DashboardViewModel(
    RuntimeStatusViewModel runtime,
    SequencerViewModel sequencer,
    ImagingViewModel imaging,
    RigViewModel? mainRig,
    CameraViewModel? camera,
    MountViewModel? mount,
    GuiderViewModel? guider
) : ViewModelBase
{
    public RuntimeStatusViewModel Runtime { get; } = runtime;
    public SequencerViewModel Sequencer { get; } = sequencer;
    public ImagingViewModel Imaging { get; } = imaging;

    /// <summary>The first rig; the mount and the guider next to it are shared equipment, not part of the rig.</summary>
    public RigViewModel? MainRig { get; } = mainRig;

    public CameraViewModel? Camera { get; } = camera;
    public MountViewModel? Mount { get; } = mount;
    public GuiderViewModel? Guider { get; } = guider;
}
