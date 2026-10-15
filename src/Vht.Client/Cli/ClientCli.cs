// SPDX-License-Identifier: Apache-2.0
//
// vht-client — the product entry point.
//
// ARCHITECTURE.md §1: `vht-client` is a Windows service *or* a user process. Both modes are here:
//
//   vht-client serve                     run the client (bridge + attach loop + control pipe)
//   vht-client list [--peer <name>]       what the peers export (their own OP_REQ_DEVLIST path)
//   vht-client attach <busid>            attach one device now (manual, ignores auto-attach grants
//                                        but still honours the ignore list)
//   vht-client detach <busid> | --all    detach
//   vht-client attached                  what is attached / pending
//   vht-client peers add <spec>          add a peer: host, host:port or name@host:port
//   vht-client peers list | remove <n>
//   vht-client ignore <pattern...>       add ignore patterns (also: allow)
//   vht-client capacity                  measure the driver's port ceiling
//   vht-client status                    one-line health
//
// The service form is a thin wrapper: same code path, `serve` mode, no console detach. The tray
// (card ui-workstream) drives the same functions through the control pipe rather than a second
// implementation.

using Vht.Client.Attach;
using Vht.Client.Bridge;
using Vht.Client.Config;
using Vht.Client.Control;
using Vht.Client.Credentials;
using Vht.Client.Diagnostics;
using Vht.Client.Peers;
using Vht.Client.Policy;
using Vht.Client.Transport;
using Vht.Ui.Hosting;

namespace Vht.Client.Cli;

