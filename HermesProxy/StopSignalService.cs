using System;
using System.Threading;
using System.Threading.Tasks;
using Framework.Logging;
using Microsoft.Extensions.Hosting;

namespace HermesProxy;

/// <summary>
/// Lets another process ask for a graceful shutdown on Windows. A launcher sets the named event
/// <c>HermesProxy-Stop-&lt;pid&gt;</c> and the host stops the way Ctrl+C would stop it: listeners
/// close, and the log drains on the way out.
/// </summary>
/// <remarks>
/// A proxy started in its own console window cannot be sent Ctrl+C from a script, which left
/// <c>Stop-Process -Force</c> as the only way to end it. That skips every shutdown path: queued log
/// lines are lost, and an attached <c>dotnet-trace</c> never gets the rundown that names its stack
/// frames. Linux and macOS need none of this (SIGTERM already stops the host) and do not support
/// named events, so the service does nothing there.
/// </remarks>
internal sealed class StopSignalService(IHostApplicationLifetime lifetime) : BackgroundService
{
    private static readonly Microsoft.Extensions.Logging.ILogger _melServer = Log.CreateMelLogger(Log.CategoryServer);
    private static readonly string _sourceFile = nameof(StopSignalService).PadRight(15);

    public static string EventName(int processId) => $"HermesProxy-Stop-{processId}";

    /// <summary>
    /// Set once a launcher has asked the proxy to stop. <c>Main</c> reads it to skip the "Press enter
    /// to close" prompt: nobody is at that console to press it, so the process would never exit.
    /// </summary>
    public static bool Requested { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
            return;

        string name = EventName(Environment.ProcessId);
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            stopEvent, (_, _) => signalled.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await signalled.Task.WaitAsync(stoppingToken);
            ServerLogMessages.StopRequested(_melServer, _sourceFile, "", name);
            Requested = true;
            lifetime.StopApplication();
        }
        catch (OperationCanceledException)
        {
            // The host is stopping for some other reason.
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
