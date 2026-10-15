using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vht.Core.Concurrency;
using Vht.Core.Hooks;
using Vht.Core.Protocol;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Server;
using Vht.Interop.Control;
using Vht.Server.Logging;

namespace Vht.Server.Control;

/// <summary>
/// The named-pipe control plane: JSON lines in, JSON lines out, one request/response pair per
/// line (ARCHITECTURE.md Appendix C).
/// </summary>
/// <remarks>
/// <para>
/// Verbs served here: <c>server.status</c>, <c>server.stop</c>, <c>devices.list</c>,
/// <c>devices.kick</c>, <c>devices.ignore</c>, <c>devices.unignore</c>,
/// <c>devices.nickname</c>, <c>devices.password</c> and <c>log.tail</c>. The
/// <c>clients.*</c> and <c>peers.*</c> verbs belong to <c>vht-client</c>'s own pipe and are
/// rejected here by name.
/// </para>
/// <para>
/// An unknown verb is an error object, never a silent success. Every mutating verb is
/// unconditional: <c>devices.kick</c> and <c>devices.ignore</c> are the two operations the
/// reference product gates behind a licence (RECON_AND_SPEC.md §1.3) and there is no licence
/// state anywhere in this process to consult.
/// </para>
/// </remarks>
public sealed class ControlPlaneServer : IAsyncDisposable
{
    private readonly VhtServerState _state;
    private readonly ILogger _log;
    private readonly RingBufferLogSink? _ring;
    private readonly Func<Task>? _stopServer;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public ControlPlaneServer(
        VhtServerState state,
        RingBufferLogSink? ring = null,
        Func<Task>? stopServer = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _log = state.Logger;
        _ring = ring;
        _stopServer = stopServer;
    }

    /// <summary>The pipe name, without the <c>\\.\pipe\</c> prefix.</summary>
    public string PipeName => _state.Options.ControlPlanePipeName;

    /// <summary>The full pipe path clients connect to.</summary>
    public string PipePath => $@"\\.\pipe\{PipeName}";

    /// <summary>Requests served since start.</summary>
    public long RequestsServed { get; private set; }

    /// <summary>
    /// What the kernel reports about the live pipe's security descriptor and mandatory integrity
    /// label, or null before the first pipe has been created. Read back from the object itself, so
    /// it can be asserted rather than assumed - a label that is not Medium is the state in which an
    /// unelevated window may be unable to write to its own worker.
    /// </summary>
    public PipeSecurityReport? PipeSecurity { get; private set; }

