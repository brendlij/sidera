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

/// <summary>
/// Progress that is handed on at once, on the thread of the operation (the handler posts to the UI itself). <see cref="Progress{T}"/> would queue the report and could deliver it after
/// the operation has ended, over its final status.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

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
    private readonly ImagingSetupContext _context;
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
        SideraRuntimeHost host, ImagingSetupContext context, SiteService? settings, SequenceDraftViewModel? session, ICelestialObjectCatalog? catalog,
        Func<SkySurveyDescriptor, ISkySurveyProvider>? providers, Action<Action> post)
    {
        _host = host;
        _context = context;
        _context.Changed += (_, _) => RefreshEquipment();
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

    public ObservableCollection<IMount> Mounts { get; } = [];

    public ObservableCollection<CelestialObject> Results { get; } = [];

    /// <summary>The current imaging setup: its optics give the field, its mount slews. Nothing is chosen on this page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetup), nameof(SetupName))]
    public partial Rig? Setup { get; private set; }

    public bool HasSetup => Setup is not null;

    public string SetupName => Setup?.Name ?? string.Empty;

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
    partial void OnIsBusyChanged(bool value)
    {
        SlewAndCenterCommand.NotifyCanExecuteChanged();
        CenterAndRotateCommand.NotifyCanExecuteChanged();
        SolveAgainCommand.NotifyCanExecuteChanged();
    }

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

    public string FovText => Field is { } f ? Format($"{f.WidthDegrees:0.00}° × {f.HeightDegrees:0.00}°") : "Not known: set the focal length of the imaging setup and connect its camera";

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

    /// <summary>Reads the current setup and the mounts again (the page was opened, equipment came or went, another setup became the current one).</summary>
    [RelayCommand]
    public void RefreshEquipment()
    {
        var mountId = SelectedMount?.Id;
        Mounts.Clear();
        foreach (var mount in _host.DeviceRegistry.GetAll().OfType<IMount>())
        {
            Mounts.Add(mount);
        }

        // The mount: the one of the setup; else the one that was chosen; else the only one. Never the first of several.
        var setup = _context.Current;
        SelectedMount = setup?.MountId is { } setupMount ? Mounts.FirstOrDefault(m => m.Id == setupMount)
            : Mounts.FirstOrDefault(m => m.Id == mountId) ?? (Mounts.Count == 1 ? Mounts[0] : null);
        if (!ReferenceEquals(setup, Setup))
        {
            Setup = setup;
            if (Target is { } target)
            {
                Target = target.WithRig(setup?.Id);
            }
        }

        RefreshField();
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

    /// <summary>Reads the geometry of the current setup: its configured optics, and what its camera reports for the rest.</summary>
    public void RefreshField()
    {
        if (Setup is not { } rig)
        {
            Field = null;
            PixelScaleTextValue = "—";
            return;
        }

        _host.DeviceRegistry.TryGet(rig.CameraId, out var camera);
        var geometry = OpticalTrainGeometry.Resolve(rig.Optics, SensorGeometry.For(camera));
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
        Target = new FramingTarget(name, center, rotation, Setup?.Id, catalogId, SelectedSurvey.Id);
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

    // The current setup is a snapshot; its rotator and the calibration of it can change while this page is open, so the registry is asked again.
    private Rig? CurrentRig => Setup is { } selected && _host.RigRegistry.TryGet(selected.Id, out var current) && current is not null ? current : Setup;

    /// <summary>The current setup has a rotator: the page then offers Center &amp; Rotate. Otherwise Slew &amp; Center, and a rotation that is made by hand.</summary>
    public bool HasRotator => CurrentRig?.RotatorId is not null;

    public bool HasNoRotator => !HasRotator;

    /// <summary>For a setup without a rotator: how far the sky is from the desired rotation, as signed degrees to change it by; empty before a solve.</summary>
    [ObservableProperty] public partial string RotationAdjustmentText { get; private set; } = string.Empty;

    private string RotatorProblem(Rig rig)
    {
        if (rig.RotatorId is not { } rotatorId)
        {
            return string.Empty;
        }

        return !_host.DeviceRegistry.TryGet(rotatorId, out var rotator) || rotator is null || rotator.ConnectionState != DeviceConnectionState.Connected ? "Connect the rotator to rotate."
            : rig.RotatorModel is null ? "Calibrate the rotator first (Equipment, Rotator): its position says nothing about the sky until then."
            : string.Empty;
    }

    private void UpdateSlewState()
    {
        SlewDisabledText =
            _host.PlateSolving is null ? "No plate solver is configured."
            : Setup is null ? _context.NoSetupText
            : SelectedMount is null ? "No mount is available."
            : SelectedMount.ConnectionState != DeviceConnectionState.Connected ? "Connect the mount to slew."
            : Target is null ? "Choose a target first."
            : CurrentRig is { RotatorId: not null } withRotator ? RotatorProblem(withRotator)
            : string.Empty;
        OnPropertyChanged(nameof(HasRotator));
        OnPropertyChanged(nameof(HasNoRotator));
        SlewAndCenterCommand.NotifyCanExecuteChanged();
        CenterAndRotateCommand.NotifyCanExecuteChanged();
        SolveAgainCommand.NotifyCanExecuteChanged();
        AddToSessionCommand.NotifyCanExecuteChanged();
        RefreshCurrent();
    }

    private bool CanSlew() => !IsBusy && SlewDisabledText.Length == 0;

    // ---- Where the rig points now

    /// <summary>
    /// The field of the rig as it is now, drawn next to the planned framing: centered where the mount of the rig says it points (live: it follows a slew), or where the latest plate solve
    /// found the camera while the mount has not moved since, with the rotation of that solve. <c>null</c> without a connected mount and without a solve. Read by <see cref="RefreshCurrent"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent), nameof(CurrentText))]
    public partial CurrentView? Current { get; private set; }

    public bool HasCurrent => Current is not null;

    /// <summary>One line for the legend: where the rig points, where that comes from, and the rotation or that it is unknown.</summary>
    public string CurrentText => Current is not { } c
        ? "Current field: unknown (connect the mount or solve)"
        : Format($"Current field: RA {c.Center.RightAscensionHours:0.###} h · Dec {c.Center.DeclinationDegrees:+0.##;-0.##;0}° · {(c.Source == CurrentViewSource.Solved ? "solved position" : "mount position")} · {(c.RotationDegrees is { } r ? "rotation " + r.ToString("0.#", CultureInfo.InvariantCulture) + "° (last solve)" : "rotation unknown")}");

    /// <summary>Reads where the rig points now. Called often (while the page is open) and cheap: it only reads the mount and the last solve, and moves nothing.</summary>
    public void RefreshCurrent()
    {
        var rig = CurrentRig;
        var mount = rig?.MountId is { } id && Mounts.FirstOrDefault(m => m.Id == id) is { } own ? own : SelectedMount;
        CelestialCoordinates? now = null;
        if (mount is { ConnectionState: DeviceConnectionState.Connected })
        {
            try
            {
                now = mount.Coordinates;
            }
            catch (Exception)
            {
                // A mount that does not say where it points: only the solve can tell.
            }
        }

        (CelestialCoordinates Center, double? RotationDegrees)? solved = null;
        CelestialCoordinates? atSolve = null;
        if (_host.PlateSolving is { LastResult: { Success: true, Center: { } center } result } service && rig is not null && service.LastRigId == rig.Id)
        {
            solved = (center, result.RotationDegrees);
            atSolve = service.LastRequest?.ApproximateCenter;
        }

        Current = CurrentViewResolver.Resolve(now, solved, atSolve);
    }

    /// <summary>
    /// Slews to the center of the framing and centers it: the existing centering of the plate solver. Position only: nothing is synchronized and nothing is rotated. After it
    /// the rotation that the last solve found is shown next to the desired one.
    /// </summary>
    /// <summary>What asks before the mount or the rotator moves; <c>null</c> where nothing is asked. Set by the main view model.</summary>
    public HardwareSafetyViewModel? Safety { get; set; }

    [RelayCommand(CanExecute = nameof(CanSlew))]
    private async Task SlewAndCenterAsync()
    {
        if (Target is not { } target || Setup is not { } rig || SelectedMount is not { } mount || _host.PlateSolving is not { } service)
        {
            return;
        }

        if (Safety is not null && !await Safety.ConfirmAsync("Slew & Center", [HardwareSafetyViewModel.NoticeFor(_host.DeviceRegistry, MovingEquipment.Mount, mount.Id)]))
        {
            StatusText = "Cancelled: nothing was moved.";
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
            var progress = new InlineProgress<CenteringProgress>(p => _post(() => StatusText = p.PointingErrorArcseconds is { } error
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
                ShowRotation(target, solved);
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

    // Target, current and difference of the rotation; for a rig without a rotator also what to change the rotation by (signed degrees of sky rotation, nothing about which way the camera turns).
    private void ShowRotation(FramingTarget target, double solved)
    {
        RotationCompareText = Format($"Target {target.DesiredRotationDegrees:0.##}°\nCurrent {solved:0.##}°\nDifference {target.RotationDifferenceDegrees(solved):+0.##;-0.##;0}°");
        RotationAdjustmentText = HasRotator ? string.Empty
            : Format($"Change the sky rotation by {SkyMath.RotationDifferenceDegrees(solved, target.DesiredRotationDegrees):+0.##;-0.##;0}°, then solve again.");
    }

    /// <summary>
    /// For a rig with a rotator: centers the target, rotates the sky to the desired rotation and verifies both with plate solves; centers again when the turn moved the field.
    /// Needs a calibrated, connected rotator. Never synchronizes the mount.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSlew))]
    private async Task CenterAndRotateAsync()
    {
        if (Target is not { } target || CurrentRig is not { RotatorId: not null } rig || SelectedMount is not { } mount || _host.Rotation is not { } service)
        {
            return;
        }

        if (Safety is not null && !await Safety.ConfirmAsync(
                "Center & Rotate",
                [HardwareSafetyViewModel.NoticeFor(_host.DeviceRegistry, MovingEquipment.Mount, mount.Id), HardwareSafetyViewModel.NoticeFor(_host.DeviceRegistry, MovingEquipment.Rotator, rig.RotatorId!.Value)]))
        {
            StatusText = "Cancelled: nothing was moved.";
            return;
        }

        IsBusy = true;
        _centering = new CancellationTokenSource();
        PositionText = string.Empty;
        RotationCompareText = string.Empty;
        RotationAdjustmentText = string.Empty;
        StatusText = "Centering…";
        try
        {
            var solve = SolveSettings;
            var progress = new InlineProgress<RotationProgress>(p => _post(() => StatusText = (p.PointingErrorArcseconds, p.RotationErrorDegrees) switch
            {
                ({ } pointing, _) => Format($"{p.Stage} · attempt {p.Attempt} · error {pointing:0.#}\""),
                (_, { } rotation) => Format($"{p.Stage} · attempt {p.Attempt} · rotation error {rotation:+0.##;-0.##;0}°"),
                _ => Format($"{p.Stage} · attempt {p.Attempt}"),
            }));
            var result = await service.CenterAndRotateAsync(
                target.Center, target.DesiredRotationDegrees, rig, mount.Id, solve.CenteringToleranceArcseconds, solve.RotationToleranceDegrees, solve.MaxCenteringAttempts,
                solve.MaxRotationAttempts, RotationService.DefaultMaxRounds, TimeSpan.FromSeconds(solve.ExposureSeconds), solve.Defaults(), progress: progress, cancellationToken: _centering.Token);

            PositionText = result.PointingErrorArcseconds is { } pointingError ? Format($"{(result.Success ? "Centered" : "Off by")} · {pointingError:0}\"") : result.Message ?? string.Empty;
            if (result.Rotation?.SolvedRotationDegrees is { } solved)
            {
                ShowRotation(target, solved);
            }

            StatusText = result.Success ? "Centered and rotated" : result.Message ?? "Not centered and rotated";
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

    private bool CanSolveAgain() => !IsBusy && Target is not null && Setup is not null && _host.PlateSolving is not null;

    /// <summary>
    /// For a setup without a rotator, after the camera was turned by hand: a plate solve of what the camera sees now, to compare its rotation with the desired one. It moves nothing and
    /// synchronizes nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSolveAgain))]
    private async Task SolveAgainAsync()
    {
        if (Target is not { } target || Setup is not { } rig || _host.PlateSolving is not { } service)
        {
            return;
        }

        IsBusy = true;
        _centering = new CancellationTokenSource();
        StatusText = "Solving…";
        try
        {
            var solve = SolveSettings;
            var result = await service.CaptureAndSolveAsync(
                rig, SelectedMount?.Id, TimeSpan.FromSeconds(solve.ExposureSeconds), solve.Defaults(), cancellationToken: _centering.Token);
            if (result is { Success: true, RotationDegrees: { } solved })
            {
                ShowRotation(target, solved);
                StatusText = "Solved";
            }
            else
            {
                StatusText = result.Message ?? "The solve did not say the rotation.";
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
    /// Puts the framing into the session: a Center &amp; Rotate for a rig with a rotator, otherwise a Slew &amp; Center with the desired rotation as metadata of the step. No sync step is
    /// ever added.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddToSession))]
    private void AddToSession()
    {
        if (Target is not { } target || _session is null)
        {
            return;
        }

        // A session that is a workflow takes the target as its target; a session of explicit steps gets the steps below.
        if (_session.TargetSink?.Invoke(new Sidera.Desktop.ViewModels.SessionTargetRequest(
                target.Name, target.Center.RightAscensionHours, target.Center.DeclinationDegrees, target.DesiredRotationDegrees, Setup?.Id)) is { } handled)
        {
            StatusText = handled;
            return;
        }

        var solve = SolveSettings;
        // A rig with a rotator gets the step that rotates; one without gets the position and the rotation as metadata. Neither adds a Sync step.
        SequenceStepDraft step = HasRotator
            ? new CenterAndRotateStepDraft(
                Guid.NewGuid(), SelectedMount?.Id, Setup?.Id, target.Center.RightAscensionHours, target.Center.DeclinationDegrees, solve.CenteringToleranceArcseconds,
                solve.MaxCenteringAttempts, target.DesiredRotationDegrees, solve.RotationToleranceDegrees, solve.MaxRotationAttempts, RotationService.DefaultMaxRounds,
                solve.ExposureSeconds, target.Name)
            : new SlewAndCenterStepDraft(
                Guid.NewGuid(), SelectedMount?.Id, Setup?.Id, target.Center.RightAscensionHours, target.Center.DeclinationDegrees,
                solve.CenteringToleranceArcseconds, solve.MaxCenteringAttempts, solve.ExposureSeconds, target.Name, target.DesiredRotationDegrees);
        var what = HasRotator ? "Center & Rotate" : "Slew & Center";
        StatusText = _session.AddStepDraft(step)
            ? $"Added {what} for {target.Name} to the session."
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