/// <summary>Parses arguments and runs the client. Returns a process exit code.</summary>
internal static class ClientCli
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitFailure = 1;

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr)
    {
        ParsedArguments parsed = ParsedArguments.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintUsage(stdout);
            return ExitOk;
        }

        ClientPaths.ApplyConfigOverride(parsed.ConfigPath);

        var log = new ClientLog(parsed.Quiet ? null : stdout, parsed.LogToFile ? ClientPaths.LogFile : null)
        {
            MinimumLevel = parsed.Verbose ? ClientLogLevel.Debug : ClientLogLevel.Info,
        };

        try
        {
            return parsed.Verb switch
            {
                "serve" => await ServeAsync(parsed, log, stderr).ConfigureAwait(false),
                "list" => await ListAsync(parsed, log, stdout, stderr).ConfigureAwait(false),
                "attach" => await AttachAsync(parsed, log, stdout).ConfigureAwait(false),
                "detach" => await DetachAsync(parsed, log, stdout).ConfigureAwait(false),
                "attached" => await AttachedAsync(parsed, log, stdout).ConfigureAwait(false),
                "peers" => await PeersAsync(parsed, log, stdout).ConfigureAwait(false),
                "ignore" => Ignore(parsed, log, stdout),
                "allow" => Allow(parsed, log, stdout),
                "capacity" => Capacity(parsed, log, stdout),
                "status" => Status(parsed, log, stdout),
                _ => Unknown(parsed.Verb, stderr),
            };
        }
        catch (ClientException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitFailure;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"error: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return ExitFailure;
        }
    }

    /// <summary>Builds the whole client graph. Shared by `serve` and by the one-shot verbs.</summary>
    private static ClientRuntime BuildRuntime(ParsedArguments parsed, ClientLog log)
    {
        var router = new BusidRouter();
        var peers = new PeerStore(parsed.PeersFile ?? ClientPaths.PeersFile, log);
        var policies = new AutoAttachPolicy(ClientPaths.AutoAttachGrantsFile, log);
        var credentials = new CredentialCache(ClientPaths.CredentialMemoryFile, log);
        var transports = PlainTcpTransportFactory.Instance;
        var catalog = new RemoteDeviceCatalog(peers, transports, router, log);

        var bridgeOptions = new BridgeOptions { Port = parsed.BridgePort ?? 3240 };
        var bridge = new LoopbackBridge(bridgeOptions, router, transports, catalog, log)
        {
            Handshake = new CredentialHandshake(credentials, log),
        };

        var controller = new UdeDeviceController(log);
        var attachments = new AttachManager(
            bridge,
            catalog,
            controller,
            policies,
            log,
            new AttachManagerOptions { AutoAttachEnabled = true });

        return new ClientRuntime(bridge, catalog, peers, policies, credentials, attachments, log);
    }

    /// <summary>
    /// Runs the client runtime in the foreground: a console invocation, or the privileged worker a
    /// window started (docs/UI.md section 1). Nothing here follows the window: the runtime stops
    /// when Ctrl+C is pressed or when the control pipe asks it to, and it keeps serving (and keeps
    /// devices attached) if the window that started it goes away.
    /// </summary>
    private static async Task<int> ServeAsync(
        ParsedArguments parsed,
        ClientLog log,
        TextWriter stderr)
    {
        ClientRuntime runtime = BuildRuntime(parsed, log);
        await using ClientControlServer control = new(
            runtime.Peers, runtime.Catalog, runtime.Attachments, runtime.Policy, runtime.Credentials, log,
            parsed.PipeName);

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            runtime.Shutdown();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            runtime.Bridge.Start();
            control.Start();

            if (parsed.MeasureCapacity)
            {
                runtime.Attachments.MeasurePortCapacity();
            }

            runtime.Attachments.Start();
            log.Info("client",
                $"vht-client running; {runtime.Peers.Peers.Count} peer(s) configured. Ctrl+C to stop.");

            await runtime.WaitForShutdownAsync().ConfigureAwait(false);
            log.Info("client", "stopping");
            return ExitOk;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> ListAsync(ParsedArguments parsed, ClientLog log, TextWriter stdout, TextWriter stderr)
    {
        await using ClientRuntime runtime = BuildRuntime(parsed, log);
        IReadOnlyList<InventoryEntry> entries = await runtime.Catalog.RefreshAsync(CancellationToken.None)
            .ConfigureAwait(false);

        if (entries.Count == 0)
        {
            await stdout.WriteLineAsync("no devices exported by any configured peer").ConfigureAwait(false);
        }
        else
        {
            foreach (InventoryEntry entry in entries)
            {
                await stdout.WriteLineAsync(
                    $"{entry.RoutingKey,-20} {entry.Peer.Name,-16} {entry.Device.VendorProduct} " +
                    $"{entry.Device.Speed,-8} {entry.Device.ClassName}")
                    .ConfigureAwait(false);
            }
        }

        foreach (PeerUnavailable failure in runtime.Catalog.UnavailablePeers)
        {
            await stderr.WriteLineAsync($"peer '{failure.Peer.Name}' ({failure.Peer.Endpoint}) unavailable: {failure.Reason}")
                .ConfigureAwait(false);
        }

        return ExitOk;
    }

    private static async Task<int> AttachAsync(ParsedArguments parsed, ClientLog log, TextWriter stdout)
    {
        if (parsed.Positional.Count == 0)
        {
            await stdout.WriteLineAsync("usage: vht-client attach <busid>").ConfigureAwait(false);
            return ExitUsage;
        }

        string busid = parsed.Positional[0];
        await using ClientRuntime runtime = BuildRuntime(parsed, log);

        // The manual path resolves through the live inventory: attaching a busid we cannot see would
        // otherwise silently create a route the bridge will refuse.
        IReadOnlyList<InventoryEntry> entries = await runtime.Catalog.RefreshAsync(CancellationToken.None)
            .ConfigureAwait(false);
        InventoryEntry? entry = entries.FirstOrDefault(e => string.Equals(e.RoutingKey, busid, StringComparison.Ordinal));
        if (entry is null)
        {
            await stdout.WriteLineAsync($"no device with busid '{busid}' in the current inventory").ConfigureAwait(false);
            return ExitFailure;
        }

        runtime.Bridge.Start();
        AttachedDevice device = await runtime.Attachments.AttachAsync(entry, AttachReason.Manual, CancellationToken.None)
            .ConfigureAwait(false);

        await stdout.WriteLineAsync($"attached {device.RoutingKey} on virtual port {device.Port}").ConfigureAwait(false);
        await stdout.WriteLineAsync("the device is now enumerated by Windows; the client must keep running to hold it.")
            .ConfigureAwait(false);

        // Hold the device until the user stops us: a one-shot process would detach on exit.
        await stdout.WriteLineAsync("Ctrl+C to detach and exit.").ConfigureAwait(false);
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            runtime.Shutdown();
        };
        Console.CancelKeyPress += handler;
        try
        {
            await runtime.WaitForShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
        return ExitOk;
    }

    private static async Task<int> DetachAsync(ParsedArguments parsed, ClientLog log, TextWriter stdout)
    {
        await using ClientRuntime runtime = BuildRuntime(parsed, log);
        runtime.Bridge.Start();

        if (parsed.All)
        {
            int count = runtime.Attachments.DetachAll();
            await stdout.WriteLineAsync($"detached {count} device(s)").ConfigureAwait(false);
            return ExitOk;
        }

        if (parsed.Positional.Count == 0)
        {
            await stdout.WriteLineAsync("usage: vht-client detach <busid> | detach --all").ConfigureAwait(false);
            return ExitUsage;
        }

        IReadOnlyList<InventoryEntry> entries = await runtime.Catalog.RefreshAsync(CancellationToken.None)
            .ConfigureAwait(false);
        InventoryEntry? entry = entries.FirstOrDefault(e =>
            string.Equals(e.RoutingKey, parsed.Positional[0], StringComparison.Ordinal));

        string? key = entry?.DeviceKey
            ?? runtime.Attachments.AttachedDevices.Values
                .FirstOrDefault(d => string.Equals(d.RoutingKey, parsed.Positional[0], StringComparison.Ordinal))?.DeviceKey;

        if (key is null)
        {
            await stdout.WriteLineAsync($"'{parsed.Positional[0]}' is not attached").ConfigureAwait(false);
            return ExitFailure;
        }

        bool detached = runtime.Attachments.Detach(key);
        await stdout.WriteLineAsync(detached ? $"detached {parsed.Positional[0]}" : $"could not detach {parsed.Positional[0]}")
            .ConfigureAwait(false);
        return detached ? ExitOk : ExitFailure;
    }

    private static async Task<int> AttachedAsync(ParsedArguments parsed, ClientLog log, TextWriter stdout)
    {
        await using ClientRuntime runtime = BuildRuntime(parsed, log);
        runtime.Bridge.Start();
        await runtime.Catalog.RefreshAsync(CancellationToken.None).ConfigureAwait(false);

        IReadOnlyList<ImportedDeviceSnapshot> imported = runtime.Attachments.ControllerSnapshot();

        if (imported.Count == 0)
        {
            await stdout.WriteLineAsync("no devices attached").ConfigureAwait(false);
        }
        else
        {
            await stdout.WriteLineAsync($"controller reports {imported.Count} imported device(s):").ConfigureAwait(false);
            foreach (ImportedDeviceSnapshot device in imported)
            {
                await stdout.WriteLineAsync($"  port {device.Port,-3} {device.BusId} via {device.Host}:{device.Service}")
                    .ConfigureAwait(false);
            }
        }

        foreach (PendingAttach pending in runtime.Attachments.PendingAttaches)
        {
            await stdout.WriteLineAsync($"  pending {pending.RoutingKey}: {pending.Reason}; not before {pending.NotBeforeUtc:u}")
                .ConfigureAwait(false);
        }

        return ExitOk;
    }

    private static async Task<int> PeersAsync(ParsedArguments parsed, ClientLog log, TextWriter stdout)
    {
        await using ClientRuntime runtime = BuildRuntime(parsed, log);

        if (parsed.Positional.Count == 0 || parsed.Positional[0] == "list")
        {
            foreach (Peer peer in runtime.Peers.Peers)
            {
                await stdout.WriteLineAsync($"  {peer.Name,-16} {peer.Endpoint,-24} tls={peer.UseTls} pinned={peer.SpkiPin is not null}")
                    .ConfigureAwait(false);
            }
            if (runtime.Peers.Peers.Count == 0)
            {
                await stdout.WriteLineAsync("no peers configured; add one with: vht-client peers add 192.168.1.10:3240")
                    .ConfigureAwait(false);
            }
            return ExitOk;
        }

        if (parsed.Positional[0] == "add" && parsed.Positional.Count > 1)
        {
            Peer peer = PeerStore.Parse(parsed.Positional[1], useTls: parsed.UseTls);
            runtime.Peers.Add(peer);
            await stdout.WriteLineAsync($"added peer {peer.Name} at {peer.Endpoint}").ConfigureAwait(false);
            return ExitOk;
        }

        if (parsed.Positional[0] == "remove" && parsed.Positional.Count > 1)
        {
            bool removed = runtime.Peers.Remove(parsed.Positional[1]);
            await stdout.WriteLineAsync(removed ? $"removed peer {parsed.Positional[1]}" : "no such peer")
                .ConfigureAwait(false);
            return removed ? ExitOk : ExitFailure;
        }

        await stdout.WriteLineAsync("usage: vht-client peers [list | add <host[:port]> | remove <name>]")
            .ConfigureAwait(false);
        return ExitUsage;
    }

    private static int Ignore(ParsedArguments parsed, ClientLog log, TextWriter stdout)
    {
        using ClientRuntime runtime = BuildRuntime(parsed, log);
        foreach (string pattern in parsed.Positional)
        {
            runtime.Policy.AddIgnore(pattern);
        }
        stdout.WriteLine($"ignore list: {string.Join(", ", runtime.Policy.IgnoreList)}");
        return ExitOk;
    }

    private static int Allow(ParsedArguments parsed, ClientLog log, TextWriter stdout)
    {
        using ClientRuntime runtime = BuildRuntime(parsed, log);
        runtime.Policy.SetAllow(parsed.Positional);
        stdout.WriteLine(runtime.Policy.AllowList.Count == 0
            ? "allow list cleared (every device is attachable)"
            : $"allow list: {string.Join(", ", runtime.Policy.AllowList)}");
        return ExitOk;
    }

    private static int Capacity(ParsedArguments parsed, ClientLog log, TextWriter stdout)
    {
        using ClientRuntime runtime = BuildRuntime(parsed, log);
        PortCapacityReport report = runtime.Attachments.MeasurePortCapacity();
        stdout.WriteLine(report.ToString());
        return ExitOk;
    }

    private static int Status(ParsedArguments parsed, ClientLog log, TextWriter stdout)
    {
        using ClientRuntime runtime = BuildRuntime(parsed, log);
        stdout.WriteLine($"peers: {runtime.Peers.Peers.Count}");
        stdout.WriteLine($"controller: {runtime.Attachments.ControllerDescription}");
        stdout.WriteLine($"bridge: {runtime.Attachments.BridgeEndpoint ?? "not running"}");
        stdout.WriteLine($"auto-attach grants: {runtime.Policy.GrantedKeys.Count}");
        stdout.WriteLine($"ignore list: [{string.Join(", ", runtime.Policy.IgnoreList)}]");
        stdout.WriteLine($"transport: {PlainTcpTransportFactory.Instance.Description}");
        return ExitOk;
    }

    private static int Unknown(string verb, TextWriter stderr)
    {
        stderr.WriteLine($"unknown command '{verb}'");
        PrintUsage(stderr);
        return ExitUsage;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("""
            vht-client — attach remote USB devices as local ones, without a device limit.

            usage: vht-client [command] [options]

            (no command)                open the VirtualThere Client window

            commands:
              serve                     run the client: bridge, attach loop, control pipe
              list                      list the devices your peers export
              attach <busid>            attach one device (Ctrl+C detaches)
              detach <busid> | --all    detach one device, or all of them
              attached                  show what the controller has imported
              peers list                configured peers
              peers add <host[:port]>   add a peer (name@host:port to name it)
              peers remove <name>       remove a peer
              ignore <pattern...>       never attach these devices
              allow <pattern...>        restrict attaching to these devices (empty = no restriction)
              capacity                  measure the driver's per-controller port ceiling
              status                    one-line health

            options:
              --config <path>           config directory override (also $VHT_CONFIG)
              --peers <path>            peer file override
              --port <n>                loopback bridge port (default 3240)
              --pipe <name>             control pipe to serve (default vht-client)
              --tls                     mark added peers as TLS (plain TCP is what v0.1.0 ships; see docs/TRANSPORT.md)
              --measure-capacity        run the port-capacity probe at startup (serve mode)
              -v, --verbose             debug logging
              -q, --quiet               no console logging
              --log                     also write a log file
              -h, --help                this text
            """);
    }
}

