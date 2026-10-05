using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Framing;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Settings;
using Sidera.Runtime;
using Sidera.Runtime.Astrometry;
using Sidera.Sky;

namespace Sidera.Desktop.ViewModels;

/// <summary>A drag of the picture, in pixels of the sky view.</summary>
public sealed record ViewDelta(double Dx, double Dy);

/// <summary>
/// The framing workspace: choose an object, a rig and a survey, see the field of the rig on the sky, drag it and turn it, and use the result: slew and center on it, or put it into
/// the session. It plans; it does not drive hardware by itself: searching, dragging, turning and loading imagery touch nothing, and the one action that moves the mount
/// (<see cref="SlewAndCenterCommand"/>) is the existing centering, which never synchronizes the mount and never turns anything. The desired rotation is a plan: it is compared with the
/// rotation of a plate solve after centering, and nothing is rotated.
/// </summary>
public sealed partial class FramingViewModel : ViewModelBase, IDisposable
{
    public const int ViewWidthPixels = 960;
    public const int ViewHeightPixels = 640;
    private const double MinimumFieldDegrees = 0.05;
    private const double MaximumFieldDegrees = 60;

    private readonly SideraRuntimeHost _host;
    private readonly SiteService? _settings;
    private readonly SequenceDraftViewModel? _session;
    private readonly ICelestialObjectCatalog? _catalog;
    private readonly Func<SkySurveyDescriptor, ISkySurveyProvider>? _providers;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, ISkySurveyProvider> _providerCache = [];
    private CancellationTokenSource? _imageLoad;
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _centering;
    private bool _settingRotationText;

    public FramingViewModel(
        SideraRuntimeHost host, SiteService? settings, SequenceDraftViewModel? session, ICelestialObjectCatalog? catalog,
        Func<SkySurveyDescriptor, ISkySurveyProvider>? providers, Action<Action> post)
    {
        _host = host;
        _settings = settings;
        _session = session;
        _catalog = catalog;
        _providers = providers;
        _post = post;
        var defaultId = settings?.SkyAtlas.DefaultSurveyId ?? SkySurveys.DefaultId;
        SelectedSurvey = Surveys.FirstOrDefault(s => s.Id == defaultId) ?? Surveys[0];
        ViewCenter = new CelestialCoordinates(0, 0);
        RefreshEquipment();
    }

    // ---- What can be chosen

    public IReadOnlyList<SkySurveyDescriptor> Surveys { get; } = SkySurveys.Defaults;

    public ObservableCollection<Rig> Rigs { get; } = [];

    public ObservableCollection<IMount> Mounts { get; } = [];

    public ObservableCollection<CelestialObject> Results { get; } = [];

    [ObservableProperty] public partial Rig? SelectedRig { get; set; }

    [ObservableProperty] public partial IMount? SelectedMount { get; set; }

    [ObservableProperty] public partial SkySurveyDescriptor SelectedSurvey { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty] public partial CelestialObject? SelectedResult { get; set; }

    [ObservableProperty] public partial string SearchStatus { get; private set; } = string.Empty;

    /// <summary>A search is running (shows a small progress bar by the search box).</summary>
    [ObservableProperty] public partial bool IsSearching { get; private set; }

    /// <summary>How much the sky picture is lifted, 0 to 1; the survey's pixels stay as they are.</summary>
    [ObservableProperty] public partial double Brightness { get; set; }

