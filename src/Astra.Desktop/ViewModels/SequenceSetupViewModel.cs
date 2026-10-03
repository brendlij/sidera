using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.ViewModels;

/// <summary>A device a user can pick: its friendly name, with the id as secondary text.</summary>
public sealed record DeviceOption(DeviceId Id, string Name)
{
    public string IdText => Id.Value;
}

/// <summary>
/// The editable parameters of the demo sequence, as text boxes and pickers. Typing never throws: every change
/// re-reads all fields, and either yields a valid <see cref="DemoSequenceConfiguration"/> or a short list of what is
/// wrong. Only a valid configuration is turned into sequence objects (the preview, and each run); text that does not
/// parse is shown as an error and is never silently replaced by a number.
/// <para>
/// Numbers are accepted with the decimal separator of the user's culture or a dot. Defaults are shown with a dot.
/// </para>
/// </summary>
public sealed partial class SequenceSetupViewModel : ViewModelBase
{
    private static readonly HashSet<string> InputProperties =
    [
        nameof(SelectedCamera), nameof(SelectedMount), nameof(SelectedGuider),
        nameof(RightAscensionText), nameof(DeclinationText),
        nameof(ExposureText), nameof(ImagingRepeatText),
        nameof(DitherRepeatText), nameof(DitherDelayText), nameof(DitherAmplitudeText),
        nameof(SettleThresholdText), nameof(SettleStableText), nameof(SettleTimeoutText),
    ];

    private readonly DeviceRegistry _registry;
    private DemoSequenceConfiguration? _previewConfiguration;

    public SequenceSetupViewModel(DeviceRegistry registry, DemoSequenceConfiguration initial)
    {
        _registry = registry;
        var devices = registry.GetAll();
        Cameras = Options<ICamera>(devices);
        Mounts = Options<IMount>(devices);
        Guiders = Options<IGuider>(devices);

        SelectedCamera = Cameras.FirstOrDefault(o => o.Id == initial.CameraId);
        SelectedMount = Mounts.FirstOrDefault(o => o.Id == initial.MountId);
        SelectedGuider = Guiders.FirstOrDefault(o => o.Id == initial.GuiderId);
        RightAscensionText = Format(initial.RightAscensionHours);
        DeclinationText = Format(initial.DeclinationDegrees);
        ExposureText = Format(initial.ExposureSeconds);
        ImagingRepeatText = initial.ImagingRepeatCount.ToString(CultureInfo.InvariantCulture);
        DitherRepeatText = initial.DitherRepeatCount.ToString(CultureInfo.InvariantCulture);
        DitherDelayText = Format(initial.DitherDelaySeconds);
        DitherAmplitudeText = Format(initial.DitherAmplitudePixels);
        SettleThresholdText = Format(initial.SettleThresholdPixels);
        SettleStableText = Format(initial.SettleStableSeconds);
        SettleTimeoutText = Format(initial.SettleTimeoutSeconds);

        Revalidate();
    }

    public IReadOnlyList<DeviceOption> Cameras { get; }
    public IReadOnlyList<DeviceOption> Mounts { get; }
    public IReadOnlyList<DeviceOption> Guiders { get; }

    [ObservableProperty]
    public partial DeviceOption? SelectedCamera { get; set; }

    [ObservableProperty]
    public partial DeviceOption? SelectedMount { get; set; }

    [ObservableProperty]
    public partial DeviceOption? SelectedGuider { get; set; }

    /// <summary>Right ascension of the slew target, in hours.</summary>
    [ObservableProperty]
    public partial string RightAscensionText { get; set; } = string.Empty;

    /// <summary>Declination of the slew target, in degrees.</summary>
    [ObservableProperty]
    public partial string DeclinationText { get; set; } = string.Empty;

    /// <summary>Exposure time in seconds.</summary>
    [ObservableProperty]
    public partial string ExposureText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImagingRepeatText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DitherRepeatText { get; set; } = string.Empty;

    /// <summary>The wait before each dither, in seconds.</summary>
    [ObservableProperty]
    public partial string DitherDelayText { get; set; } = string.Empty;

    /// <summary>Dither amplitude in guide camera pixels.</summary>
    [ObservableProperty]
    public partial string DitherAmplitudeText { get; set; } = string.Empty;

    /// <summary>Guide error, in guide camera pixels, at or below which guiding counts as settled.</summary>
    [ObservableProperty]
    public partial string SettleThresholdText { get; set; } = string.Empty;

    /// <summary>How long the guide error must stay within the threshold, in seconds.</summary>
    [ObservableProperty]
    public partial string SettleStableText { get; set; } = string.Empty;

    /// <summary>How long to wait for guiding to settle before giving up, in seconds.</summary>
    [ObservableProperty]
    public partial string SettleTimeoutText { get; set; } = string.Empty;