/// <summary>Everything `serve` wires together, so the one-shot verbs share one constructor path.</summary>
internal sealed class ClientRuntime : IAsyncDisposable, IDisposable
{
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ClientRuntime(
        LoopbackBridge bridge,
        RemoteDeviceCatalog catalog,
        PeerStore peers,
        AutoAttachPolicy policy,
        CredentialCache credentials,
        AttachManager attachments,
        ClientLog log)
    {
        Bridge = bridge;
        Catalog = catalog;
        Peers = peers;
        Policy = policy;
        Credentials = credentials;
        Attachments = attachments;
        Log = log;
    }

    internal LoopbackBridge Bridge { get; }

    internal RemoteDeviceCatalog Catalog { get; }

    internal PeerStore Peers { get; }

    internal AutoAttachPolicy Policy { get; }

    internal CredentialCache Credentials { get; }

    internal AttachManager Attachments { get; }

    internal ClientLog Log { get; }

    internal void Shutdown() => _shutdown.TrySetResult();

    internal Task WaitForShutdownAsync() => _shutdown.Task;

    public async ValueTask DisposeAsync()
    {
        await Attachments.DisposeAsync().ConfigureAwait(false);
        await Bridge.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous dispose for the one-shot verbs. The async path is the real one; this exists so the
    /// short-lived CLI verbs can use `using` without becoming async all the way out for no reason.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
