using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.Notifications;

/// <summary>What a message is about: something that is, something that worked, something that needs a look, something that failed.</summary>
public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A button on a message: what it is called and what it does ("Open Equipment", "Open Diagnostics"). Pressing it also closes the message.</summary>
public sealed record NotificationAction(string Label, Action Invoke);

/// <summary>What was said, kept for the list of recent messages.</summary>
public sealed record NotificationRecord(DateTimeOffset Time, NotificationSeverity Severity, string Title, string? Detail);

/// <summary>Time for the messages: when it is, and a call at a later time. The application runs it on the interface thread; a test runs it by hand.</summary>
public interface INotificationScheduler
{
    DateTimeOffset Now { get; }

    /// <summary>Runs <paramref name="callback"/> once after <paramref name="delay"/>; disposing the result cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>The clock of the application: a timer whose calls are posted to the interface thread.</summary>
public sealed class ThreadingNotificationScheduler(Action<Action> postToUi) : INotificationScheduler
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var timer = new Timer(_ => postToUi(callback), null, delay, Timeout.InfiniteTimeSpan);
        return timer;
    }
}

/// <summary>
/// One message on the screen: a short title, an optional line under it, an optional button, a severity that says how to read it. The same message posted again while it is shown is not shown twice:
/// it counts up. It closes by itself after a while (never an error) and always has a close button; pointing at it holds the clock.
/// </summary>
public sealed partial class NotificationViewModel : ObservableObject
{
    private readonly NotificationService _owner;

    internal NotificationViewModel(NotificationService owner, NotificationSeverity severity, string title, string? detail, string key, NotificationAction? action)
    {
        _owner = owner;
        Severity = severity;
        Title = title;
        Detail = detail;
        Key = key;
        Action = action;
    }

    public NotificationSeverity Severity { get; }

    public string Title { get; }

    public string? Detail { get; internal set; }

    /// <summary>What makes two messages the same: while one is shown, the other only counts.</summary>
    public string Key { get; }

    public NotificationAction? Action { get; internal set; }

    /// <summary>How many times it was posted while it was shown: "×3" next to the title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount), nameof(CountText), nameof(AutomationText))]
    public partial int Count { get; internal set; } = 1;

    public bool HasCount => Count > 1;

    public string CountText => Count > 1 ? "×" + Count : string.Empty;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public bool HasAction => Action is not null;

    public string ActionLabel => Action?.Label ?? string.Empty;

    public bool IsInfo => Severity == NotificationSeverity.Info;

    public bool IsSuccess => Severity == NotificationSeverity.Success;

    public bool IsWarning => Severity == NotificationSeverity.Warning;

    public bool IsError => Severity == NotificationSeverity.Error;

    /// <summary>The word that says the severity ("Error"), so that it is never only a colour.</summary>
    public string SeverityText => Severity.ToString();

    /// <summary>The resource key of the icon: each severity has its own shape, so that Red Night (which has one hue) can still be read.</summary>
    public string IconKey => Severity switch
    {
        NotificationSeverity.Success => "IconSuccess",
        NotificationSeverity.Warning => "IconWarning",
        NotificationSeverity.Error => "IconError",
        _ => "IconInfo",
    };

    /// <summary>How a screen reader announces it: errors at once, the rest when it is the turn.</summary>
    public string LiveSetting => Severity == NotificationSeverity.Error ? "Assertive" : "Polite";

    /// <summary>"Error: Sequence failed. See the log."</summary>
    public string AutomationText => $"{SeverityText}: {Title}{(HasDetail ? ". " + Detail : string.Empty)}{(HasCount ? " " + CountText : string.Empty)}";

    [RelayCommand]
    private void Dismiss() => _owner.Dismiss(this);

    [RelayCommand]
    private void RunAction()
    {
        var action = Action;
        _owner.Dismiss(this);
        action?.Invoke();
    }

    /// <summary>The pointer is over the message (or left it): the clock that closes it is held meanwhile.</summary>
    public void SetHovered(bool hovered) => _owner.SetHovered(this, hovered);
}

/// <summary>What anything that has something to say talks to: it says it, and the shell shows it. Views and view models see this and not the screen.</summary>
public interface INotificationService
{
    /// <summary>The messages on the screen, the newest first.</summary>
    ReadOnlyObservableCollection<NotificationViewModel> Visible { get; }

    /// <summary>The last messages that were said, the newest first (at most <see cref="NotificationService.HistoryLimit"/>).</summary>
    IReadOnlyList<NotificationRecord> History { get; }

    /// <summary>Raised when something was added to <see cref="History"/>.</summary>
    event EventHandler? HistoryChanged;

    /// <summary>Says something. The same <paramref name="key"/> (by default the same words) while it is shown counts up instead of showing it twice.</summary>
    void Post(NotificationSeverity severity, string title, string? detail = null, string? key = null, NotificationAction? action = null);

    void Dismiss(NotificationViewModel notification);

    void DismissAll();
}

/// <summary>The four severities as short calls.</summary>
public static class NotificationExtensions
{
    public static void Info(this INotificationService service, string title, string? detail = null, string? key = null, NotificationAction? action = null) =>
        service.Post(NotificationSeverity.Info, title, detail, key, action);

    public static void Success(this INotificationService service, string title, string? detail = null, string? key = null, NotificationAction? action = null) =>
        service.Post(NotificationSeverity.Success, title, detail, key, action);

    public static void Warning(this INotificationService service, string title, string? detail = null, string? key = null, NotificationAction? action = null) =>
        service.Post(NotificationSeverity.Warning, title, detail, key, action);

    public static void Error(this INotificationService service, string title, string? detail = null, string? key = null, NotificationAction? action = null) =>
        service.Post(NotificationSeverity.Error, title, detail, key, action);
}

