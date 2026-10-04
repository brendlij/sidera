using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Sidera.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sidera.Desktop.Diagnostics;

/// <summary>
/// The one place where logging is configured: the console and the rolling file of the session, the minimum level, and the
/// levels of noisy categories. Created by the application before the runtime host, so that everything that is created
/// afterwards can log. The runtime and the view models only receive loggers from the factory; they never configure it.
/// Disposing flushes the file and releases it, which the application does last.
/// </summary>
public sealed class SideraLogging : IDisposable
{
    private readonly RollingFileLoggerProvider _file;

    private SideraLogging(LoggingOptions options, ILoggerFactory factory, RollingFileLoggerProvider file, LogSummary summary)
    {
        Options = options;
        Factory = factory;
        _file = file;
        Summary = summary;
    }

    /// <summary>The warnings and errors of this session, for the diagnostics page.</summary>
    public LogSummary Summary { get; }

    public LoggingOptions Options { get; }

    public ILoggerFactory Factory { get; }

    /// <summary>The file this session writes to now.</summary>
    public string CurrentFilePath => _file.CurrentFilePath;

    /// <summary>What the diagnostics page shows.</summary>
    public LogInfo Info(string sessionId) => new(Options.LogDirectory, CurrentFilePath, Options.MinimumLevel, sessionId, Summary);

    /// <summary>
    /// Console and file, at the level of <paramref name="options"/>. Microsoft's own categories only report warnings and
    /// above, so that the level of Sidera's categories can be Debug without the framework drowning the log.
    /// </summary>
    public static SideraLogging Create(LoggingOptions options, bool console = true)
    {
        options.Validate();
        var file = new RollingFileLoggerProvider(options);
        var summary = new LogSummary();
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(options.MinimumLevel);
            builder.AddFilter("Microsoft", LogLevel.Warning);
            builder.AddFilter("System", LogLevel.Warning);

            if (console)
            {
                builder.AddSimpleConsole(format =>
                {
                    format.SingleLine = true;
                    format.IncludeScopes = true;
                    format.TimestampFormat = "HH:mm:ss.fff ";
                });
            }

            builder.AddProvider(file);
            builder.AddProvider(summary);
        });

        return new SideraLogging(options, factory, file, summary);
    }

    /// <summary>
    /// What a log of this session starts with: the version of Sidera, the platform, the runtime, the architecture, and where
    /// the log is. Nothing about the user or the machine.
    /// </summary>
    public static void LogStartup(ILogger logger, LogInfo info)
    {
        logger.LogInformation(
            "Sidera {Version} starting on {Platform}, {Runtime}, {Architecture} process",
            Version(), RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture);
        logger.LogInformation(
            "Logging to {LogFile} at level {MinimumLevel}; files older than {RetentionDays} days are removed",
            info.CurrentFile, info.MinimumLevel, LoggingOptions.DefaultRetentionDays);
    }

    /// <summary>The version for people: "1.0.0 (751a2d5)" instead of the whole commit hash.</summary>
    public static string DisplayVersion()
    {
        var version = Version();
        var plus = version.IndexOf('+');
        return plus > 0 && version.Length - plus - 1 >= 7 ? $"{version[..plus]} ({version.Substring(plus + 1, 7)})" : version;
    }

    public static string Version() =>
        typeof(SideraLogging).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SideraLogging).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public void Dispose()
    {
        // A provider that was handed over as an instance is not disposed by the factory: the file must be closed (and
        // flushed) here, after the last entry has been written.
        Factory.Dispose();
        _file.Dispose();
    }
}

/// <summary>Where the log of this session is and how much it says; shown by the diagnostics page.</summary>
public sealed record LogInfo(
    string Directory, string? CurrentFile, LogLevel MinimumLevel, string SessionId, LogSummary? Summary = null);
