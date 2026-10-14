using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Vht.Ui.MockService;

/// <summary>
/// In-memory server and client state behind the two pipes, with the frozen verbs applied to it.
/// Unknown verbs are rejected with an error object, never ignored (Appendix C).
/// </summary>
internal sealed class MockState
{
    private readonly object _gate = new();
    private readonly List<JsonObject> _devices = new();
    private readonly List<JsonObject> _peers = new();
    private readonly List<JsonObject> _log = new();
    private readonly string _scenario;
    private bool _running;
    private int _logSequence = 1;

    public MockState(string scenario)
    {
        _scenario = scenario;
        SeedDevices();
        SeedPeers();
        Log("server", "info", "VirtualThere server service idle; listeners closed");
        if (!string.Equals(scenario, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            Log("server", "info", "Enumeration complete: " + _devices.Count.ToString(CultureInfo.InvariantCulture) + " USB devices considered");
        }
    }

    private void SeedDevices()
    {
        AddDevice("usb\\vid_046d&pid_c52b\\5&1a2b3c4d&0&2", "1-4", "Barcode scanner", 0x046d, 0xc52b,
            "Logitech", "USB Receiver", CaptureStateCapturable, "None", attached: 1, ignored: false, password: false);

        AddDevice("usb\\vid_04b8&pid_0202\\5&2b3c4d5e&0&1", "1-5", "Label printer", 0x04b8, 0x0202,
            "Seiko Epson", "TM-T88VI", CaptureStateCapturable, "None", attached: 0, ignored: false, password: false);

        AddDevice("usb\\vid_046d&pid_0a38\\5&3c4d5e6f&0&1", "1-6", "Conference microphone", 0x046d, 0x0a38,
            "Logitech", "USB Microphone", CaptureStateCapturable, "None", attached: 0, ignored: false, password: false);

        AddDevice("usb\\vid_0781&pid_5583\\5&4d5e6f70&0&1", "1-7", "USB 3.0 flash drive", 0x0781, 0x5583,
            "SanDisk", "Ultra Fit", CaptureStateCapturable, "None", attached: 0, ignored: false, password: false);

        AddDevice("usb\\vid_0bda&pid_58e0\\5&5e6f7081&0&1", "1-8", "Internal webcam", 0x0bda, 0x58e0,
            "Realtek", "Integrated Webcam", "CaptureUnsupported", "DriverHoldsDevice", attached: 0, ignored: false, password: false);

        AddDevice("usb\\vid_046d&pid_c31c\\5&6f708192&0&1", "1-9", "Built-in keyboard", 0x046d, 0xc31c,
            "Logitech", "USB Keyboard", "UnsafeToExport", "SystemCritical", attached: 0, ignored: false, password: false);

        AddDevice("usb\\vid_10c4&pid_ea60\\5&708192a3&0&1", "1-10", "USB-to-serial adapter", 0x10c4, 0xea60,
            "Silicon Labs", "CP2102 USB to UART Bridge", CaptureStateCapturable, "None", attached: 0, ignored: false, password: false);

        if (!string.Equals(_scenario, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            AddDevice("usb\\vid_0403&pid_6001\\5&8192a3b4&0&1", "1-11", "Bench instrument cable", 0x0403, 0x6001,
                "FTDI", "FT232R USB UART", "Capturable", "IsoUnsupported", attached: 0, ignored: false, password: false);
        }
    }

    private void SeedPeers()
    {
        AddPeer("workshop-pi", "workshop-pi", "192.168.1.42", 3241, "online", online: true, manual: false, new[]
        {
            Device("1-3", "Industrial camera", 0x046d, 0x0825, "Logitech", "C920 HD Pro", attached: false, auto: false, password: false, status: null),
            Device("1-4", "GPS receiver", 0x091e, 0x0003, "Garmin", "GPS 18x LVC", attached: false, auto: false, password: false, status: null),
        });

        AddPeer("hv-lab", "high-voltage bench", "10.0.0.7", 3241, "online", online: true, manual: false, new[]
        {
            Device("2-2", "Zigbee coordinator", 0x10c4, 0xea60, "Silicon Labs", "CP2102", attached: false, auto: true, password: false, status: null),
        });

        AddPeer("media-server", "media-server", "192.168.1.20", 3240, "offline", online: false, manual: false, new[]
        {
            Device("1-2", "Blu-ray drive", 0x0e8d, 0x1860, "MediaTek", "USB DVD Writer", attached: false, auto: false, password: false, status: "last seen 4 minutes ago"),
        });
    }

    public JsonObject Handle(string line, bool isServer)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return Error("malformed_request", "the request line was not valid JSON.");
        }

        string verb = request["verb"]?.GetValue<string>() ?? string.Empty;
        lock (_gate)
        {
            return verb switch
            {
                "devices.list" when isServer => DevicesList(),
                "devices.kick" => Kick(request),
                "devices.ignore" => SetIgnored(request, true),
                "devices.unignore" => SetIgnored(request, false),
                "devices.nickname" => SetNickname(request),
                "devices.password" => SetPassword(request),
                "server.status" when isServer => ServerStatus(),
                "server.start" => StartStop(request, true),
                "server.stop" => StartStop(request, false),
                "log.tail" => LogTail(request),
                "clients.attach" => Attach(request, true),
                "clients.detach" => Attach(request, false),
                "peers.add" => AddPeerCommand(request),
                "peers.list" when !isServer => PeersList(),
                _ => Error("unknown_verb", "verb '" + verb + "' is not part of the control-plane vocabulary."),
            };
        }
    }

