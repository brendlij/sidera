using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Sidera.Core.Devices;
using Sidera.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// What the capability-driven panels of a device share: they follow the capabilities and the state that the device reports
/// (never the kind of backend), refresh the state of the device while the panel is shown and the device connected, and
/// keep the preferences of the device. A panel shows only what the device says it supports.
/// </summary>
public abstract partial class DevicePanelViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private readonly IObservableDevice _observable;
    private bool _wasAvailable;
    private int _generation;

    protected DevicePanelViewModel(
        DeviceViewModelBase device, IObservableDevice observable, IDevicePreferenceStore? preferences, TimeSpan? pollInterval)
    {
        Device = device;
        _observable = observable;
        Preferences = preferences;
        device.CommandsRefreshed += OnCommandsRefreshed;
        if (pollInterval is { } interval && interval > TimeSpan.Zero)
        {
            _ = PollAsync(interval, _stop.Token);
        }
    }

    public DeviceViewModelBase Device { get; }

    protected IDevicePreferenceStore? Preferences { get; }

    /// <summary>The panel is on screen: the state of the device is read again regularly.</summary>
    public bool IsShown { get; set; }

    /// <summary>The device reported its capabilities (it is connected); nothing is offered before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    public partial bool IsAvailable { get; private set; }

    public bool IsUnavailable => !IsAvailable;

    /// <summary>A sequence is running or a manual command is: commands wait.</summary>
    [ObservableProperty]
    public partial bool IsWorking { get; private set; }

    /// <summary>The outcome of the last thing done that is not an error: preferences that could not be applied, for example.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string NoticeText { get; protected set; } = string.Empty;

    public bool HasNotice => NoticeText.Length > 0;

    public bool CanOperate => IsAvailable && !IsWorking && !Device.IsSessionBusy;

    protected abstract bool HasCapabilities { get; }

    /// <summary>What the device says about itself and its driver, line by line, for the Driver Info tab.</summary>
    public abstract IReadOnlyList<InfoLine> DriverInfo { get; }

    /// <summary>Reads the capabilities and the state from the device again and updates what the panel shows (UI thread).</summary>
    protected abstract void Rebuild();

    /// <summary>Applies the stored preferences after a connect; runs once per connection.</summary>
    protected abstract Task ApplyPreferencesAsync();

    /// <summary>Called by the derived class when the device raised a capability or state event (any thread).</summary>
    protected void DeviceChanged() => Device.PostToUiThread(Update);

    protected void Update()
    {
        var available = HasCapabilities;

        // What is offered is built first and only then announced as available, so that nobody sees a half built panel.
        Rebuild();
        IsAvailable = available;
        OnPropertyChanged(nameof(CanOperate));
        OnCommandsChanged();
        if (available && !_wasAvailable)
        {
            _wasAvailable = true;
            var generation = ++_generation;
            _ = ApplyAfterConnectAsync(generation);
        }
        else if (!available)
        {
            _wasAvailable = false;
            _generation++;
        }
    }

    private async Task ApplyAfterConnectAsync(int generation)
    {
        try
        {
            await ApplyPreferencesAsync();
        }
        catch (Exception ex) when (generation == _generation)
        {
            NoticeText = $"The saved preferences could not be applied: {UserFacingError.Describe(ex)}";
        }
        catch
        {
            // The connection ended meanwhile; there is nothing left to apply to.
        }
    }

    protected virtual void OnCommandsChanged()
    {
    }

    private void OnCommandsRefreshed(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanOperate));
        OnCommandsChanged();
    }

    /// <summary>Runs a command on the device: errors become a sentence, the state is read again afterwards.</summary>
    protected async Task OperateAsync(Func<Task> operation)
    {
        ClearError();
        NoticeText = string.Empty;
        IsWorking = true;
        OnPropertyChanged(nameof(CanOperate));
        OnCommandsChanged();
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsWorking = false;
            Update();
        }
    }

    /// <summary>Reads the state of the device once more, now.</summary>
    public async Task RefreshNowAsync()
    {
        try
        {
            await _observable.RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not connected any more, or the driver refused a read: the panel keeps showing what it had.
        }
    }

    private async Task PollAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (IsShown && IsAvailable && !IsWorking)
                {
                    await RefreshNowAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected void SavePreferences(Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>> change)
    {
        if (Preferences is null)
        {
            return;
        }

        var id = Device.DeviceIdText;
        if (Preferences.SavePreferences(id, change(Preferences.GetPreferences(id))) is { } problem)
        {
            NoticeText = problem;
        }
    }

    public virtual void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        Device.CommandsRefreshed -= OnCommandsRefreshed;
    }

    protected static bool TryNumber(string? text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    protected static bool TryWhole(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
        || int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    protected static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// An input for a whole-number setting that is either a range (a text box with the limits as hint) or a list of named
/// choices (a drop-down), exactly as the capabilities describe it. Without capabilities it is not offered.
/// </summary>
public sealed partial class IntegerControlInput(string label) : ObservableObject
{
    public string Label { get; } = label;

    public IntegerControl? Control { get; private set; }

    public bool IsAvailable => Control is not null;
    public bool IsRange => Control is { IsList: false };
    public bool IsList => Control is { IsList: true };
    public IReadOnlyList<string> Choices => Control?.Choices ?? [];

    public string Hint => Control is { IsList: false, Minimum: { } lo, Maximum: { } hi }
        ? string.Create(CultureInfo.InvariantCulture, $"{lo} to {hi}")
        : string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    public void Load(IntegerControl? control, int? current)
    {
        Control = control;
        OnPropertyChanged(nameof(Control));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsRange));
        OnPropertyChanged(nameof(IsList));
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Hint));
        Text = current?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SelectedIndex = control is { IsList: true } && current is { } value && control.Accepts(value) ? value : -1;
    }

    /// <summary>The value that is entered; <c>null</c> when nothing is entered. A text that is not a whole number is a problem.</summary>
    public int? Read()
    {
        if (Control is null)
        {
            return null;
        }

        if (Control.IsList)
        {
            return SelectedIndex >= 0 ? SelectedIndex : null;
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            return null;
        }

        return int.TryParse(Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            || int.TryParse(Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out v)
            ? v
            : throw new FormatException($"{Label} must be a whole number.");
    }
}
