using Sidera.Ascom.Discovery;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Ascom;

/// <param name="Completed">The driver's setup dialog was called and returned. It does not say that a window was shown.</param>
/// <param name="Problem">Why it was not: a sentence for the user.</param>
public sealed record AscomSetupResult(bool Completed, string? Problem);

/// <summary>Opens the setup dialog of an ASCOM driver, so that the driver can be configured before or between connections.</summary>
public interface IAscomSetupService
{
    Task<AscomSetupResult> ShowAsync(AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs <c>SetupDialog</c> of a driver. The driver is created for this one call, not connected, on a dispatcher
/// thread of its own (a single-threaded apartment, which is what a dialog of a COM driver needs), and released
/// afterwards. A driver without a dialog, or a dialog that fails, ends in a result with a problem: it never throws and
/// never takes Sidera down. The call is only waited for; whether the dialog really opened depends on the driver, and has
/// to be checked by hand with real drivers.
/// </summary>
public sealed class AscomSetupService(IAscomDriverFactory drivers, ILogger? logger = null) : IAscomSetupService
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<AscomSetupResult> ShowAsync(AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(progId))
        {
            return new AscomSetupResult(false, "Choose a driver first.");
        }

        var dispatcher = new AscomDispatcher($"setup {progId}");
        try
        {
            _logger.LogInformation("Opening the setup dialog of ASCOM driver {ProgId}", progId);
            await dispatcher
                .InvokeAsync(
                    () =>
                    {
                        using var driver = kind switch
                        {
                            AscomDeviceKind.Camera => (IAscomDriver)drivers.CreateCamera(progId),
                            AscomDeviceKind.Mount => drivers.CreateMount(progId),
                            _ => drivers.CreateFocuser(progId),
                        };
                        driver.SetupDialog();
                    })
                .WaitAsync(cancellationToken);
            return new AscomSetupResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return new AscomSetupResult(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The setup dialog of ASCOM driver {ProgId} failed", progId);
            return new AscomSetupResult(false, $"The setup dialog of {progId} could not be opened: {AscomErrors.Describe(ex)}");
        }
        finally
        {
            // Not disposed while the dialog is open: the dispatcher is only disposed once the call returned or was abandoned.
            _ = Task.Run(dispatcher.Dispose);
        }
    }
}
