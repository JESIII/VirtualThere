using System.Text.Json.Nodes;
using Vht.Core.Logging;
using Vht.Core.Server;
using Vht.Interop;
using Vht.Interop.Services;
using Vht.Server.Config;
using Vht.Server.Control;
using Vht.Server.Devices;

namespace Vht.Server.Hosting;

/// <summary>
/// The verbs both <c>vht-server</c> and <c>vht</c> expose: everything that talks to a running
/// server, plus service installation. Kept in one place so the two binaries cannot drift.
/// </summary>
public static class ServerCommands
{
    /// <summary><c>status</c> — asks the control plane whether a server is live.</summary>
    public static int Status(TextWriter output, string? pipeName = null)
    {
        using var client = new ControlClient(pipeName);
        try
        {
            JsonObject status = client.Status();
            output.WriteLine($"control plane: {client.PipePath}");
            output.WriteLine($"banner:        {status["banner"]}");
            output.WriteLine($"server name:   {status["server_name"]}");
            output.WriteLine($"exported:      {status["exported_devices"]} device(s)");
            output.WriteLine($"attached:      {status["attached_devices"]} device(s)");
            output.WriteLine($"connections:   {status["connections"]}");
            output.WriteLine($"capture mode:  {status["capture_mode"]}");
            output.WriteLine($"device limit:  {status["device_limit"]?.ToJsonString() ?? "null"} (no cap)");
            output.WriteLine($"licence:       {status["licence"]}");
            output.WriteLine("listeners:");
            if (status["listeners"] is JsonArray listeners)
                foreach (JsonNode? listener in listeners)
                    output.WriteLine($"  {listener}");
            return 0;
        }
        catch (ControlPlaneException ex)
        {
            output.WriteLine($"server is not answering: {ex.Message}");
            return 3;
        }
    }

    /// <summary><c>list</c> — devices from the running server, or locally with <c>--local</c>.</summary>
    public static int List(TextWriter output, bool local, string? pipeName = null, ServerOptions? options = null)
    {
        if (local)
        {
            var opts = options ?? new ServerOptions();
            var logger = new StructuredLogger(LogLevel.Warn);
            var enumerator = new UsbDeviceEnumerator(opts, logger);
            IReadOnlyList<Core.Model.UsbDevice> devices = enumerator.Enumerate();

            foreach (Core.Model.UsbDevice device in devices)
                output.WriteLine(device.Describe());

            output.WriteLine($"({devices.Count} device(s) present; no device limit)");
            return 0;
        }

        using var client = new ControlClient(pipeName);
        try
        {
            JsonObject list = client.ListDevices();
            if (list["devices"] is not JsonArray devices || devices.Count == 0)
            {
                output.WriteLine("no exported devices");
                return 0;
            }

            foreach (JsonNode? node in devices)
            {
                if (node is not JsonObject device) continue;
                output.WriteLine(
                    $"{device["busid"],-14} [{device["vid"]}:{device["pid"]}] " +
                    $"\"{device["display_name"]}\" state={device["state"]} " +
                    (device["attached"]?.GetValue<bool>() == true
                        ? $"attached to {device["attached_to"]}"
                        : "free"));
            }

            output.WriteLine($"({devices.Count} device(s); no device limit)");
            return 0;
        }
        catch (ControlPlaneException ex)
        {
            output.WriteLine($"server is not answering: {ex.Message}");
            return 3;
        }
    }

