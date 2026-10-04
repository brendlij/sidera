using System.Runtime.InteropServices;
using Sidera.Ascom.Infrastructure;
using Sidera.Core.Devices;

namespace Sidera.Ascom.Tests;

/// <summary>The STA dispatcher: one apartment thread, one call at a time, deterministic disposal.</summary>
public class DispatcherTests
{
    [Fact]
    public async Task Work_RunsOnASingleThreadedApartment_ThatIsNotTheCallers()
    {
        using var dispatcher = new AscomDispatcher("test");

        var (apartment, thread) = await dispatcher.InvokeAsync(() => (Thread.CurrentThread.GetApartmentState(), Environment.CurrentManagedThreadId));

        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
        Assert.Equal(dispatcher.ThreadId, thread);
    }

    [Fact]
    public async Task EveryCall_RunsOnTheSameThread_InTheOrderItWasQueued()
    {
        using var dispatcher = new AscomDispatcher("test");
        var seen = new List<(int Index, int Thread)>();

        var tasks = Enumerable.Range(0, 50)
            .Select(i => dispatcher.InvokeAsync(() => seen.Add((i, Environment.CurrentManagedThreadId))))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(0, 50), seen.Select(s => s.Index));
        Assert.Single(seen.Select(s => s.Thread).Distinct());
    }

    [Fact]
    public async Task CallsFromManyThreads_NeverOverlap()
    {
        using var dispatcher = new AscomDispatcher("test");
        var inside = 0;
        var overlapped = false;

        await Task.WhenAll(
            Enumerable.Range(0, 40)
                .Select(_ => Task.Run(() => dispatcher.InvokeAsync(
                    () =>
                    {
                        if (Interlocked.Increment(ref inside) > 1)
                        {
                            overlapped = true;
                        }

                        Thread.Sleep(2);
                        Interlocked.Decrement(ref inside);
                    }))));

        Assert.False(overlapped);
    }

    [Fact]
    public async Task AnExceptionOfTheWork_ReachesTheCaller_AndTheDispatcherKeepsWorking()
    {
        using var dispatcher = new AscomDispatcher("test");

        var failure = await Assert.ThrowsAsync<COMException>(() => dispatcher.InvokeAsync<int>(() => throw new COMException("boom", unchecked((int)0x80004005))));

        Assert.Equal("boom", failure.Message);
        Assert.Equal(7, await dispatcher.InvokeAsync(() => 7));
    }

    [Fact]
    public async Task ACancelledCall_ThatWaitsInTheQueue_NeverRuns()
    {
        using var dispatcher = new AscomDispatcher("test");
        using var gate = new ManualResetEventSlim();
        var running = dispatcher.InvokeAsync(() => gate.Wait(TimeSpan.FromSeconds(10)));
        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = dispatcher.InvokeAsync(() => ran = true, cts.Token);

        cts.Cancel();
        gate.Set();
        await running;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(ran);
    }

    [Fact]
    public async Task ACallThatAlreadyRuns_IsNotInterruptedByItsToken()
    {
        using var dispatcher = new AscomDispatcher("test");
        using var started = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var completed = false;
        var call = dispatcher.InvokeAsync(
            () =>
            {
                started.Set();
                Thread.Sleep(100);
                completed = true;
            },
            cts.Token);
        started.Wait(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await call;

        Assert.True(completed);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_EndsTheThread_AndRefusesNewWork()
    {
        var dispatcher = new AscomDispatcher("test");
        await dispatcher.InvokeAsync(() => 1);

        dispatcher.Dispose();
        dispatcher.Dispose();

        Assert.True(dispatcher.HasExited);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => 1));
    }

    [Fact]
    public async Task Dispose_CompletesWhatHasNotStarted_WithoutRunningIt_AndWaitsForWhatRuns()
    {
        var dispatcher = new AscomDispatcher("test");
        using var started = new ManualResetEventSlim();
        var finished = false;
        var running = dispatcher.InvokeAsync(
            () =>
            {
                started.Set();
                Thread.Sleep(150);
                finished = true;
            });
        var ran = false;
        var queued = dispatcher.InvokeAsync(() => ran = true);
        started.Wait(TimeSpan.FromSeconds(5));

        dispatcher.Dispose();

        Assert.True(finished);
        await running;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        Assert.False(ran);
        Assert.True(dispatcher.HasExited);
    }

    [Fact]
    public void Dispose_GivesUpOnACallThatNeverReturns_AfterTheBoundedWait()
    {
        var dispatcher = new AscomDispatcher("test", TimeSpan.FromMilliseconds(100));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _ = dispatcher.InvokeAsync(
            () =>
            {
                started.Set();
                release.Wait();
            });
        started.Wait(TimeSpan.FromSeconds(5));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        dispatcher.Dispose();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.False(dispatcher.HasExited); // still inside the call: given up on, not killed

        release.Set();
        SpinWait.SpinUntil(() => dispatcher.HasExited, TimeSpan.FromSeconds(5));
        Assert.True(dispatcher.HasExited);
    }

    [Fact]
    public async Task Dispose_FromTheDispatcherThread_DoesNotDeadlock()
    {
        var dispatcher = new AscomDispatcher("test");

        await dispatcher.InvokeAsync(dispatcher.Dispose);

        SpinWait.SpinUntil(() => dispatcher.HasExited, TimeSpan.FromSeconds(5));
        Assert.True(dispatcher.HasExited);
    }
}

