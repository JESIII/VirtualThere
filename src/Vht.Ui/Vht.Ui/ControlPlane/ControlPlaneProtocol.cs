using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vht.Ui.ControlPlane;

/// <summary>
/// The frozen control-plane vocabulary (docs/ARCHITECTURE.md Appendix C). The tray sends
/// exactly these verbs and no others; a service rejects anything else with an error object.
/// </summary>
public static class ControlPlaneVerbs
{
    public const string DevicesList = "devices.list";
    public const string DevicesKick = "devices.kick";
    public const string DevicesIgnore = "devices.ignore";
    public const string DevicesUnignore = "devices.unignore";
    public const string DevicesNickname = "devices.nickname";
    public const string DevicesPassword = "devices.password";
    public const string ServerStatus = "server.status";
    public const string ServerStart = "server.start";
    public const string ServerStop = "server.stop";
    public const string LogTail = "log.tail";
    public const string ClientsAttach = "clients.attach";
    public const string ClientsDetach = "clients.detach";
    public const string PeersAdd = "peers.add";
    public const string PeersList = "peers.list";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        DevicesList, DevicesKick, DevicesIgnore, DevicesUnignore, DevicesNickname, DevicesPassword,
        ServerStatus, ServerStart, ServerStop, LogTail,
        ClientsAttach, ClientsDetach, PeersAdd, PeersList,
    };
}

public enum ControlPlaneStatus
{
    Ok,
    Error,
    Unavailable,
    Malformed,
}

public sealed class ControlPlaneError
{
    public ControlPlaneError(string code, string message)
    {
        Code = code;
        Message = message;
    }

    public string Code { get; }

    public string Message { get; }

    public override string ToString() =>
        string.IsNullOrEmpty(Code) ? Message : Code + ": " + Message;
}

/// <summary>One response line from a service, already classified.</summary>
public sealed class ControlPlaneResponse
{
    private ControlPlaneResponse(ControlPlaneStatus status, string verb, JsonNode? result, ControlPlaneError? error)
    {
        Status = status;
        Verb = verb;
        Result = result;
        Error = error;
    }

    public ControlPlaneStatus Status { get; }

    public string Verb { get; }

    /// <summary>The payload the verb defines, flattened out of any <c>result</c>/<c>data</c> wrapper.</summary>
    public JsonNode? Result { get; }

    public ControlPlaneError? Error { get; }

    public bool IsOk => Status == ControlPlaneStatus.Ok;

    public static ControlPlaneResponse Ok(string verb, JsonNode? result) =>
        new(ControlPlaneStatus.Ok, verb, result, null);

    public static ControlPlaneResponse Fail(string verb, string code, string message) =>
        new(ControlPlaneStatus.Error, verb, null, new ControlPlaneError(code, message));

    public static ControlPlaneResponse Unavailable(string verb, string message) =>
        new(ControlPlaneStatus.Unavailable, verb, null, new ControlPlaneError("service_unavailable", message));

    public static ControlPlaneResponse Malformed(string verb, string message) =>
        new(ControlPlaneStatus.Malformed, verb, null, new ControlPlaneError("malformed_response", message));

    /// <summary>
    /// Parses one JSON line. Tolerant on purpose, because the response envelope is not
    /// frozen to the same depth as the verb list: it accepts a bare payload
    /// (<c>{"devices":[...]}</c>), a payload wrapped in <c>result</c> or <c>data</c>,
    /// an explicit <c>{"ok":false,"error":{...}}</c>, and a bare <c>{"error":"text"}</c>.
    /// See docs/UI.md, "Response contract".
    /// </summary>
    public static ControlPlaneResponse Parse(string line, string expectedVerb)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            return Malformed(expectedVerb, "response was not valid JSON: " + ex.Message);
        }

        if (node is not JsonObject obj)
        {
            return Malformed(expectedVerb, "response was not a JSON object.");
        }

        string verb = obj["verb"]?.GetValue<string>() ?? expectedVerb;

        bool? okFlag = obj["ok"] is JsonNode okNode && okNode.GetValueKind() == JsonValueKind.False
            ? false
            : obj["ok"] is JsonNode okTrue && okTrue.GetValueKind() == JsonValueKind.True ? true : null;

        if (okFlag == false)
        {
            return Fail(verb, ReadErrorCode(obj), ReadErrorMessage(obj));
        }

        if (obj["error"] is JsonNode error && error.GetValueKind() != JsonValueKind.Null &&
            error.GetValueKind() != JsonValueKind.False)
        {
            return Fail(verb, ReadErrorCode(obj), ReadErrorMessage(obj));
        }

        JsonNode? result = obj["result"] ?? obj["data"] ?? obj["payload"] ?? obj;
        return Ok(verb, result);
    }

    private static string ReadErrorCode(JsonObject obj)
    {
        if (obj["error"] is JsonObject err)
        {
            return err["code"]?.GetValue<string>() ?? "error";
        }

        return obj["code"]?.GetValue<string>() ?? "error";
    }

    private static string ReadErrorMessage(JsonObject obj)
    {
        if (obj["error"] is JsonObject err)
        {
            return err["message"]?.GetValue<string>() ?? err.ToJsonString();
        }

        if (obj["error"] is JsonNode scalar && scalar.GetValueKind() == JsonValueKind.String)
        {
            return scalar.GetValue<string>();
        }

        return obj["message"]?.GetValue<string>() ?? "the service reported an error.";
    }

    /// <summary>Human text for the status strip.</summary>
    public string Describe()
    {
        return Status switch
        {
            ControlPlaneStatus.Ok => "ok",
            ControlPlaneStatus.Unavailable => "service unavailable - " + (Error?.Message ?? string.Empty),
            ControlPlaneStatus.Malformed => "unexpected response - " + (Error?.Message ?? string.Empty),
            _ => "rejected - " + (Error?.ToString() ?? "unknown error"),
        };
    }
}

/// <summary>Small tolerant readers for the untyped payloads the verbs return.</summary>
public static class JsonRead
{
    public static string? String(JsonNode? container, string name)
    {
        JsonNode? value = container?[name];
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        return value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToJsonString();
    }

    public static int Int(JsonNode? container, string name, int fallback = 0)
    {
        JsonNode? value = container?[name];
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.GetValueKind() == JsonValueKind.Number && value.AsValue().TryGetValue(out int number))
        {
            return number;
        }

        if (value.GetValueKind() == JsonValueKind.String &&
            int.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public static bool Bool(JsonNode? container, string name, bool fallback = false)
    {
        JsonNode? value = container?[name];
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.GetValueKind() == JsonValueKind.True)
        {
            return true;
        }

        if (value.GetValueKind() == JsonValueKind.False)
        {
            return false;
        }

        if (value.GetValueKind() == JsonValueKind.String &&
            bool.TryParse(value.GetValue<string>(), out bool parsed))
        {
            return parsed;
        }

        return fallback;
    }

    /// <summary>
    /// Reads the first array found under any of the given names, so a service that answers
    /// with <c>{"devices":[...]}</c> or <c>{"result":{"devices":[...]}}</c> both work.
    /// </summary>
    public static JsonArray? Array(JsonNode? container, params string[] names)
    {
        foreach (string name in names)
        {
            if (container?[name] is JsonArray direct)
            {
                return direct;
            }

            if (container?["result"]?[name] is JsonArray nested)
            {
                return nested;
            }
        }

        return null;
    }
}