/// <summary>
/// The messages of the application, in one place. At most <see cref="MaxVisible"/> are shown, the newest on top; the others wait their turn and appear when there is room. Success and info close after
/// about four seconds, a warning after eight, an error stays until it is closed. Pointing at a message holds its clock. A message that is said again while it is shown does not stack: it counts up and
/// its clock starts over. Everything runs on the interface thread (<c>postToUi</c>), and all time comes from the <see cref="INotificationScheduler"/>, so a test needs no waiting.
/// </summary>
public sealed class NotificationService : INotificationService, IDisposable
{
    public const int MaxVisible = 3;
    public const int HistoryLimit = 50;

    public static TimeSpan InfoDuration { get; } = TimeSpan.FromSeconds(4);
    public static TimeSpan SuccessDuration { get; } = TimeSpan.FromSeconds(4);
    public static TimeSpan WarningDuration { get; } = TimeSpan.FromSeconds(8);

    private sealed class Clock
    {
        public IDisposable? Handle;
        public TimeSpan Remaining;
        public DateTimeOffset StartedAt;
    }

    private readonly Action<Action> _postToUi;
    private readonly INotificationScheduler _scheduler;
    private readonly ObservableCollection<NotificationViewModel> _visible = [];
    private readonly Queue<NotificationViewModel> _waiting = new();
    private readonly Dictionary<NotificationViewModel, Clock> _clocks = [];
    private readonly List<NotificationRecord> _history = [];
    private bool _disposed;

    public NotificationService(Action<Action> postToUi, INotificationScheduler? scheduler = null)
    {
        _postToUi = postToUi;
        _scheduler = scheduler ?? new ThreadingNotificationScheduler(postToUi);
        Visible = new ReadOnlyObservableCollection<NotificationViewModel>(_visible);
    }

    public ReadOnlyObservableCollection<NotificationViewModel> Visible { get; }

    public IReadOnlyList<NotificationRecord> History => _history;

    public event EventHandler? HistoryChanged;

    /// <summary>How long a message of a severity stays; <c>null</c> for an error: it stays until it is closed.</summary>
    public static TimeSpan? DurationOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => null,
        NotificationSeverity.Warning => WarningDuration,
        NotificationSeverity.Success => SuccessDuration,
        _ => InfoDuration,
    };

    public void Post(NotificationSeverity severity, string title, string? detail = null, string? key = null, NotificationAction? action = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _postToUi(() => PostCore(severity, title.Trim(), string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(), key, action));
    }

    private void PostCore(NotificationSeverity severity, string title, string? detail, string? key, NotificationAction? action)
    {
        if (_disposed)
        {
            return;
        }

        key ??= $"{severity}|{title}|{detail}";
        _history.Insert(0, new NotificationRecord(_scheduler.Now, severity, title, detail));
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);

        if (_visible.FirstOrDefault(n => n.Key == key) is { } shown)
        {
            shown.Count++;
            shown.Detail = detail;
            shown.Action = action ?? shown.Action;
            Restart(shown);
            return;
        }

        if (_waiting.FirstOrDefault(n => n.Key == key) is { } waiting)
        {
            waiting.Count++;
            return;
        }

        var notification = new NotificationViewModel(this, severity, title, detail, key, action);
        if (_visible.Count < MaxVisible)
        {
            Show(notification);
        }
        else
        {
            _waiting.Enqueue(notification);
        }
    }

    private void Show(NotificationViewModel notification)
    {
        _visible.Insert(0, notification);
        Restart(notification);
    }

    // The clock of a message starts over (it was shown, or it was said again).
    private void Restart(NotificationViewModel notification)
    {
        Cancel(notification);
        if (DurationOf(notification.Severity) is not { } duration)
        {
            return;
        }

        StartClock(notification, duration);
    }

    private void StartClock(NotificationViewModel notification, TimeSpan remaining)
    {
        var clock = new Clock { Remaining = remaining, StartedAt = _scheduler.Now };
        clock.Handle = _scheduler.Schedule(remaining, () => Dismiss(notification));
        _clocks[notification] = clock;
    }

    private void Cancel(NotificationViewModel notification)
    {
        if (_clocks.Remove(notification, out var clock))
        {
            clock.Handle?.Dispose();
        }
    }

    public void SetHovered(NotificationViewModel notification, bool hovered)
    {
        _postToUi(() =>
        {
            if (!_visible.Contains(notification) || DurationOf(notification.Severity) is null)
            {
                return;
            }

            if (hovered)
            {
                if (_clocks.Remove(notification, out var clock))
                {
                    clock.Handle?.Dispose();
                    var left = clock.Remaining - (_scheduler.Now - clock.StartedAt);
                    _held[notification] = left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1);
                }
            }
            else if (_held.Remove(notification, out var remaining))
            {
                // Read in peace, then a little time more: never less than two seconds after the pointer left.
                StartClock(notification, remaining > TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2));
            }
        });
    }

    private readonly Dictionary<NotificationViewModel, TimeSpan> _held = [];

    public void Dismiss(NotificationViewModel notification)
    {
        _postToUi(() =>
        {
            Cancel(notification);
            _held.Remove(notification);
            if (_visible.Remove(notification) && _waiting.Count > 0)
            {
                Show(_waiting.Dequeue());
            }
        });
    }

    public void DismissAll()
    {
        _postToUi(() =>
        {
            foreach (var notification in _visible.ToList())
            {
                Cancel(notification);
            }

            _visible.Clear();
            _held.Clear();
            _waiting.Clear();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var clock in _clocks.Values)
        {
            clock.Handle?.Dispose();
        }

        _clocks.Clear();
    }
}