    private JsonObject DevicesList()
    {
        var devices = new JsonArray();
        foreach (JsonObject device in _devices)
        {
            devices.Add(device.DeepClone());
        }

        return Ok("devices.list", new JsonObject { ["devices"] = devices });
    }

    private JsonObject ServerStatus()
    {
        int attached = 0;
        foreach (JsonObject device in _devices)
        {
            attached += device["attachedClients"]?.GetValue<int>() ?? 0;
        }

        return Ok("server.status", new JsonObject
        {
            ["running"] = _running,
            ["serverName"] = "workstation",
            ["plaintextPort"] = 3240,
            ["tlsPort"] = 3241,
            ["exportedDevices"] = _devices.Count,
            ["attachedClients"] = attached,
        });
    }

    private JsonObject StartStop(JsonObject request, bool start)
    {
        string verb = start ? "server.start" : "server.stop";
        if (_running == start)
        {
            return Ok(verb, new JsonObject { ["running"] = _running, ["changed"] = false });
        }

        _running = start;
        Log("server", "info", start ? "Server started; listeners bound" : "Server stopped; listeners closed");
        return Ok(verb, new JsonObject { ["running"] = _running, ["changed"] = true });
    }

    private JsonObject Kick(JsonObject request)
    {
        JsonObject? device = FindDevice(request);
        if (device is null)
        {
            return Error("unknown_device", "no exported device matches the request.");
        }

        int before = device["attachedClients"]?.GetValue<int>() ?? 0;
        device["attachedClients"] = 0;
        Log("server", "info", "Kicked " + before.ToString(CultureInfo.InvariantCulture) +
            " client(s) from " + DeviceLabel(device));
        return Ok("devices.kick", new JsonObject
        {
            ["kicked"] = before,
            ["key"] = device["key"]!.GetValue<string>(),
        });
    }

    private JsonObject SetIgnored(JsonObject request, bool ignored)
    {
        string verb = ignored ? "devices.ignore" : "devices.unignore";
        JsonObject? device = FindDevice(request);
        if (device is null)
        {
            return Error("unknown_device", "no exported device matches the request.");
        }

        device["ignored"] = ignored;
        device["captureState"] = ignored ? "Ignored" : "Capturable";
        device["captureReason"] = ignored ? "Policy" : "None";
        Log("server", "info", (ignored ? "Ignoring " : "No longer ignoring ") + DeviceLabel(device));
        return Ok(verb, new JsonObject { ["ignored"] = ignored });
    }

