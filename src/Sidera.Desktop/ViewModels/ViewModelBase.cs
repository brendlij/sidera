using System;
using Sidera.Desktop.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>The last problem, in a sentence for the user; <c>null</c> when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>The exception behind <see cref="ErrorMessage"/>, kept for diagnostics.</summary>
    public Exception? LastException { get; private set; }

    protected void ReportError(Exception exception)
    {
        LastException = exception;
        ErrorMessage = UserFacingError.Describe(exception);
    }

    protected void ReportError(string message)
    {
        LastException = null;
        ErrorMessage = message;
    }

    /// <summary>A message for the user, with the exception behind it kept for diagnostics.</summary>
    protected void ReportError(string message, Exception exception)
    {
        LastException = exception;
        ErrorMessage = message;
    }

    /// <summary>Where this view model says what happened (a result, a failure): the messages at the top right of the window. <c>null</c> where nothing listens (a test of something else).</summary>
    public INotificationService? Notifications { get; set; }

    /// <summary>Says something to the person at the screen, as a message.</summary>
    protected void Announce(NotificationSeverity severity, string title, string? detail = null, string? key = null, NotificationAction? action = null) =>
        Notifications?.Post(severity, title, detail, key, action);

    /// <summary>
    /// A failure of something the user did (open, save, run, connect): it is the error of this view model (<see cref="ErrorMessage"/>, for whoever shows it next to the thing) and it is said as an error message
    /// that stays until it is closed. A value that was typed wrongly is not this: that is told next to the field.
    /// </summary>
    protected void ReportProblem(string title, string message, Exception? exception = null, NotificationAction? action = null)
    {
        if (exception is null)
        {
            ReportError(message);
        }
        else
        {
            ReportError(message, exception);
        }

        Notifications?.Post(NotificationSeverity.Error, title, message, action: action);
    }

    protected void ClearError()
    {
        LastException = null;
        ErrorMessage = null;
    }
}