/// <summary>What drivers throw becomes a sentence for the user; cancellation and Sidera's own errors are left alone.</summary>
public class ErrorTranslationTests
{
    private static readonly DeviceId Id = new("focuser.main");

    private static Exception Translate(Exception exception) => AscomErrors.Translate(exception, Id, "Main Focuser", "ASCOM.Test.Focuser", "move");

    [Fact]
    public void ACOMException_IsDescribedWithItsErrorCode_AndKeptAsInner()
    {
        var com = new COMException("Server execution failed", unchecked((int)0x80080005));

        var translated = Assert.IsType<AscomDeviceException>(Translate(com));

        Assert.Equal("Could not move Main Focuser (ASCOM.Test.Focuser): COM error 0x80080005: Server execution failed", translated.Message);
        Assert.Same(com, translated.InnerException);
        Assert.Equal(Id, translated.DeviceId);
        Assert.Equal("ASCOM.Test.Focuser", translated.ProgId);
        Assert.Equal("move", translated.Operation);
    }

    [Fact]
    public void TheExceptionsOfTheAscomLibrary_AreExplainedByMeaning()
    {
        Assert.Contains("not connected", Translate(new ASCOM.NotConnectedException("x")).Message);
        Assert.Contains("does not support this", Translate(new ASCOM.MethodNotImplementedException("Halt")).Message);
        Assert.Contains("rejected a value", Translate(new ASCOM.InvalidValueException("Position", "-5", "0 to 100")).Message);
        Assert.Contains("parked", Translate(new ASCOM.ParkedException("parked")).Message);
        Assert.Contains("reported error", Translate(new ASCOM.DriverException("fault", 0x500)).Message);
    }

    [Fact]
    public void ATargetInvocationException_IsUnwrapped()
    {
        var wrapped = new System.Reflection.TargetInvocationException(new COMException("inner", 5));

        var translated = Assert.IsType<AscomDeviceException>(Translate(wrapped));

        Assert.IsType<COMException>(translated.InnerException);
        Assert.Contains("inner", translated.Message);
    }

    [Fact]
    public void CancellationAndSiderasOwnErrors_PassThroughUnchanged()
    {
        var cancelled = new OperationCanceledException();
        var disposed = new ObjectDisposedException("dispatcher");
        var argument = new ArgumentOutOfRangeException("target");
        var state = new InvalidOperationException("The focuser is already moving.");
        var own = new AscomTimeoutException(Id, "p", "move", "slow");

        Assert.Same(cancelled, Translate(cancelled));
        Assert.Same(disposed, Translate(disposed));
        Assert.Same(argument, Translate(argument));
        Assert.Same(state, Translate(state));
        Assert.Same(own, Translate(own));
    }

    [Fact]
    public void AnInvalidOperationOfTheDriver_IsTranslated_NotMistakenForSiderasOwn()
    {
        var translated = Assert.IsType<AscomDeviceException>(Translate(new ASCOM.InvalidOperationException("busy")));

        Assert.Contains("refused the operation", translated.Message);
    }
}
