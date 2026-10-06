using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rotators;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop.ViewModels;

/// <summary>What moves when an operation runs, for the question that precedes it.</summary>
public enum MovingEquipment
{
    Mount,
    Rotator,
}

/// <summary>One piece of real equipment that an operation moves.</summary>
public sealed record MovementNotice(MovingEquipment Kind, DeviceId Device, string DeviceName)
{
    public string Key => $"{Kind}:{Device.Value}";

    public string Text => Kind == MovingEquipment.Mount ? $"the mount ({DeviceName})" : $"the rotator ({DeviceName})";
}

/// <summary>
/// What stands between a button and real equipment that moves: a question the user answers, once for each mount and rotator for as long as Sidera runs. It replaces the environment variables that
/// the hardware tests use as gates; those stay in the tests, and a person at the telescope answers here instead. Cancel means nothing moves; Continue lets the operation go on and counts as the
/// answer for this equipment until Sidera is closed (or the answers are forgotten on the Advanced tab of the settings): nothing is trusted for good, and nothing is written to a file.
/// <para>
/// Simulated equipment never asks. The operation itself is not changed by this: it asks first, and does nothing when the answer is no.
/// </para>
/// </summary>
public sealed partial class HardwareSafetyViewModel : ObservableObject
{
    private readonly HashSet<string> _answered = [];
    private TaskCompletionSource<bool>? _pending;
    private IReadOnlyList<MovementNotice> _asking = [];

    /// <summary>A question is open: the shell shows it over the page, and nothing else can be done until it is answered.</summary>
    [ObservableProperty]
    public partial bool IsPending { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    /// <summary>"This operation will move the mount (EQ6 Mount)."</summary>
    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    public string SafetyText => "Ensure the equipment can move safely.";

    public string RememberText => "You are not asked again for this equipment until Sidera is closed.";

    /// <summary>How many pieces of equipment have been answered for in this run of Sidera.</summary>
    public int AnsweredCount => _answered.Count;

    /// <summary>A notice for a device that really moves: <c>null</c> for a simulator, a device that is not there, or one of the wrong kind.</summary>
    public static MovementNotice? NoticeFor(DeviceRegistry registry, MovingEquipment kind, DeviceId id)
    {
        if (!registry.TryGet(id, out var device) || device is null)
        {
            return null;
        }

        var simulated = device is SimulatedMount or SimulatedRotator;
        var right = kind == MovingEquipment.Mount ? device is IMount : device is IRotator;
        return simulated || !right ? null : new MovementNotice(kind, id, device.Name);
    }

    /// <summary>
    /// Asks whether <paramref name="what"/> may go on, if it moves equipment that was not answered for yet. Returns at once with <c>true</c> when nothing is to be asked. <c>false</c> means the
    /// user cancelled: the caller does nothing.
    /// </summary>
    public Task<bool> ConfirmAsync(string what, IEnumerable<MovementNotice?> equipment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        var needed = equipment.OfType<MovementNotice>().DistinctBy(n => n.Key).Where(n => !_answered.Contains(n.Key)).ToList();
        if (needed.Count == 0)
        {
            return Task.FromResult(true);
        }

        // A second question while one is open: the first one is answered "no", so that nothing waits forever.
        _pending?.TrySetResult(false);
        _asking = needed;
        Title = what;
        Message = $"This operation will move {string.Join(" and ", needed.Select(n => n.Text))}.";
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsPending = true;
        return _pending.Task;
    }

    /// <summary>Is this equipment answered for already?</summary>
    public bool IsAnswered(MovementNotice notice) => _answered.Contains(notice.Key);

    [RelayCommand]
    private void Continue()
    {
        foreach (var notice in _asking)
        {
            _answered.Add(notice.Key);
        }

        OnPropertyChanged(nameof(AnsweredCount));
        Close(true);
    }

    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool answer)
    {
        var pending = _pending;
        _pending = null;
        _asking = [];
        IsPending = false;
        pending?.TrySetResult(answer);
    }

    /// <summary>Forgets every answer: the next operation that moves real equipment asks again.</summary>
    public void Forget()
    {
        _answered.Clear();
        OnPropertyChanged(nameof(AnsweredCount));
    }
}