    /// <summary>False while a sequence runs (also while it pauses): the parameters of a run cannot be changed.</summary>
    [ObservableProperty]
    public partial bool IsEditable { get; set; } = true;

    /// <summary>What is wrong with the parameters right now; empty when they make a valid sequence.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationErrors))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial IReadOnlyList<string> ValidationErrors { get; private set; } = [];

    public bool HasValidationErrors => ValidationErrors.Count > 0;
    public bool IsValid => ValidationErrors.Count == 0;

    /// <summary>The parameters as values, when they are valid.</summary>
    public DemoSequenceConfiguration? Configuration { get; private set; }

    /// <summary>
    /// What the parameters would run, as a sequence definition for display: always built from the latest valid
    /// parameters, and only a picture: runs build their own sequence. <c>null</c> until there was a valid one.
    /// </summary>
    public Sequence? Preview { get; private set; }

    /// <summary>Raised after every re-evaluation of the parameters.</summary>
    public event EventHandler? Changed;

    /// <summary>Builds a new sequence from the current, validated parameters.</summary>
    /// <exception cref="SequenceConfigurationException">The parameters are not valid.</exception>
    public Sequence Build()
    {
        Revalidate();
        if (Configuration is null)
        {
            throw new SequenceConfigurationException(ValidationErrors);
        }

        return DemoSequenceBuilder.Build(_registry, Configuration);
    }

    /// <summary>Reads all fields again. Also catches a device that disappeared since the last time.</summary>
    public void Revalidate()
    {
        var errors = new List<string>();
        var defaults = new DemoSequenceConfiguration();

        var ra = Parse(RightAscensionText, "Right ascension", "a number of hours", errors, defaults.RightAscensionHours);
        var dec = Parse(DeclinationText, "Declination", "a number of degrees", errors, defaults.DeclinationDegrees);
        var exposure = Parse(ExposureText, "Exposure", "a number of seconds", errors, defaults.ExposureSeconds);
        var imagingRepeat = ParseCount(ImagingRepeatText, "Imaging repeat count", errors, defaults.ImagingRepeatCount);
        var ditherRepeat = ParseCount(DitherRepeatText, "Dither repeat count", errors, defaults.DitherRepeatCount);
        var delay = Parse(DitherDelayText, "Wait before dither", "a number of seconds", errors, defaults.DitherDelaySeconds);
        var amplitude = Parse(DitherAmplitudeText, "Dither amplitude", "a number of pixels", errors, defaults.DitherAmplitudePixels);
        var threshold = Parse(SettleThresholdText, "Settle threshold", "a number of pixels", errors, defaults.SettleThresholdPixels);
        var stable = Parse(SettleStableText, "Settle stable time", "a number of seconds", errors, defaults.SettleStableSeconds);
        var timeout = Parse(SettleTimeoutText, "Settle timeout", "a number of seconds", errors, defaults.SettleTimeoutSeconds);

        var configuration = new DemoSequenceConfiguration
        {
            CameraId = SelectedCamera?.Id,
            MountId = SelectedMount?.Id,
            GuiderId = SelectedGuider?.Id,
            RightAscensionHours = ra,
            DeclinationDegrees = dec,
            ExposureSeconds = exposure,
            ImagingRepeatCount = imagingRepeat,
            DitherRepeatCount = ditherRepeat,
            DitherDelaySeconds = delay,
            DitherAmplitudePixels = amplitude,
            SettleThresholdPixels = threshold,
            SettleStableSeconds = stable,
            SettleTimeoutSeconds = timeout,
        };

        // Fields that did not parse were replaced by valid defaults above, so only the other problems are added.
        errors.AddRange(DemoSequenceBuilder.Validate(_registry, configuration));

        Configuration = errors.Count == 0 ? configuration : null;
        if (Configuration is not null && Configuration != _previewConfiguration)
        {
            // Only objects of a valid configuration are ever built, and only when it actually changed.
            Preview = DemoSequenceBuilder.Build(_registry, Configuration);
            _previewConfiguration = Configuration;
        }

        if (!errors.SequenceEqual(ValidationErrors))
        {
            ValidationErrors = errors;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not null && InputProperties.Contains(e.PropertyName))
        {
            Revalidate();
        }
    }

    private static IReadOnlyList<DeviceOption> Options<T>(IReadOnlyCollection<IDevice> devices) where T : class, IDevice =>
        devices.OfType<T>().OrderBy(d => d.Id.Value, StringComparer.Ordinal).Select(d => new DeviceOption(d.Id, d.Name)).ToList();

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // The user's own decimal separator, or a dot.
    private static double Parse(string? text, string label, string expected, List<string> errors, double fallback)
    {
        var trimmed = text?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value))
        {
            return value;
        }

        errors.Add($"{label} must be {expected}.");
        return fallback;
    }

    private static int ParseCount(string? text, string label, List<string> errors, int fallback)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            || int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        errors.Add($"{label} must be a whole number.");
        return fallback;
    }
}
