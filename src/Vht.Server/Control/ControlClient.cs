using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vht.Interop;

namespace Vht.Server.Control;

/// <summary>Raised when the control plane cannot be reached or answers with an error.</summary>
public sealed class ControlPlaneException : Exception
{
    public ControlPlaneException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// A client for the server's named-pipe control plane: one JSON line out, one back.
/// </summary>
/// <remarks>
/// Used by <c>vht</c> (and by the tray, through the same verbs). It never falls back to a
/// second, in-process path: if the control plane cannot be reached, the operation fails and
/// says so, so a UI can never report a kick that did not happen.
/// </remarks>
public sealed class ControlClient : IDisposable
{
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;

    public ControlClient(string? pipeName = null, TimeSpan? timeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? ServiceControl.DefaultServiceName : pipeName;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>The pipe this client talks to.</summary>
    public string PipePath => $@"\\.\pipe\{_pipeName}";

    /// <summary>Sends one request and returns the response. Throws on transport failure.</summary>
    public JsonObject Send(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect((int)_timeout.TotalMilliseconds);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);

            writer.WriteLine(request.ToJsonString());

            string? line = reader.ReadLine();
            if (line is null)
                throw new ControlPlaneException($"{PipePath} closed without answering");

            return JsonNode.Parse(line) as JsonObject
                ?? throw new ControlPlaneException($"{PipePath} answered with something that is not a JSON object");
        }
        catch (TimeoutException ex)
        {
            throw new ControlPlaneException(
                $"no answer from {PipePath} within {_timeout.TotalSeconds:0.#}s; is the service running?", ex);
        }
        catch (IOException ex)
        {
            throw new ControlPlaneException(
                $"could not talk to {PipePath}: {ex.Message}; is the service running?", ex);
        }
        catch (JsonException ex)
        {
            throw new ControlPlaneException($"{PipePath} answered with malformed JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Sends a request and requires <c>ok</c> to be true.</summary>
    public JsonObject SendChecked(JsonObject request)
    {
        JsonObject response = Send(request);

        if (response["ok"]?.GetValue<bool>() != true)
            throw new ControlPlaneException(response["error"]?.GetValue<string>() ?? "the server refused the request");

        return response;
    }

    public JsonObject Status() => SendChecked(new JsonObject { ["verb"] = "server.status" });

    public JsonObject ListDevices() => SendChecked(new JsonObject { ["verb"] = "devices.list" });

    public JsonObject Kick(string busId) => SendChecked(new JsonObject
    {
        ["verb"] = "devices.kick",
        ["busid"] = busId,
    });

    public JsonObject Ignore(string rule) => SendChecked(new JsonObject
    {
        ["verb"] = "devices.ignore",
        ["rule"] = rule,
    });

    public JsonObject Unignore(string rule) => SendChecked(new JsonObject
    {
        ["verb"] = "devices.unignore",
        ["rule"] = rule,
    });

    public JsonObject Nickname(string busId, string nickname) => SendChecked(new JsonObject
    {
        ["verb"] = "devices.nickname",
        ["busid"] = busId,
        ["nickname"] = nickname,
    });

    public JsonObject Password(string busId, string password) => SendChecked(new JsonObject
    {
        ["verb"] = "devices.password",
        ["busid"] = busId,
        ["password"] = password,
    });

    public JsonObject LogTail(int lines = 100) => SendChecked(new JsonObject
    {
        ["verb"] = "log.tail",
        ["lines"] = lines,
    });

    public JsonObject StopServer() => SendChecked(new JsonObject { ["verb"] = "server.stop" });

    /// <summary>True when a control plane is answering on the pipe.</summary>
    public bool TryPing()
    {
        try
        {
            Status();
            return true;
        }
        catch (ControlPlaneException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Nothing is held between calls: the pipe is opened per request so a dropped service
        // never leaves the CLI holding a half-open handle.
    }
}
