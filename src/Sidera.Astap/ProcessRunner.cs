using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Sidera.Astap;

/// <summary>A program to run: its arguments one by one (never one string to be split), what it may take and how much of its output is kept.</summary>
public sealed record ProcessRunRequest(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>The most characters kept of each of standard output and standard error; the rest is read and thrown away so that the program never blocks on a full pipe.</summary>
    public int MaxOutputCharacters { get; init; } = 32 * 1024;
}

public sealed record ProcessRunResult(int ExitCode, bool TimedOut, string StandardOutput, string StandardError, bool OutputTruncated, TimeSpan Duration);

/// <summary>Runs a program and waits for it. The seam where a test pretends to be the program.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the program. When it takes longer than the timeout it is ended (with everything it started) and the result says so; when the token is
    /// cancelled it is ended the same way and <see cref="OperationCanceledException"/> is thrown. Never leaves the program running.
    /// </summary>
    /// <exception cref="FileNotFoundException">The program does not exist or cannot be started.</exception>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs a real process: no window, output read asynchronously and bounded, the whole process tree ended on a timeout or a cancel.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                throw new FileNotFoundException($"{request.FileName} could not be started.", request.FileName);
            }
        }
        catch (Win32Exception ex)
        {
            throw new FileNotFoundException($"{request.FileName} could not be started: {ex.Message}", request.FileName, ex);
        }

        // Nothing is typed to the program: closing its input keeps it from waiting for any.
        process.StandardInput.Close();
        var output = new BoundedText(request.MaxOutputCharacters);
        var error = new BoundedText(request.MaxOutputCharacters);
        var readOut = Drain(process.StandardOutput, output);
        var readError = Drain(process.StandardError, error);

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            Kill(process);
            if (!timedOut)
            {
                await Settle(process, readOut, readError).ConfigureAwait(false);
                throw;
            }
        }

        await Settle(process, readOut, readError).ConfigureAwait(false);
        clock.Stop();
        return new ProcessRunResult(
            timedOut ? -1 : process.ExitCode, timedOut, output.ToString(), error.ToString(), output.Truncated || error.Truncated, clock.Elapsed);
    }

    // The process is ended with all that it started, and what it left is read: no solver stays behind.
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // It ended by itself in the meantime.
        }
    }

    private static async Task Settle(Process process, Task readOut, Task readError)
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            await Task.WhenAll(readOut, readError).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // A program that cannot be ended in five seconds is left to the system; the pipes are closed with the process object.
        }
    }

    private static async Task Drain(StreamReader reader, BoundedText sink)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                sink.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed with the process.
        }
    }

    private sealed class BoundedText(int limit)
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();

        public bool Truncated { get; private set; }

        public void Append(ReadOnlySpan<char> chars)
        {
            lock (_gate)
            {
                var room = limit - _text.Length;
                if (room > 0)
                {
                    _text.Append(chars[..Math.Min(room, chars.Length)]);
                }

                if (chars.Length > room)
                {
                    Truncated = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }
}
