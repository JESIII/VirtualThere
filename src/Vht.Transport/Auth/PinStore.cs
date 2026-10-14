using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vht.Transport.Auth;

/// <summary>Where a client records the SPKI pin it has seen for a peer (trust-on-first-use).</summary>
public interface IPinStore
{
    /// <summary>Returns the recorded pin for a peer identity such as <c>host:port</c>.</summary>
    bool TryGetPin(string peerIdentity, out string spkiPin);

    /// <summary>Records (or replaces) the pin for a peer identity.</summary>
    void StorePin(string peerIdentity, string spkiPin);

    /// <summary>Removes a pin, e.g. after a user explicitly re-consents to a changed server key.</summary>
    bool RemovePin(string peerIdentity);
}

/// <summary>Non-persistent pin store, for tests and for a client that has no profile directory.</summary>
public sealed class InMemoryPinStore : IPinStore
{
    private readonly Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty store.</summary>
    public InMemoryPinStore()
    {
    }

    /// <summary>Creates a store seeded with pins.</summary>
    public InMemoryPinStore(IEnumerable<KeyValuePair<string, string>> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        foreach (var (key, value) in pins)
        {
            _pins[key] = value;
        }
    }

    /// <inheritdoc />
    public bool TryGetPin(string peerIdentity, out string spkiPin)
    {
        ArgumentNullException.ThrowIfNull(peerIdentity);
        lock (_pins)
        {
            return _pins.TryGetValue(peerIdentity, out spkiPin!);
        }
    }

    /// <inheritdoc />
    public void StorePin(string peerIdentity, string spkiPin)
    {
        ArgumentNullException.ThrowIfNull(peerIdentity);
        ArgumentNullException.ThrowIfNull(spkiPin);
        lock (_pins)
        {
            _pins[peerIdentity] = spkiPin;
        }
    }

    /// <inheritdoc />
    public bool RemovePin(string peerIdentity)
    {
        ArgumentNullException.ThrowIfNull(peerIdentity);
        lock (_pins)
        {
            return _pins.Remove(peerIdentity);
        }
    }
}

/// <summary>File-backed pin store: a small JSON document, written atomically.</summary>
/// <remarks>
/// The document is deliberately human-readable — an operator must be able to see which servers a
/// client trusts, and to delete a pin when a server's key legitimately changed.
/// </remarks>
public sealed class JsonPinStore : IPinStore, IPinStoreHealth
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, PinEntry> _peers = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    /// <summary>Creates a store backed by <paramref name="path"/>; the file is created on first write.</summary>
    public JsonPinStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Default location: <c>%LOCALAPPDATA%\VirtualThere\known_peers.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualThere",
        "known_peers.json");

    /// <inheritdoc />
    public bool TryGetPin(string peerIdentity, out string spkiPin)
    {
        ArgumentNullException.ThrowIfNull(peerIdentity);
        lock (_gate)
        {
            EnsureLoaded();
            if (_peers.TryGetValue(peerIdentity, out var entry))
            {
                spkiPin = entry.Spki;
                return true;
            }
        }

        spkiPin = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public void StorePin(string peerIdentity, string spkiPin)
    {
        ArgumentNullException.ThrowIfNull(peerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(spkiPin);
        lock (_gate)
        {
            EnsureLoaded();
            _peers[peerIdentity] = new PinEntry { Spki = spkiPin, FirstSeen = DateTimeOffset.UtcNow.ToString("O") };
            Save();
        }
    }

    /// <inheritdoc />
    public bool RemovePin(string peerIdentity)
    {
        ArgumentNullException.ThrowIfNull(peerIdentity);
        lock (_gate)
        {
            EnsureLoaded();
            if (_peers.Remove(peerIdentity))
            {
                Save();
                return true;
            }
        }

        return false;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<PinDocument>(File.ReadAllText(_path), SerializerOptions);
            if (document?.Peers is null)
            {
                return;
            }

            foreach (var (key, value) in document.Peers)
            {
                if (!string.IsNullOrWhiteSpace(value.Spki))
                {
                    _peers[key] = value;
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt pin store must not silently become "no pins": treating it as empty would
            // downgrade every Strict peer to unpinned. Keep it empty but record the condition.
            _corrupt = true;
        }
        catch (IOException)
        {
            _corrupt = true;
        }
    }

    /// <summary>
    /// True when the existing file could not be parsed. A Strict-mode client refuses to connect in
    /// this state rather than treating every peer as unknown.
    /// </summary>
    public bool Corrupt
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _corrupt;
            }
        }
    }

    /// <inheritdoc />
    public bool Healthy => !Corrupt;

    private bool _corrupt;

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new PinDocument { Peers = new Dictionary<string, PinEntry>(_peers, StringComparer.OrdinalIgnoreCase) };
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed class PinDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("peers")]
        public Dictionary<string, PinEntry>? Peers { get; set; }
    }

    private sealed class PinEntry
    {
        [JsonPropertyName("spki")]
        public string Spki { get; set; } = string.Empty;

        [JsonPropertyName("firstSeen")]
        public string? FirstSeen { get; set; }
    }
}