    /// <summary>Starts the accept loop.</summary>
    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_stop.Token));

        _log.Write(LogLevel.Info, LogEvent.ControlPlaneStarted,
            $"control plane listening on {PipePath}",
            new Dictionary<string, object?> { ["pipe"] = PipePath });
    }

    private async Task RunAsync(CancellationToken ct)
    {
        bool first = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Created through Vht.Interop.Control so that the pipe this *elevated* worker owns
                // is still writable by the unelevated window that started it: an explicit DACL plus
                // a Medium mandatory integrity label (docs/UI.md section 1).
                NamedPipeServerStream pipe = NamedPipeSecurity.CreateServerPipe(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    out PipeSecurityReport security);

                PipeSecurity = security;

                if (first)
                {
                    first = false;
                    ReportPipeSecurity(security);
                }

                await using (pipe.ConfigureAwait(false))
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await ServeConnectionAsync(pipe, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                _log.Write(LogLevel.Warn, LogEvent.ControlPlaneFailed,
                    $"control plane pipe error: {ex.Message}");
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Error, LogEvent.ControlPlaneFailed,
                    $"control plane failed: {ex.Message}");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Records the pipe's security state in the server's own log. The descriptor goes out at
    /// Detail level because it is diagnostic; a label that is not Medium is a warning, because it
    /// means an unelevated window will be refused and the operator needs to be able to see why.
    /// </summary>
    private void ReportPipeSecurity(PipeSecurityReport security)
    {
        _log.Write(LogLevel.Detail, LogEvent.ControlPlaneStarted,
            $"control pipe security: {security.Descriptor} label={security.IntegrityLabel}",
            new Dictionary<string, object?>
            {
                ["pipe"] = PipePath,
                ["descriptor"] = security.Descriptor,
                ["integrity_label"] = security.IntegrityLabel,
                ["integrity_label_medium"] = security.MediumIntegrityLabelPresent,
                ["interactive_user_ace"] = security.InteractiveUserHasAnAce,
            });

        if (security.Problem is not null)
        {
            _log.Write(LogLevel.Warn, LogEvent.ControlPlaneFailed,
                $"control pipe security is not what the window needs: {security.Problem}");
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return;
            if (line.Trim().Length == 0) continue;

            JsonObject response = Handle(line);
            await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            Interlocked.Increment(ref _requests);
        }
    }

    private long _requests;

    /// <summary>
    /// Dispatches one request line. Public so the acceptance harness can drive verbs without a
    /// pipe; the pipe loop is a thin wrapper around it.
    /// </summary>
    public JsonObject Handle(string line)
    {
        RequestsServed = Interlocked.Read(ref _requests);

        string verb;
        JsonObject request;
        try
        {
            JsonNode? node = JsonNode.Parse(line);
            if (node is not JsonObject obj)
                return Error("request must be a JSON object");

            request = obj;
            verb = obj["verb"]?.GetValue<string>() ?? string.Empty;
        }
        catch (JsonException ex)
        {
            return Error($"malformed JSON: {ex.Message}");
        }

        if (verb.Length == 0)
            return Error("request has no 'verb' field");

        JsonObject response;
        try
        {
            response = Dispatch(verb, request);
        }
        catch (Exception ex)
        {
            response = Error($"{verb} failed: {ex.Message}");
        }

        Interlocked.Increment(ref _requests);

        _log.Write(LogLevel.Detail, LogEvent.ControlPlaneCommand,
            $"control plane verb {verb}: {(response["ok"]?.GetValue<bool>() == true ? "ok" : "error")}",
            new Dictionary<string, object?> { ["verb"] = verb });

        return response;
    }

    private JsonObject Dispatch(string verb, JsonObject request) => verb switch
    {
        "server.status" => Status(),
        "server.stop" => Stop(),
        "devices.list" => List(),
        "devices.kick" => Kick(request),
        "devices.ignore" => SetIgnore(request, ignore: true),
        "devices.unignore" => SetIgnore(request, ignore: false),
        "devices.nickname" => SetNickname(request),
        "devices.password" => SetPassword(request),
        "log.tail" => LogTail(request),
        "clients.attach" or "clients.detach" or "peers.add" or "peers.list" =>
            Error($"'{verb}' is a vht-client verb; it is served on \\\\.\\pipe\\vht-client"),
        _ => Error($"unknown verb '{verb}'"),
    };

    private JsonObject Status()
    {
        IReadOnlyList<ExportEntry> exports = _state.Exports.Snapshot();
        IReadOnlyList<AttachRecord> attached = _state.Attachments.Snapshot();

        return new JsonObject
        {
            ["ok"] = true,
            ["version"] = Core.VhtVersion.Version,
            ["banner"] = _state.Banner,
            ["server_name"] = _state.Options.ServerName,
            ["exported_devices"] = exports.Count,
            ["attached_devices"] = attached.Count,
            ["connections"] = _state.Connections.Count,
            ["listeners"] = new JsonArray(_state.Options.Listeners()
                .Select(l => (JsonNode)$"{l.Address}:{l.Port} ({l.Kind})").ToArray()),
            ["capture_mode"] = _state.Options.CaptureMode,
            ["rebind_enabled"] = _state.Options.RebindEnabled,
            ["licence"] = "none: VirtualThere has no licence file, no activation and no paid tier",
            // Reported explicitly, and null: the reference product's cap is max_devices, and
            // this field is where it would have to appear. There is no field to hold one.
            ["device_limit"] = null,
            ["connection_limit"] = null,
            ["export_generation"] = _state.ExportGeneration,
            ["max_in_flight_urbs_per_connection"] = _state.Resources.MaxInFlightUrbs,
            ["max_transfer_buffer_per_connection"] = _state.Resources.MaxTransferBuffer,
        };
    }

    private JsonObject Stop()
    {
        if (_stopServer is null)
            return Error("this server instance has no stop callback (running in console mode without one)");

        // The reply is written before the server tears down, so the caller always sees it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50).ConfigureAwait(false);
            await _stopServer().ConfigureAwait(false);
        });

        return new JsonObject { ["ok"] = true, ["stopping"] = true };
    }

    private JsonObject List()
    {
        var devices = new JsonArray();

        foreach (ExportEntry entry in _state.Exports.Snapshot())
        {
            UsbDevice device = entry.Device;
            bool attached = _state.Attachments.TryGet(device.Key, out AttachRecord record);

            devices.Add(new JsonObject
            {
                ["busid"] = device.BusId,
                ["vid"] = device.VendorId.ToString("x4"),
                ["pid"] = device.ProductId.ToString("x4"),
                ["serial"] = device.Serial,
                ["manufacturer"] = device.Manufacturer,
                ["product"] = device.Product,
                ["nickname"] = device.Nickname,
                ["display_name"] = device.DisplayName,
                ["speed"] = Core.Protocol.UsbIp.SpeedName(device.Speed),
                ["class"] = UsbClass.Name(device.DeviceClass),
                ["state"] = device.CaptureState.ToString(),
                ["reason"] = device.CaptureReason.Describe(),
                ["path"] = device.SysPath,
                ["instance_path"] = device.InstancePath,
                ["password_required"] = device.RequiresPassword,
                ["attached"] = attached,
                ["attached_to"] = attached ? record.RemoteAddress : null,
                ["connection_id"] = attached ? record.ConnectionId.Value : null,
                ["binds"] = entry.BindCount,
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["count"] = devices.Count,
            ["device_limit"] = null,
            ["devices"] = devices,
        };
    }

    private JsonObject Kick(JsonObject request)
    {
        string busId = request["busid"]?.GetValue<string>() ?? string.Empty;
        if (busId.Length == 0)
            return Error("devices.kick requires 'busid'");

        if (!_state.KickDevice(DeviceKey.FromBusId(busId), out AttachRecord? record, out string reason))
            return Error(reason);

        return new JsonObject
        {
            ["ok"] = true,
            ["busid"] = busId,
            ["connection_id"] = record!.ConnectionId.Value,
            ["client"] = record.RemoteAddress,
        };
    }

    private JsonObject SetIgnore(JsonObject request, bool ignore)
    {
        string rule = request["rule"]?.GetValue<string>()
            ?? request["device"]?.GetValue<string>()
            ?? string.Empty;

        if (!DeviceRule.TryParse(rule, out DeviceRule parsed))
            return Error($"'{rule}' is not in vvvv:pppp[:serial][@server] form");

        if (ignore)
        {
            _state.Policy.Ignore(parsed);
            _log.Write(LogLevel.Info, LogEvent.DeviceIgnored,
                $"policy: ignoring {parsed.Describe()}",
                new Dictionary<string, object?> { ["rule"] = parsed.Describe() });
        }
        else
        {
            if (!_state.Policy.Unignore(parsed))
                return Error($"{parsed.Describe()} was not ignored");

            _log.Write(LogLevel.Info, LogEvent.DeviceUnignored,
                $"policy: un-ignoring {parsed.Describe()}",
                new Dictionary<string, object?> { ["rule"] = parsed.Describe() });
        }

        // Re-evaluate immediately: an ignored device leaves the export table now, and an
        // un-ignored device appears now, rather than at the next hotplug event.
        RefreshExportsSync();

        HookEvent hook = ignore ? HookEvent.OnDeviceIgnore : HookEvent.OnDeviceUnignore;
        _ = RunHookForRuleAsync(hook, parsed);

        return new JsonObject
        {
            ["ok"] = true,
            ["rule"] = parsed.Describe(),
            ["ignored"] = ignore,
            ["exported_devices"] = _state.Exports.Count,
        };
    }

    private async Task RunHookForRuleAsync(HookEvent hook, DeviceRule rule)
    {
        foreach (ExportEntry entry in _state.Exports.Snapshot())
        {
            if (!rule.Matches(entry.Device.Identity, _state.Options.ServerName)) continue;
            await _state.RunHookAsync(hook, entry.Device, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private JsonObject SetNickname(JsonObject request)
    {
        string busId = request["busid"]?.GetValue<string>() ?? string.Empty;
        string nickname = request["nickname"]?.GetValue<string>() ?? string.Empty;

        if (busId.Length == 0)
            return Error("devices.nickname requires 'busid'");
        if (nickname.Contains(',', StringComparison.Ordinal))
            return Error("a nickname may not contain a comma; the reference product's delimiter rule is kept");

        UsbDevice? device = FindDevice(busId);
        if (device is null)
            return Error($"no exported device with bus id '{busId}'");

        _state.Policy.SetNickname(device.Identity, nickname.Length == 0 ? null : nickname);

        _log.Write(LogLevel.Info, LogEvent.NicknameChanged,
            $"{busId} nickname -> '{nickname}'",
            new Dictionary<string, object?> { ["busid"] = busId, ["nickname"] = nickname });

        RefreshExportsSync();

        _ = _state.RunHookAsync(HookEvent.OnChangeNickname, device, CancellationToken.None,
            newNickname: nickname);

        return new JsonObject { ["ok"] = true, ["busid"] = busId, ["nickname"] = nickname };
    }

    private JsonObject SetPassword(JsonObject request)
    {
        string busId = request["busid"]?.GetValue<string>() ?? string.Empty;
        string password = request["password"]?.GetValue<string>() ?? string.Empty;

        if (busId.Length == 0)
            return Error("devices.password requires 'busid'");

        UsbDevice? device = FindDevice(busId);
        if (device is null)
            return Error($"no exported device with bus id '{busId}'");

        _state.Policy.SetPasswordVerifier(
            device.Identity,
            password.Length == 0 ? null : PasswordVerifier.Create(password));

        RefreshExportsSync();

        return new JsonObject
        {
            ["ok"] = true,
            ["busid"] = busId,
            ["password_required"] = password.Length > 0,
            // §6.4: a password-protected device is not advertised on the plaintext listener.
            ["plaintext_listener_eligible"] = password.Length == 0,
        };
    }

    private JsonObject LogTail(JsonObject request)
    {
        int lines = request["lines"]?.GetValue<int>() ?? 100;
        if (_ring is null)
            return Error("no in-memory log buffer is configured for this server instance");

        return new JsonObject
        {
            ["ok"] = true,
            ["lines"] = lines,
            ["text"] = _ring.TailText(lines),
        };
    }

    private UsbDevice? FindDevice(string busId)
    {
        DeviceKey key = DeviceKey.FromBusId(busId);
        return _state.Exports.TryGet(key, out ExportEntry entry) ? entry.Device : null;
    }

    /// <summary>
    /// Re-runs enumeration policy without waiting for a hotplug event; used by the mutating
    /// verbs so <c>vht ignore</c> takes effect on the next <c>usbip list</c> rather than
    /// whenever the next device change happens.
    /// </summary>
    private void RefreshExportsSync()
    {
        try
        {
            _state.RefreshExportsAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warn, LogEvent.DeviceEnumerated,
                $"export refresh after a policy change failed: {ex.Message}");
        }
    }

    private static JsonObject Error(string message) => new()
    {
        ["ok"] = false,
        ["error"] = message,
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _stop.Dispose();
    }
}