    /// <summary>
    /// The suggestions while typing, for the drop-down of the search box: the objects whose names match what is typed so far. Called off the UI thread, after a pause in the typing, and
    /// cancelled by the next keystroke; a catalog that cannot answer gives an empty list.
    /// </summary>
    public async Task<IEnumerable<object>> SuggestAsync(string? text, CancellationToken cancellationToken)
    {
        var query = (text ?? string.Empty).Trim();
        if (_catalog is null || query.Length < 2 || cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        try
        {
            return [.. await _catalog.SearchAsync(query, 8, cancellationToken)];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    public Func<string?, CancellationToken, Task<IEnumerable<object>>> Suggest => SuggestAsync;

    // ---- The plan and the view

    /// <summary>The framing being planned; <c>null</c> until an object or a place was chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget), nameof(CenterText), nameof(TargetName))]
    public partial FramingTarget? Target { get; private set; }

    public bool HasTarget => Target is not null;

    partial void OnTargetChanged(FramingTarget? value) => UpdateSlewState();
    partial void OnIsBusyChanged(bool value) => SlewAndCenterCommand.NotifyCanExecuteChanged();

    public string TargetName => Target?.Name ?? "No target";

    /// <summary>What the picture is centered on: where the person looked, which is not the center of the framing once it was dragged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Viewport))]
    public partial CelestialCoordinates ViewCenter { get; private set; }

    /// <summary>The width of the picture in degrees.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Viewport))]
    public partial double FieldDegrees { get; private set; } = 3;

    public SkyViewport Viewport => new(ViewCenter, FieldDegrees / ViewWidthPixels, ViewWidthPixels, ViewHeightPixels);

    /// <summary>The field of the selected rig as it is configured now; <c>null</c> while it is not known (no focal length, no sensor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FovText), nameof(PixelScaleText), nameof(HasField))]
    public partial RigField? Field { get; private set; }

    public bool HasField => Field is not null;

    public string FovText => Field is { } f ? Format($"{f.WidthDegrees:0.00}° × {f.HeightDegrees:0.00}°") : "Not known: set the focal length of the rig and connect its camera";

    [ObservableProperty] public partial string PixelScaleTextValue { get; private set; } = "—";

    public string PixelScaleText => PixelScaleTextValue;

    public string CenterText => Target is { } t
        ? Format($"RA {SkyMath.HoursToDegrees(t.Center.RightAscensionHours) / 15:0.#####} h · Dec {t.Center.DeclinationDegrees:+0.#####;-0.#####;0}°")
        : "—";

    /// <summary>The desired rotation as typed; a rotation that is not a number is shown as a problem and does not change the plan.</summary>
    [ObservableProperty] public partial string RotationText { get; set; } = "0";

    [ObservableProperty] public partial string ProblemText { get; private set; } = string.Empty;

    // ---- The imagery

    [ObservableProperty] public partial SkyImage? Image { get; private set; }

    [ObservableProperty] public partial SkyViewport? ImageViewport { get; private set; }

    [ObservableProperty] public partial long ImageVersion { get; private set; }

    [ObservableProperty] public partial bool IsLoadingImage { get; private set; }

    /// <summary>What to say about the imagery: that tiles are missing, that there is none; empty when the picture is complete.</summary>
    [ObservableProperty] public partial string ImageNote { get; private set; } = string.Empty;

    /// <summary>The compact attribution of the survey: "DSS colored · © ... · License ...".</summary>
    [ObservableProperty] public partial string AttributionText { get; private set; } = string.Empty;

    // ---- Using the plan

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string PositionText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string RotationCompareText { get; private set; } = string.Empty;

    /// <summary>Why the mount cannot be slewed; empty when it can.</summary>
    [ObservableProperty] public partial string SlewDisabledText { get; private set; } = string.Empty;

    public bool CanSearch => _catalog is not null;

    // ---- Equipment

    /// <summary>Reads the rigs and the mounts again (the page was opened, equipment came or went).</summary>
    [RelayCommand]
    public void RefreshEquipment()
    {
        var rigId = SelectedRig?.Id;
        var mountId = SelectedMount?.Id;
        Rigs.Clear();
        foreach (var rig in _host.RigRegistry.GetAll().OrderBy(r => r.Id.Value, StringComparer.Ordinal))
        {
            Rigs.Add(rig);
        }

        Mounts.Clear();
        foreach (var mount in _host.DeviceRegistry.GetAll().OfType<IMount>())
        {
            Mounts.Add(mount);
        }

        SelectedRig = Rigs.FirstOrDefault(r => r.Id == rigId) ?? Rigs.FirstOrDefault();
        SelectedMount = Mounts.FirstOrDefault(m => m.Id == mountId) ?? Mounts.FirstOrDefault();
        RefreshField();
        UpdateSlewState();
    }

    partial void OnSelectedRigChanged(Rig? value)
    {
        // The field follows the rig at once: nothing of it is kept in the plan.
        RefreshField();
        if (Target is { } target)
        {
            Target = target.WithRig(value?.Id);
        }

        UpdateSlewState();
    }

    partial void OnSelectedMountChanged(IMount? value) => UpdateSlewState();

    partial void OnSelectedSurveyChanged(SkySurveyDescriptor value)
    {
        if (Target is { } target)
        {
            Target = new FramingTarget(target.Name, target.Center, target.DesiredRotationDegrees, target.RigId, target.CatalogId, value.Id);
        }

        ScheduleImageLoad();
    }

    /// <summary>Reads the geometry of the selected rig: its configured optics, and what its camera reports for the rest.</summary>
    public void RefreshField()
    {
        if (SelectedRig is not { } rig)
        {
            Field = null;
            PixelScaleTextValue = "—";
            return;
        }

        _host.DeviceRegistry.TryGet(rig.CameraId, out var camera);
        var geometry = OpticalTrainGeometry.Resolve(rig.Optics, SensorGeometry.From((camera as ICameraControl)?.Capabilities.Value));
        Field = RigField.From(geometry);
        PixelScaleTextValue = RigViewModel.PixelScaleOf(geometry);
    }

    // ---- Search

    [RelayCommand]
    private async Task SearchAsync()
    {
        _search?.Cancel();
        var query = SearchText.Trim();
        Results.Clear();
        if (_catalog is null || query.Length == 0)
        {
            SearchStatus = _catalog is null ? "No object catalog is available." : string.Empty;
            return;
        }

        var source = _search = new CancellationTokenSource();
        SearchStatus = "Searching…";
        IsSearching = true;
        try
        {
            var found = await _catalog.SearchAsync(query, 8, source.Token);
            if (source.IsCancellationRequested)
            {
                return;
            }

            foreach (var item in found)
            {
                Results.Add(item);
            }

            SearchStatus = found.Count == 0 ? $"Nothing found for “{query}”. Without a network only the local list is searched." : string.Empty;
            if (found.Count > 0)
            {
                SelectedResult = found[0];
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_search, source))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>Choosing an object centers the atlas on it and plans a framing there with the selected rig. It does not touch the mount.</summary>
    partial void OnSelectedResultChanged(CelestialObject? value)
    {
        if (value is null)
        {
            return;
        }

        SetTarget(value.Name, value.Position, value.Name);
        FieldDegrees = Math.Clamp(Field is { } f ? Math.Max(f.WidthDegrees, f.HeightDegrees) * 2.5 : 3, MinimumFieldDegrees, MaximumFieldDegrees);
        ViewCenter = value.Position;
        ScheduleImageLoad();
    }

    private void SetTarget(string name, CelestialCoordinates center, string? catalogId)
    {
        var rotation = Target?.DesiredRotationDegrees ?? 0;
        Target = new FramingTarget(name, center, rotation, SelectedRig?.Id, catalogId, SelectedSurvey.Id);
        UpdateSlewState();
    }

    // ---- The view: panning, zooming, dragging and turning the frame

    /// <summary>The frame is dragged to a position of the sky: the plan follows. The mount is not touched.</summary>
    public void MoveTarget(CelestialCoordinates center)
    {
        if (Target is null)
        {
            return;
        }

        Target = Target.WithCenter(center);
        OnPropertyChanged(nameof(Viewport));
    }

    [RelayCommand]
    private void MoveTargetTo(CelestialCoordinates? center)
    {
        if (center is not null)
        {
            MoveTarget(center);
        }
    }

    /// <summary>The picture is dragged by a number of pixels; imagery for the new place is loaded after a pause.</summary>
    [RelayCommand]
    private void Pan(ViewDelta? delta)
    {
        if (delta is null)
        {
            return;
        }

        ViewCenter = Viewport.Panned(delta.Dx, delta.Dy).Center;
        ScheduleImageLoad();
    }

    /// <summary>The picture is zoomed: a factor under 1 shows less sky.</summary>
    [RelayCommand]
    private void Zoom(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        FieldDegrees = Math.Clamp(FieldDegrees * factor, MinimumFieldDegrees, MaximumFieldDegrees);
        ScheduleImageLoad();
    }

    /// <summary>The frame is turned by some degrees (counterclockwise is positive, as a rotation is everywhere in Sidera).</summary>
    [RelayCommand]
    private void RotateBy(double degrees)
    {
        if (Target is { } target && double.IsFinite(degrees))
        {
            SetRotation(target.DesiredRotationDegrees + degrees);
        }
    }

    private void SetRotation(double degrees)
    {
        if (Target is null)
        {
            return;
        }

        Target = Target.WithRotation(degrees);
        _settingRotationText = true;
        RotationText = Format($"{Target.DesiredRotationDegrees:0.##}");
        _settingRotationText = false;
        ProblemText = string.Empty;
        OnPropertyChanged(nameof(Viewport));
    }

    partial void OnRotationTextChanged(string value)
    {
        if (_settingRotationText || Target is null)
        {
            return;
        }

        if (double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) && double.IsFinite(degrees))
        {
            Target = Target.WithRotation(degrees);
            ProblemText = string.Empty;
            OnPropertyChanged(nameof(Viewport));
        }
        else
        {
            ProblemText = "The rotation must be a number of degrees.";
        }
    }

    // ---- Imagery

    private ISkySurveyProvider? ProviderFor(SkySurveyDescriptor survey)
    {
        if (_providers is null)
        {
            return null;
        }

        if (!_providerCache.TryGetValue(survey.Id, out var provider))
        {
            provider = _providerCache[survey.Id] = _providers(survey);
        }

        return provider;
    }

    /// <summary>Loads the imagery of the current view after a short pause; a newer view cancels the load that is running, so a drag is never behind.</summary>
    public void ScheduleImageLoad()
    {
        _imageLoad?.Cancel();
        var provider = ProviderFor(SelectedSurvey);
        if (provider is null || Target is null)
        {
            ImageNote = provider is null ? "No sky survey is available." : string.Empty;
            return;
        }

        var source = _imageLoad = new CancellationTokenSource();
        var viewport = Viewport;
        IsLoadingImage = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120), source.Token);
                var image = await provider.GetImageAsync(viewport, source.Token);
                if (source.IsCancellationRequested)
                {
                    return;
                }

                _post(() =>
                {
                    if (source.IsCancellationRequested)
                    {
                        return;
                    }

                    Image = image;
                    ImageViewport = viewport;
                    ImageVersion++;
                    ImageNote = image.Note ?? string.Empty;
                    AttributionText = image.Info?.AttributionLine ?? SelectedSurvey.Title;
                    IsLoadingImage = false;
                });
            }
            catch (OperationCanceledException)
            {
                // Replaced by a newer view.
            }
            catch (Exception ex)
            {
                _post(() =>
                {
                    ImageNote = "The imagery could not be loaded: " + ex.Message;
                    IsLoadingImage = false;
                });
            }
        });
    }

    // ---- Using the plan

    private PlateSolvingSettings SolveSettings => _settings?.PlateSolving ?? new PlateSolvingSettings();

    private void UpdateSlewState()
    {
        SlewDisabledText =
            _host.PlateSolving is null ? "No plate solver is configured."
            : SelectedRig is null ? "Select a rig."
            : SelectedMount is null ? "No mount is available."
            : SelectedMount.ConnectionState != DeviceConnectionState.Connected ? "Connect the mount to slew."
            : Target is null ? "Choose a target first."
            : string.Empty;
        SlewAndCenterCommand.NotifyCanExecuteChanged();
        AddToSessionCommand.NotifyCanExecuteChanged();
    }

    private bool CanSlew() => !IsBusy && SlewDisabledText.Length == 0;

    /// <summary>
    /// Slews to the center of the framing and centers it: the existing centering of the plate solver. Position only: nothing is synchronized and nothing is rotated. After it
    /// the rotation that the last solve found is shown next to the desired one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSlew))]
    private async Task SlewAndCenterAsync()
    {
        if (Target is not { } target || SelectedRig is not { } rig || SelectedMount is not { } mount || _host.PlateSolving is not { } service)
        {
            return;
        }

        IsBusy = true;
        _centering = new CancellationTokenSource();
        PositionText = string.Empty;
        RotationCompareText = string.Empty;
        StatusText = "Slewing…";
        SlewAndCenterCommand.NotifyCanExecuteChanged();
        try
        {
            var solve = SolveSettings;
            var progress = new Progress<CenteringProgress>(p => _post(() => StatusText = p.PointingErrorArcseconds is { } error
                ? Format($"{p.Stage} · attempt {p.Attempt} · error {error:0.#}\"")
                : Format($"{p.Stage} · attempt {p.Attempt}")));
            var result = await service.CenterTargetAsync(
                target.Center, rig, mount.Id, solve.CenteringToleranceArcseconds, solve.MaxCenteringAttempts, TimeSpan.FromSeconds(solve.ExposureSeconds),
                solve.Defaults(), progress: progress, cancellationToken: _centering.Token);

            PositionText = result.Success
                ? Format($"Centered · {result.PointingErrorArcseconds:0}\"")
                : result.Message ?? "Centering failed.";
            StatusText = result.Success ? "Centered" : "Not centered";
            if (service.LastResult is { Success: true, RotationDegrees: { } solved })
            {
                RotationCompareText = Format($"Target {target.DesiredRotationDegrees:0.##}°\nCurrent {solved:0.##}°\nDifference {target.RotationDifferenceDegrees(solved):+0.##;-0.##;0}°");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _centering?.Dispose();
            _centering = null;
            IsBusy = false;
            UpdateSlewState();
        }
    }

    [RelayCommand]
    private void Cancel() => _centering?.Cancel();

    /// <summary>
    /// Puts a Slew &amp; Center on the framing into the session: the position, the rig and the desired rotation as metadata of the step. No sync step is added, and no rotation
    /// step, because there is no rotator.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddToSession))]
    private void AddToSession()
    {
        if (Target is not { } target || _session is null)
        {
            return;
        }

        var solve = SolveSettings;
        var step = new SlewAndCenterStepDraft(
            Guid.NewGuid(), SelectedMount?.Id, SelectedRig?.Id, target.Center.RightAscensionHours, target.Center.DeclinationDegrees,
            solve.CenteringToleranceArcseconds, solve.MaxCenteringAttempts, solve.ExposureSeconds, target.Name, target.DesiredRotationDegrees);
        StatusText = _session.AddStepDraft(step)
            ? $"Added Slew & Center for {target.Name} to the session."
            : "The session cannot be changed while it runs.";
    }

    private bool CanAddToSession() => Target is not null && _session is not null;

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _imageLoad?.Cancel();
        _search?.Cancel();
        _centering?.Cancel();
        foreach (var provider in _providerCache.Values.OfType<IDisposable>())
        {
            provider.Dispose();
        }
    }
}
