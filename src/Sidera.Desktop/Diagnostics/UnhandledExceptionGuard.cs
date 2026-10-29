using System;
using Microsoft.Extensions.Logging;
using Sidera.Desktop.Notifications;

namespace Sidera.Desktop.Diagnostics;

/// <summary>
/// The last line of defence of the interface: an exception that nothing handled on the interface thread is written to the log with its stack trace and said as an error message, and the
/// application goes on, so that a failed callback does not end a night (and does not drop the connections of the devices). Only what cannot be survived is passed on to end the process.
/// </summary>
public sealed class UnhandledExceptionGuard(ILogger logger)
{
    private INotificationService? _notifications;

    /// <summary>Where the messages go; until it is set, the problem is only logged.</summary>
    public void Attach(INotificationService notifications) => _notifications = notifications;

    /// <summary>Logs and says the exception. True when the application can go on; false for the ones that must end it.</summary>
    public bool Handle(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OutOfMemoryException or StackOverflowException or AccessViolationException or InsufficientExecutionStackException)
        {
            logger.LogCritical(exception, "A fatal exception on the interface thread; the application is terminating");
            return false;
        }

        logger.LogError(exception, "An exception that nothing handled on the interface thread; the application goes on");
        try
        {
            _notifications?.Error("Something went wrong", $"{exception.GetType().Name}: {exception.Message} The details are in the log.", key: "unhandled-exception");
        }
        catch (Exception problem)
        {
            logger.LogError(problem, "The message about an unhandled exception could not be shown");
        }

        return true;
    }

    /// <summary>A task whose exception nobody observed: logged and said, the process is not affected.</summary>
    public void HandleUnobserved(Exception exception)
    {
        logger.LogError(exception, "A task failed and nobody observed its exception");
        try
        {
            _notifications?.Error("A background task failed", $"{exception.GetType().Name}: {exception.Message} The details are in the log.", key: "unobserved-task");
        }
        catch (Exception problem)
        {
            logger.LogError(problem, "The message about a failed task could not be shown");
        }
    }
}
