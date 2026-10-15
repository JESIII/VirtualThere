using System.Net.Sockets;
using Vht.Core.Hooks;
using Vht.Core.Concurrency;
using Vht.Core.Logging;
using Vht.Core.Server;
using Vht.Server.Control;
using Vht.Server.Devices;
using Vht.Server.Hotplug;
using Vht.Server.Logging;
using Vht.Server.Net;

namespace Vht.Server.Hosting;

/// <summary>
/// Assembles and runs the whole server: enumeration, capture, exports, hotplug, both listeners,
/// the control plane and the service lifecycle.
/// </summary>
/// <remarks>
/// One object per process, and the only place the parts are wired together — the session layer
/// below it is transport-agnostic, and both the tests and the CLI drive the same pieces
/// directly.
/// </remarks>
public sealed class ServerRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private ServerRuntime(
        VhtServerState state,
        UsbDeviceEnumerator enumerator,
        WinUsbCaptureProvider capture,
        HotplugWatcher hotplug,
        UsbIpTcpServer tcp,
        ControlPlaneServer? control,
        RingBufferLogSink ring)
    {
        State = state;
        Enumerator = enumerator;
        Capture = capture;
        Hotplug = hotplug;
        Tcp = tcp;
        Control = control;
        Ring = ring;
    }

    public VhtServerState State { get; }
    public UsbDeviceEnumerator Enumerator { get; }
    public WinUsbCaptureProvider Capture { get; }
    public HotplugWatcher Hotplug { get; }
    public UsbIpTcpServer Tcp { get; }
    public ControlPlaneServer? Control { get; }
    public RingBufferLogSink Ring { get; }

    /// <summary>Number of devices exported at the last refresh.</summary>
    public int ExportedDevices { get; private set; }

    /// <summary>
    /// Builds every component and binds the listeners, without starting the accept loops.
    /// </summary>
    public static ServerRuntime Build(
        ServerOptions options,
        ILogger log,
        Func<NetworkStream, Core.Transport.TransportKind, Core.Transport.ITransport>? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        var ring = new RingBufferLogSink();
        if (log is StructuredLogger structured) structured.AddSink(ring);

        var enumerator = new UsbDeviceEnumerator(options, log);
        WinUsbRebinder? rebinder = options.RebindEnabled ? new WinUsbRebinder(string.Empty, log) : null;
        var capture = new WinUsbCaptureProvider(enumerator, options, log, rebinder);

        HookRunner? hooks = options.Hooks.Scripts.Count > 0
            ? new HookRunner(options.Hooks, new ShellProcessRunner(), log)
            : null;

        var state = new VhtServerState(options, capture, log, hooks);
        var hotplug = new HotplugWatcher(state);
        var tcp = new UsbIpTcpServer(state, transportFactory);

        ControlPlaneServer? control = null;
        ServerRuntime? runtime = null;
        control = options.ControlPlaneEnabled
            ? new ControlPlaneServer(state, ring, () =>
            {
                runtime?.RequestStop();
                return Task.CompletedTask;
            })
            : null;

        runtime = new ServerRuntime(state, enumerator, capture, hotplug, tcp, control, ring);
        return runtime;
    }

    /// <summary>Runs the server until <paramref name="ct"/> is cancelled. Returns a process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        IReadOnlyList<string> problems = State.Options.Validate();
        if (problems.Count > 0)
        {
            foreach (string problem in problems)
                State.Logger.Write(LogLevel.Error, LogEvent.ServerStarting, $"configuration error: {problem}");

            return 2;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);

        State.Logger.Write(LogLevel.Info, LogEvent.ServerStarting,
            $"starting {State.Banner}",
            new Dictionary<string, object?>
            {
                ["version"] = Core.VhtVersion.Version,
                ["capture_mode"] = State.Options.CaptureMode,
                ["rebind"] = State.Options.RebindEnabled,
                ["elevated"] = Interop.Services.WindowsServiceManager.IsElevated,
            });

        ExportedDevices = await State.RefreshExportsAsync(linked.Token).ConfigureAwait(false);

        // A run that was killed mid-rebind leaves its record behind, and the devices it moved are
        // still on the package it staged. Handing them back here - before the server starts
        // serving - is what makes the record worth writing at all. Nothing is touched when the
        // record is empty, which is the normal case.
        Capture.RestorePendingHandbacks();

        if (!Hotplug.Start(out int cr))
        {
            State.Logger.Write(LogLevel.Warn, LogEvent.HotplugArrival,
                $"device-interface notifications could not be registered (CR {cr}); " +
                "falling back to a periodic rescan",
                new Dictionary<string, object?> { ["cr"] = cr });

            _ = Task.Run(() => PollFallbackAsync(linked.Token), CancellationToken.None);
        }
        else if (Hotplug.UsingAllInterfaceClasses)
        {
            // The narrow filter was refused but the wide one works, so hotplug is event-driven
            // rather than poll-driven on this host; arrivals are filtered on the symbolic link.
            State.Logger.Write(LogLevel.Info, LogEvent.HotplugArrival,
                "device-interface notifications registered for all interface classes " +
                $"(the class-specific filter was refused with CR {Hotplug.ClassSpecificError})",
                new Dictionary<string, object?>
                {
                    ["cr"] = Hotplug.ClassSpecificError,
                    ["all_interface_classes"] = true,
                });
        }

        Tcp.Bind();

        State.Logger.Write(LogLevel.Info, LogEvent.ServerStarted,
            $"{State.Banner} is serving {ExportedDevices} device(s) with no device limit; " +
            $"listeners: {string.Join(", ", Tcp.Endpoints)}",
            new Dictionary<string, object?>
            {
                ["exported"] = ExportedDevices,
                ["listeners"] = Tcp.Endpoints.Count,
                ["device_limit"] = null,
            });

        Control?.Start();

        try
        {
            await Tcp.RunAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            State.Logger.Write(LogLevel.Info, LogEvent.ServerStopping, "stopping");

            if (Control is not null)
                await Control.DisposeAsync().ConfigureAwait(false);

            await Tcp.DisposeAsync().ConfigureAwait(false);
            await Hotplug.DisposeAsync().ConfigureAwait(false);

            // Connections are cancelled *before* the capture provider is disposed, so the sessions
            // holding a device hand it back on their own teardown first and the sweep in
            // Capture.DisposeAsync() has nothing left to do. Whatever a session was too late to
            // release is handed back by that sweep regardless: no device is left on winusb.sys
            // because the server stopped while a client had it.
            foreach (Connection connection in State.Connections.Snapshot())
            {
                try
                {
                    await connection.Cancellation.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            await Capture.DisposeAsync().ConfigureAwait(false);

            State.Logger.Write(LogLevel.Info, LogEvent.ServerStopped, "stopped");
        }

        return 0;
    }

    /// <summary>
    /// Convergence path when <c>CM_Register_Notification</c> is unavailable: re-enumerate on a
    /// short timer so an unplug is still un-exported within a few seconds.
    /// </summary>
    private async Task PollFallbackAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                int count = await State.RefreshExportsAsync(ct).ConfigureAwait(false);
                ExportedDevices = count;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                State.Logger.Write(LogLevel.Warn, LogEvent.HotplugRemoval,
                    $"periodic rescan failed: {ex.Message}");
            }
        }
    }

    /// <summary>Asks the run loop to stop (the <c>server.stop</c> verb, Ctrl+C, or the SCM).</summary>
    public void RequestStop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        _stop.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
