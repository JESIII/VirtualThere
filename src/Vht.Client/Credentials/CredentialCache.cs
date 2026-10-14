// SPDX-License-Identifier: Apache-2.0
//
// Per-device passwords (deliverable 7). ARCHITECTURE.md §6.1: the password participates in the auth
// handshake on the TLS listener; §6.4: a password-protected export is never served on the plaintext
// listener. So this class does not "send a password" — it holds the credential, decides whether one
// is needed, and hands it to the transport's AuthenticateAsync, which is where the handshake lives.
//
// What IS persisted is only the *remember choice* (and the device key it applies to), never the
// secret: a device password written in plaintext to disk in a user profile is a worse problem than
// retyping it. §6.1 says the verifier is stored server-side and the password is never stored; the
// client respects the same rule. When Vht.Config lands, the remember-flags move into its store
// (`credentials.json` here, `[credentials]` there) — the interface does not change.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vht.Client.Bridge;
using Vht.Client.Diagnostics;

namespace Vht.Client.Credentials;

/// <summary>Supplies device passwords. The UI card (ui-workstream) provides the prompt; the CLI provides flags.</summary>
internal interface ICredentialProvider
{
    /// <summary>Returns the password for a device key, if one is known for this process.</summary>
    string? GetPassword(string deviceKey);

    /// <summary>Installs a password for this process, and records the remember choice if asked.</summary>
    void Remember(string deviceKey, string password, bool persistChoice);

    /// <summary>Forgets a password (and the remember choice when <paramref name="forgetChoice"/>).</summary>
    void Forget(string deviceKey, bool forgetChoice = true);

    /// <summary>True when the user previously asked to remember this device's credential.</summary>
    bool IsRemembered(string deviceKey);
}

/// <summary>
/// In-memory secret store with a persisted remember-choice. Thread-safe; secrets are never logged.
/// </summary>
internal sealed class CredentialCache : ICredentialProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _remembered = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly ClientLog _log;

    internal CredentialCache(string path, ClientLog log)
    {
        _path = path;
        _log = log;
        Load();
    }

    public string? GetPassword(string deviceKey)
    {
        lock (_sync)
        {
            return _secrets.TryGetValue(deviceKey, out string? password) ? password : null;
        }
    }

    public void Remember(string deviceKey, string password, bool persistChoice)
    {
        lock (_sync)
        {
            _secrets[deviceKey] = password;
            if (persistChoice && _remembered.Add(deviceKey))
            {
                Save();
            }
        }
        _log.Info("credentials", $"credential set for {deviceKey} (remember={persistChoice})");
    }

    public void Forget(string deviceKey, bool forgetChoice = true)
    {
        lock (_sync)
        {
            _secrets.Remove(deviceKey);
            if (forgetChoice && _remembered.Remove(deviceKey))
            {
                Save();
            }
        }
    }

    public bool IsRemembered(string deviceKey)
    {
        lock (_sync) return _remembered.Contains(deviceKey);
    }

    /// <summary>Device keys the user has asked to remember. Never contains secrets.</summary>
    internal IReadOnlyList<string> RememberedKeys
    {
        get
        {
            lock (_sync) return _remembered.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            RememberedState? state = JsonSerializer.Deserialize<RememberedState>(File.ReadAllText(_path), JsonOptions);
            if (state?.Keys is null) return;
            foreach (string key in state.Keys)
            {
                _remembered.Add(key);
            }
            _log.Debug("credentials", $"loaded {_remembered.Count} remember-choice(s) from {_path}");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Error("credentials", $"could not read '{_path}'", ex);
        }
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(_path, JsonSerializer.Serialize(new RememberedState
            {
                Keys = _remembered.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            }, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("credentials", $"could not write '{_path}'", ex);
        }
    }

    private sealed record RememberedState
    {
        public List<string>? Keys { get; init; }
    }
}

/// <summary>
/// The attach handshake the bridge runs on the server leg before replaying the driver's import.
/// Lives here (rather than inside the bridge) so the bridge stays a byte relay with one policy hook.
/// </summary>
internal interface IAttachHandshake
{
    Task<Transport.ClientAuthResult> AuthenticateAsync(
        Peer peer,
        string remoteBusId,
        Transport.IClientTransport transport,
        CancellationToken ct);
}

/// <summary>
/// Default handshake: nothing to do for a transport with no auth capabilities (the plaintext stub),
/// refuse anything that needs a credential we have no way to supply. Deliberately NOT a no-op — App. C
/// requires the plaintext path to fail closed rather than silently accept.
/// </summary>
internal sealed class CredentialHandshake : IAttachHandshake
{
    private readonly ICredentialProvider _credentials;
    private readonly ClientLog _log;

    internal CredentialHandshake(ICredentialProvider credentials, ClientLog log)
    {
        _credentials = credentials;
        _log = log;
    }

    public async Task<Transport.ClientAuthResult> AuthenticateAsync(
        Peer peer,
        string remoteBusId,
        Transport.IClientTransport transport,
        CancellationToken ct)
    {
        var capabilities = transport.Capabilities;

        if (capabilities == Transport.TransportCapabilities.None)
        {
            // Plaintext: no auth is possible at all (§6.3/§6.4). The password-protected case is
            // rejected by the peer (or, when we know the device needs one, below).
            return Transport.ClientAuthResult.Ok();
        }

        if (!capabilities.HasFlag(Transport.TransportCapabilities.HandshakeRequired))
        {
            return Transport.ClientAuthResult.Failed(
                $"transport {transport.Kind} does not require the extended handshake but declares auth " +
                "capabilities; refusing rather than guessing what to send.");
        }

        string deviceKey = $"{peer.Name}/{peer.Host}:{peer.Port}/{remoteBusId}";
        string? password = capabilities.HasFlag(Transport.TransportCapabilities.DevicePassword)
            ? _credentials.GetPassword(deviceKey)
            : null;

        if (capabilities.HasFlag(Transport.TransportCapabilities.DevicePassword) && password is null)
        {
            _log.Info("credentials", $"device {deviceKey} needs a password and none is available yet");
        }

        var request = new Transport.ClientAuthRequest
        {
            BusId = remoteBusId,
            DevicePassword = password,
            ExpectedSpkiPin = peer.SpkiPin,
        };

        Transport.ClientAuthResult result = await transport.AuthenticateAsync(request, ct).ConfigureAwait(false);
        if (result.Success && result.ObservedSpkiPin is not null && peer.SpkiPin is null)
        {
            // Trust-on-first-use: the pin decision is the caller's to persist (§6.1); we only report it.
            _log.Info("credentials", $"peer '{peer.Name}' presented SPKI {result.ObservedSpkiPin}; " +
                                     "no pin is recorded yet (trust on first use)");
        }

        return result;
    }
}