    private JsonObject SetNickname(JsonObject request)
    {
        JsonObject? device = FindDevice(request);
        if (device is null)
        {
            return Error("unknown_device", "no exported device matches the request.");
        }

        string? nickname = request["nickname"]?.GetValue<string>();
        if (nickname is not null && nickname.Contains(',', StringComparison.Ordinal))
        {
            return Error("invalid_argument", "a nickname cannot contain a comma.");
        }

        device["nickname"] = string.IsNullOrWhiteSpace(nickname) ? null : nickname;
        Log("server", "info", "Nickname for " + device["busid"]!.GetValue<string>() + " is now '" + (nickname ?? string.Empty) + "'");
        return Ok("devices.nickname", new JsonObject { ["nickname"] = device["nickname"]?.DeepClone() });
    }

    private JsonObject SetPassword(JsonObject request)
    {
        JsonObject? device = FindDevice(request);
        if (device is null)
        {
            return Error("unknown_device", "no exported device matches the request.");
        }

        bool clear = request["password"] is null ||
                     request["password"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null;
        string? password = clear ? null : request["password"]!.GetValue<string>();
        if (!clear && string.IsNullOrEmpty(password))
        {
            clear = true;
        }

        device["passwordProtected"] = !clear;
        Log("server", "info", (clear ? "Password removed for " : "Password set for ") + DeviceLabel(device));
        return Ok("devices.password", new JsonObject { ["passwordProtected"] = !clear });
    }

    private JsonObject LogTail(JsonObject request)
    {
        int max = request["max"]?.GetValue<int>() ?? 200;
        var lines = new JsonArray();
        int start = Math.Max(0, _log.Count - max);
        for (int i = start; i < _log.Count; i++)
        {
            lines.Add(_log[i].DeepClone());
        }

        return Ok("log.tail", new JsonObject { ["lines"] = lines });
    }

    private JsonObject Attach(JsonObject request, bool attach)
    {
        string verb = attach ? "clients.attach" : "clients.detach";
        string? peerId = request["peer"]?.GetValue<string>();
        string? busId = request["busid"]?.GetValue<string>();
        JsonObject? peer = FindPeer(peerId);
        if (peer is null)
        {
            return Error("unknown_peer", "no server matches '" + peerId + "'.");
        }

        JsonObject? device = FindPeerDevice(peer, busId);
        if (device is null)
        {
            return Error("unknown_device", "server '" + peerId + "' does not export '" + busId + "'.");
        }

        JsonNode? auto = request["auto"];
        if (auto is not null && auto.GetValueKind() != System.Text.Json.JsonValueKind.Null)
        {
            bool autoValue = auto.GetValue<bool>();
            device["autoAttach"] = autoValue;
            Log("client", "info", (autoValue ? "Recording auto-attach grant for " : "Revoking auto-attach grant for ") +
                PeerLabel(peer) + " / " + DeviceLabel(device));
            return Ok("clients.attach", new JsonObject { ["autoAttach"] = autoValue });
        }

        device["attached"] = attach;
        device["status"] = attach ? "attached as VirtualThere port 2" : null;
        Log("client", "info", (attach ? "Attached " : "Detached ") + DeviceLabel(device) + " from " + PeerLabel(peer));
        return Ok(verb, new JsonObject
        {
            ["attached"] = attach,
            ["port"] = attach ? 2 : null,
        });
    }

    private JsonObject AddPeerCommand(JsonObject request)
    {
        string? host = request["host"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(host))
        {
            return Error("invalid_argument", "a host is required.");
        }

        foreach (JsonObject existing in _peers)
        {
            if (string.Equals(existing["host"]?.GetValue<string>(), host, StringComparison.OrdinalIgnoreCase))
            {
                return Ok("peers.add", new JsonObject { ["id"] = existing["id"]!.DeepClone(), ["added"] = false });
            }
        }

        string? name = request["name"]?.GetValue<string>();
        int port = request["port"] is null || request["port"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null
            ? 3240
            : request["port"]!.GetValue<int>();

        string id = "manual-" + host.Replace('.', '-').Replace(':', '-');
        AddPeer(id, string.IsNullOrWhiteSpace(name) ? host : name, host, port, "online", online: true, manual: true, new[]
        {
            Device("1-1", "Manual server device", 0x1d6b, 0x0104, "Linux Foundation", "Multifunction Composite Gadget",
                attached: false, auto: false, password: false, status: null),
        });
        Log("client", "info", "Server " + host + " added manually");
        return Ok("peers.add", new JsonObject { ["id"] = id, ["added"] = true });
    }

    private JsonObject PeersList()
    {
        var peers = new JsonArray();
        foreach (JsonObject peer in _peers)
        {
            peers.Add(peer.DeepClone());
        }

        return Ok("peers.list", new JsonObject { ["peers"] = peers });
    }

    private JsonObject? FindDevice(JsonObject request)
    {
        string? key = request["key"]?.GetValue<string>();
        if (key is null)
        {
            return null;
        }

        foreach (JsonObject device in _devices)
        {
            if (string.Equals(device["key"]?.GetValue<string>(), key, StringComparison.Ordinal) ||
                string.Equals(device["busid"]?.GetValue<string>(), key, StringComparison.Ordinal))
            {
                return device;
            }
        }

        return null;
    }

    private JsonObject? FindPeer(string? peerId)
    {
        if (peerId is null)
        {
            return null;
        }

        foreach (JsonObject peer in _peers)
        {
            if (string.Equals(peer["id"]?.GetValue<string>(), peerId, StringComparison.Ordinal) ||
                string.Equals(peer["host"]?.GetValue<string>(), peerId, StringComparison.Ordinal))
            {
                return peer;
            }
        }

        return null;
    }

    private static JsonObject? FindPeerDevice(JsonObject peer, string? busId)
    {
        if (peer["devices"] is not JsonArray devices || busId is null)
        {
            return null;
        }

        foreach (JsonNode? node in devices)
        {
            if (node is JsonObject device &&
                string.Equals(device["busid"]?.GetValue<string>(), busId, StringComparison.Ordinal))
            {
                return device;
            }
        }

        return null;
    }

    private void AddDevice(
        string key, string busId, string nickname, int vid, int pid, string vendor, string product,
        string captureState, string captureReason, int attached, bool ignored, bool password)
    {
        _devices.Add(new JsonObject
        {
            ["key"] = key,
            ["busid"] = busId,
            ["nickname"] = nickname,
            ["vid"] = vid,
            ["pid"] = pid,
            ["vendor"] = vendor,
            ["product"] = product,
            ["attachedClients"] = attached,
            ["captureState"] = captureState,
            ["captureReason"] = captureReason,
            ["ignored"] = ignored,
            ["passwordProtected"] = password,
        });
    }

    private void AddPeer(string id, string name, string host, int port, string status, bool online, bool manual, JsonObject[] devices)
    {
        _peers.Add(new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["host"] = host,
            ["port"] = port,
            ["status"] = status,
            ["online"] = online,
            ["manual"] = manual,
            ["devices"] = new JsonArray(devices),
        });
    }

    private static JsonObject Device(
        string busId, string nickname, int vid, int pid, string vendor, string product,
        bool attached, bool auto, bool password, string? status)
    {
        return new JsonObject
        {
            ["busid"] = busId,
            ["nickname"] = nickname,
            ["vid"] = vid,
            ["pid"] = pid,
            ["vendor"] = vendor,
            ["product"] = product,
            ["attached"] = attached,
            ["autoAttach"] = auto,
            ["passwordRequired"] = password,
            ["status"] = status,
        };
    }

    private static string DeviceLabel(JsonObject device) =>
        device["nickname"]?.GetValue<string>() ?? device["busid"]?.GetValue<string>() ?? "device";

    private static string PeerLabel(JsonObject peer) =>
        peer["name"]?.GetValue<string>() ?? peer["id"]?.GetValue<string>() ?? "server";

    private void Log(string source, string level, string message)
    {
        _log.Add(new JsonObject
        {
            ["seq"] = _logSequence++,
            ["timestamp"] = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ["level"] = level,
            ["source"] = source,
            ["message"] = message,
        });
    }

    private static JsonObject Ok(string verb, JsonObject payload)
    {
        JsonObject response = new() { ["ok"] = true, ["verb"] = verb };
        foreach (KeyValuePair<string, JsonNode?> pair in payload)
        {
            response[pair.Key] = pair.Value?.DeepClone();
        }

        return response;
    }

    private static JsonObject Error(string code, string message) => new()
    {
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private const string CaptureStateCapturable = "Capturable";
}
