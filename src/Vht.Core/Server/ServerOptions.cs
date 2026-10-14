namespace Vht.Core.Server;

using Vht.Core.Capture;
using Vht.Core.Concurrency;
using Vht.Core.Hooks;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Protocol;
using Vht.Core.Transport;

/// <summary>
/// Everything the server needs, as a single injectable object. The config card
/// (<c>config-workstream</c>) binds its TOML document onto these keys; the CLI and the service host
/// build one directly. Key names mirror the frozen vocabulary in ARCHITECTURE.md Appendix C.
/// </summary>
public sealed class ServerOptions
{
    /// <summary><c>server.name</c> — advertised server name (reference vocabulary: <c>ServerName</c>).</summary>
    public string ServerName { get; set; } = Environment.MachineName;

    /// <summary><c>transport.plaintext.enabled</c> — serve stock USB/IP for interop.</summary>
    public bool PlaintextListenerEnabled { get; set; } = true;

    /// <summary><c>transport.plaintext.port</c> — stock USB/IP port (reference vocabulary: <c>TCPPort</c>).</summary>
    public int PlaintextPort { get; set; } = 3240;

    /// <summary><c>transport.plaintext.address</c> — bind address for the plaintext listener.</summary>
    public string PlaintextAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// <c>transport.tls.enabled</c> — the TLS listener. Default off until the transport card
    /// lands its implementation, so the server never claims an encryption it does not have.
    /// </summary>
    public bool TlsListenerEnabled { get; set; }

    /// <summary><c>transport.tls.port</c> — TLS port (reference vocabulary: <c>SSLPort</c>).</summary>
    public int TlsPort { get; set; } = 3241;

    /// <summary><c>transport.tls.address</c> — bind address for the TLS listener.</summary>
    public string TlsAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// <c>capture.mode</c> — <c>auto</c> | <c>winusb-only</c> | <c>prebound-only</c>
    /// (ARCHITECTURE.md §4.1). Default <c>auto</c>.
    /// </summary>
    public string CaptureMode { get; set; } = "auto";

    /// <summary>
    /// <c>capture.rebind.enabled</c> — permits the P2 path (installing a device-specific INF
    /// that binds the inbox <c>winusb.sys</c>). A device is only rebound when the operator opts
    /// in, never silently (ARCHITECTURE.md risk 4).
    /// </summary>
    public bool RebindEnabled { get; set; }

    /// <summary><c>capture.refuse_management_nic</c> — refuse to export the management interface's NIC.</summary>
    public bool RefuseManagementNic { get; set; } = true;

    /// <summary><c>policy.allow</c> — allow list; empty means "everything not ignored".</summary>
    public List<string> AllowList { get; } = new();

    /// <summary><c>policy.ignore</c> — ignore list.</summary>
    public List<string> IgnoreList { get; } = new();

    /// <summary><c>connections.max_in_flight_urbs</c> — per-connection guard, not a device cap.</summary>
    public int MaxInFlightUrbs { get; set; } = 256;

    /// <summary><c>connections.max_transfer_buffer</c> — per-connection size guard.</summary>
    public int MaxTransferBuffer { get; set; } = UsbIp.MaxTransferBuffer;

    /// <summary><c>logging.level</c> — minimum level.</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary><c>logging.file</c> — structured log path; empty means console/Event Log only.</summary>
    public string? LogFile { get; set; }

    /// <summary><c>control_plane.enabled</c> — the named-pipe control channel the tray drives.</summary>
    public bool ControlPlaneEnabled { get; set; } = true;

    /// <summary><c>control_plane.pipe_name</c> — pipe name under <c>\\.\pipe\</c>.</summary>
    public string ControlPlanePipeName { get; set; } = "vht-server";

    /// <summary><c>hooks.*</c> — hook configuration.</summary>
    public HookOptions Hooks { get; } = new();

    /// <summary>Validates the options, returning every problem found rather than the first.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerName))
            problems.Add("server.name must not be empty");

        if (PlaintextListenerEnabled && (PlaintextPort < 1 || PlaintextPort > 65535))
            problems.Add($"transport.plaintext.port {PlaintextPort} is out of range");

        if (TlsListenerEnabled && (TlsPort < 1 || TlsPort > 65535))
            problems.Add($"transport.tls.port {TlsPort} is out of range");

        if (PlaintextListenerEnabled && TlsListenerEnabled && PlaintextPort == TlsPort)
            problems.Add("transport.plaintext.port and transport.tls.port must differ");

        if (!PlaintextListenerEnabled && !TlsListenerEnabled)
            problems.Add("at least one listener must be enabled");

        if (CaptureMode is not ("auto" or "winusb-only" or "prebound-only"))
            problems.Add($"capture.mode '{CaptureMode}' is not one of auto|winusb-only|prebound-only");

        foreach (string rule in AllowList.Concat(IgnoreList))
        {
            if (!DeviceRule.TryParse(rule, out _))
                problems.Add($"device rule '{rule}' is not in vvvv:pppp[:serial][@server] form");
        }

        if (MaxInFlightUrbs < 1)
            problems.Add("connections.max_in_flight_urbs must be at least 1");

        if (MaxTransferBuffer < 1)
            problems.Add("connections.max_transfer_buffer must be at least 1");

        if (CaptureMode == "prebound-only" && RebindEnabled)
            problems.Add("capture.mode=prebound-only conflicts with capture.rebind.enabled=true");

        return problems;
    }

    /// <summary>Builds a policy object from the configured rule strings.</summary>
    public DevicePolicy BuildPolicy(string? serverName = null)
    {
        var policy = new DevicePolicy { ServerName = serverName ?? ServerName };
        foreach (string text in AllowList)
            if (DeviceRule.TryParse(text, out DeviceRule rule)) policy.Allow(rule);
        foreach (string text in IgnoreList)
            if (DeviceRule.TryParse(text, out DeviceRule rule)) policy.Ignore(rule);
        return policy;
    }

    /// <summary>
    /// The listening endpoints this configuration implies.
    /// </summary>
    public IReadOnlyList<(TransportKind Kind, string Address, int Port)> Listeners()
    {
        var list = new List<(TransportKind, string, int)>();
        if (PlaintextListenerEnabled) list.Add((TransportKind.PlainTcp, PlaintextAddress, PlaintextPort));
        if (TlsListenerEnabled) list.Add((TransportKind.Tls, TlsAddress, TlsPort));
        return list;
    }
}