    /// <summary><c>kick</c> — force-detaches the client holding <paramref name="busId"/>.</summary>
    public static int Kick(TextWriter output, string busId, string? pipeName = null)
    {
        using var client = new ControlClient(pipeName);
        try
        {
            JsonObject response = client.Kick(busId);
            output.WriteLine($"kicked {busId} from connection {response["connection_id"]} ({response["client"]})");
            return 0;
        }
        catch (ControlPlaneException ex)
        {
            output.WriteLine($"kick failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary><c>ignore</c> — hides a device from the export list.</summary>
    public static int Ignore(TextWriter output, string rule, string? pipeName = null) =>
        Policy(output, rule, ignore: true, pipeName);

    /// <summary><c>unignore</c> — restores a hidden device.</summary>
    public static int Unignore(TextWriter output, string rule, string? pipeName = null) =>
        Policy(output, rule, ignore: false, pipeName);

    private static int Policy(TextWriter output, string rule, bool ignore, string? pipeName)
    {
        using var client = new ControlClient(pipeName);
        try
        {
            JsonObject response = ignore ? client.Ignore(rule) : client.Unignore(rule);
            output.WriteLine($"{(ignore ? "ignoring" : "un-ignoring")} {response["rule"]}; " +
                             $"{response["exported_devices"]} device(s) exported");
            return 0;
        }
        catch (ControlPlaneException ex)
        {
            output.WriteLine($"{(ignore ? "ignore" : "unignore")} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary><c>nickname</c> — sets or clears a device's display name.</summary>
    public static int Nickname(TextWriter output, string busId, string nickname, string? pipeName = null)
    {
        using var client = new ControlClient(pipeName);
        try
        {
            client.Nickname(busId, nickname);
            output.WriteLine($"{busId} nickname set to '{nickname}'");
            return 0;
        }
        catch (ControlPlaneException ex)
        {
            output.WriteLine($"nickname failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary><c>password</c> — sets or clears a device password (empty clears it).</summary>
    public static int Password(TextWriter output, string busId, string password, string? pipeName = null)
    {
        using var client = new ControlClient(pipeName);
        try
        {
            JsonObject response = client.Password(busId, password);
            output.WriteLine(response["password_required"]?.GetValue<bool>() == true
                ? $"{busId} now requires a password (TLS listener only)"
                : $"{busId} password cleared");
            return 0;
        }
        catch (ControlPlaneException ex)
        {
            output.WriteLine($"password failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary><c>stop</c> — asks a running server to shut down cleanly.</summary>
    public static int StopServer(TextWriter output, string? pipeName = null)
    {
        using var client = new ControlClient(pipeName);
        try
        {
            client.StopServer();
            output.WriteLine("stop requested");
            return 0;
        }
        catch (ControlPlaneException ex)
        {
            output.WriteLine($"stop failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary><c>install</c> — registers the service with the SCM (automatic start).</summary>
    public static int Install(TextWriter output, string serviceName = ServiceControl.DefaultServiceName)
    {
        string binary = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "vht-server.exe");

        ServiceOperationResult result = WindowsServiceManager.Install(binary, serviceName);

        output.WriteLine(result.Ok
            ? $"{result.Detail}; binary: {binary}"
            : $"install failed: {result}");

        if (!result.Ok && !WindowsServiceManager.IsElevated)
            output.WriteLine("this shell is not elevated; service installation needs administrator rights");

        return result.Ok ? 0 : 1;
    }

    /// <summary><c>uninstall</c> — stops and removes the service.</summary>
    public static int Uninstall(TextWriter output, string serviceName = ServiceControl.DefaultServiceName)
    {
        ServiceOperationResult result = WindowsServiceManager.Uninstall(serviceName);
        output.WriteLine(result.Ok ? result.Detail : $"uninstall failed: {result}");
        return result.Ok ? 0 : 1;
    }

    /// <summary><c>start</c> — starts the installed service.</summary>
    public static int Start(TextWriter output, string serviceName = ServiceControl.DefaultServiceName)
    {
        ServiceOperationResult result = WindowsServiceManager.Start(serviceName);
        output.WriteLine(result.Ok ? result.Detail : $"start failed: {result}");
        return result.Ok ? 0 : 1;
    }

    /// <summary><c>stop-service</c> — stops the installed service (not the pipe verb).</summary>
    public static int StopService(TextWriter output, string serviceName = ServiceControl.DefaultServiceName)
    {
        ServiceOperationResult result = WindowsServiceManager.Stop(serviceName);
        output.WriteLine(result.Ok ? result.Detail : $"stop failed: {result}");
        return result.Ok ? 0 : 1;
    }

    /// <summary><c>service-status</c> — queries the SCM directly.</summary>
    public static int ServiceStatus(TextWriter output, string serviceName = ServiceControl.DefaultServiceName)
    {
        ServiceOperationResult result = WindowsServiceManager.Query(serviceName);
        output.WriteLine(result.Ok ? result.Detail : $"query failed: {result}");
        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// Writes the commented configuration template, creating the directory it lands in - the default
    /// path is <c>./config/server.toml</c> in a portable build (where nothing has created that
    /// directory yet) and <c>%ProgramData%\VirtualThere\server.toml</c> on an installed machine.
    /// </summary>
    public static int WriteConfigTemplate(TextWriter output, string path)
    {
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, ServerConfigLoader.Template);
            output.WriteLine($"wrote configuration template to {path}");
            return 0;
        }
        catch (IOException ex)
        {
            output.WriteLine($"could not write {path}: {ex.Message}");
            return 1;
        }
    }
}
