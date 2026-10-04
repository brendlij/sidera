using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Diagnostics;

/// <summary>
/// How Sidera logs: how much, where, and for how long files are kept. Code defaults; there is no settings system yet.
/// Not part of a sequence document.
/// </summary>
/// <param name="MinimumLevel">Entries below this level are dropped. Debug adds the per-step, per-measurement detail.</param>
/// <param name="LogDirectory">The folder of the log files; see <see cref="LogLocation.DefaultDirectory"/>.</param>
/// <param name="RetentionDays">Log files not written for this many days are deleted when logging starts.</param>
/// <param name="MaxFileBytes">A session file that gets this big is continued in a new one.</param>
public sealed record LoggingOptions(
    LogLevel MinimumLevel,
    string LogDirectory,
    int RetentionDays = LoggingOptions.DefaultRetentionDays,
    long MaxFileBytes = LoggingOptions.DefaultMaxFileBytes
)
{
    public const int DefaultRetentionDays = 14;
    public const long DefaultMaxFileBytes = 20 * 1024 * 1024;

    /// <summary>
    /// The defaults of this build: Debug when it is a debug build, Information otherwise, in the user's log folder.
    /// The level is a parameter of the build, not of the runtime, so that it is explicit and testable.
    /// </summary>
    public static LoggingOptions ForBuild(bool debugBuild) =>
        new(debugBuild ? LogLevel.Debug : LogLevel.Information, LogLocation.DefaultDirectory());

    public LoggingOptions Validate()
    {
        if (MinimumLevel == LogLevel.None)
        {
            throw new ArgumentException("The minimum log level cannot be None.", nameof(MinimumLevel));
        }

        if (string.IsNullOrWhiteSpace(LogDirectory))
        {
            throw new ArgumentException("The log directory cannot be empty.", nameof(LogDirectory));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(RetentionDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFileBytes, 4096);
        return this;
    }
}

/// <summary>Where Sidera keeps its logs: the per-user application data folder of the platform, never the install or work folder.</summary>
public static class LogLocation
{
    /// <summary>
    /// Windows: <c>%LOCALAPPDATA%\Sidera\logs</c>. Linux: <c>~/.local/share/Sidera/logs</c> (or <c>$XDG_DATA_HOME</c>).
    /// macOS: <c>~/Library/Application Support/Sidera/logs</c>.
    /// </summary>
    public static string DefaultDirectory()
    {
        string root;
        if (OperatingSystem.IsMacOS())
        {
            // .NET maps the local application data folder to ~/.local/share there; macOS programs use Application Support.
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }
        else
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (string.IsNullOrEmpty(root))
        {
            // No profile (a service, a container): the temp folder is still per user and not the work folder.
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "Sidera", "logs");
    }
}
