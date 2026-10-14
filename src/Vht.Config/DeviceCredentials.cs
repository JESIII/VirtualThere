using System.Security.Cryptography;
using System.Text;
using Vht.Config.DeviceIdentity;
using Vht.Config.Policy;

namespace Vht.Config;

/// <summary>
/// Friendly names per device (the reference product's <c>DeviceNicknames</c> store). Lookup order, most specific
/// first: exact device key -&gt; vid:pid:serial -&gt; vid:pid -&gt; product string. The reference product
/// stores this as a comma-delimited blob with an integrity check and a "nickname cannot contain a
/// comma" rule; TOML tables have no such delimiter, so nicknames may contain any character.
/// </summary>
public sealed class NicknameStore
{
    private readonly Dictionary<string, string> _entries;

    public NicknameStore(IReadOnlyDictionary<string, string> entries)
    {
        _entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            _entries[NormalizeKeyOrDefault(key)] = value;
        }
    }

    public static NicknameStore Empty { get; } = new(new Dictionary<string, string>());

    public int Count => _entries.Count;

    public IReadOnlyDictionary<string, string> Entries => _entries;

    /// <summary>Set or replace a nickname for a selector or device key. Throws on an unparsable key
    /// so a typo is reported at the call site instead of silently never matching.</summary>
    public NicknameStore With(string selectorOrKey, string nickname)
    {
        var key = NormalizeKey(selectorOrKey);
        var copy = new Dictionary<string, string>(_entries, StringComparer.OrdinalIgnoreCase) { [key] = nickname };
        return new NicknameStore(copy);
    }

    public NicknameStore Without(string selectorOrKey)
    {
        var copy = new Dictionary<string, string>(_entries, StringComparer.OrdinalIgnoreCase);
        copy.Remove(NormalizeKey(selectorOrKey));
        return new NicknameStore(copy);
    }

    public string? Resolve(DeviceDescriptor descriptor, string deviceKey)
    {
        // The same device can be named by its bare key ("s-1050-...") or by the explicit selector form
        // ("key:s-1050-..."); both normalise to different dictionary keys, so both are consulted, most
        // explicit first.
        if (_entries.TryGetValue(deviceKey, out var byKey))
        {
            return byKey;
        }

        if (_entries.TryGetValue($"key:{deviceKey}", out var byExplicitKey))
        {
            return byExplicitKey;
        }

        if (descriptor.UsableSerial is { } serial
            && _entries.TryGetValue($"{descriptor.VendorId:x4}:{descriptor.ProductId:x4}:{serial}", out var bySerial))
        {
            return bySerial;
        }

        if (_entries.TryGetValue(descriptor.VidPid, out var byVidPid))
        {
            return byVidPid;
        }

        if (descriptor.Product is { Length: > 0 } product
            && _entries.TryGetValue($"product:{product}", out var byProduct))
        {
            return byProduct;
        }

        return null;
    }

    /// <summary>What the UI shows when no nickname is configured. Never returns null.</summary>
    public string DisplayName(DeviceDescriptor descriptor, string deviceKey)
    {
        var nickname = Resolve(descriptor, deviceKey);
        if (nickname is not null)
        {
            return nickname;
        }

        if (descriptor.Product is { Length: > 0 } product)
        {
            return product;
        }

        return $"{deviceKey} ({descriptor.VidPid})";
    }

    public static string NormalizeKey(string selectorOrKey)
    {
        if (TryNormalizeKey(selectorOrKey, out var normalized, out var error))
        {
            return normalized;
        }

        throw new ArgumentException(error, nameof(selectorOrKey));
    }

    /// <summary>Normalises a nickname/password key, or reports why it cannot be used. Config loading
    /// uses this so that a bad key is a diagnostic (with a line number) rather than an exception;
    /// runtime callers use <see cref="NormalizeKey"/> so a typo is caught at the call site.</summary>
    public static bool TryNormalizeKey(string? selectorOrKey, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(selectorOrKey))
        {
            error = "a device key must not be empty";
            return false;
        }

        var trimmed = selectorOrKey.Trim();
        if (DeviceSelector.TryParse(trimmed, out var selector, out var selectorError))
        {
            normalized = selector.Kind == DeviceSelectorKind.Key ? $"key:{selector.DeviceKey}" : selector.Raw.ToLowerInvariant();
            return true;
        }

        // device keys (s-.../f-.../t-...) do not parse as selectors; accept them verbatim.
        if (trimmed.StartsWith("s-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("f-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("t-", StringComparison.OrdinalIgnoreCase))
        {
            normalized = trimmed.ToLowerInvariant();
            return true;
        }

        // product:<name> aliases are accepted (resolved against the USB product string).
        if (trimmed.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = trimmed.ToLowerInvariant();
            return true;
        }

        error = $"'{selectorOrKey}' is neither a device selector nor a device key: {selectorError}";
        return false;
    }

    private static string NormalizeKeyOrDefault(string key) =>
        TryNormalizeKey(key, out var normalized, out _) ? normalized : key.Trim().ToLowerInvariant();
}

/// <summary>How a stored password is encoded. Everything except <see cref="Plaintext"/> is a verifier.</summary>
public enum PasswordEncoding
{
    Plaintext,
    Sha256,
    Pbkdf2,
}

public sealed record DevicePassword(string Selector, PasswordEncoding Encoding, string Value, int Iterations = 0, string? Salt = null)
{
    /// <summary>Accepts <c>plaintext</c>, <c>sha256:&lt;64 hex&gt;</c> or
    /// <c>pbkdf2:&lt;iterations&gt;:&lt;salt-b64&gt;:&lt;hash-b64&gt;</c>. Returns false with a reason for
    /// anything else, so a malformed entry is a config error rather than a silent lockout.</summary>
    public static bool TryParse(string selector, string raw, out DevicePassword password, out string? error)
    {
        password = null!;
        error = null;
        if (raw.Length == 0)
        {
            error = "password is empty";
            return false;
        }

        if (raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = raw[7..].Trim();
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            {
                error = "sha256: password must be followed by exactly 64 hexadecimal characters (the hash of the password, UTF-8, no salt)";
                return false;
            }

            password = new DevicePassword(selector, PasswordEncoding.Sha256, hex.ToLowerInvariant());
            return true;
        }

        if (raw.StartsWith("pbkdf2:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':');
            if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            {
                error = "pbkdf2: password must be pbkdf2:<iterations>:<salt-base64>:<hash-base64>";
                return false;
            }

            try
            {
                Convert.FromBase64String(parts[2]);
                Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                error = "pbkdf2: salt and hash must be base64";
                return false;
            }

            password = new DevicePassword(selector, PasswordEncoding.Pbkdf2, parts[3], iterations, parts[2]);
            return true;
        }

        password = new DevicePassword(selector, PasswordEncoding.Plaintext, raw);
        return true;
    }

    public bool Verify(string candidate)
    {
        return Encoding switch
        {
            // System.Text.Encoding is spelled out because the record property named Encoding
            // (the PasswordEncoding) hides the System.Text.Encoding type inside this record.
            PasswordEncoding.Plaintext => CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(Value),
                System.Text.Encoding.UTF8.GetBytes(candidate)),
            PasswordEncoding.Sha256 => CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(Value),
                System.Text.Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidate))))),
            PasswordEncoding.Pbkdf2 => VerifyPbkdf2(candidate),
            _ => false,
        };
    }

    private bool VerifyPbkdf2(string candidate)
    {
        var salt = Convert.FromBase64String(Salt!);
        var expected = Convert.FromBase64String(Value);
        var actual = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(candidate), salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static DevicePassword CreatePbkdf2(string selector, string secret, int iterations = 100_000, int saltBytes = 16)
    {
        var salt = RandomNumberGenerator.GetBytes(saltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, 32);
        return new DevicePassword(selector, PasswordEncoding.Pbkdf2, Convert.ToBase64String(hash), iterations, Convert.ToBase64String(salt));
    }

    public string Serialize() => Encoding switch
    {
        PasswordEncoding.Plaintext => Value,
        PasswordEncoding.Sha256 => "sha256:" + Value,
        PasswordEncoding.Pbkdf2 => $"pbkdf2:{Iterations}:{Salt}:{Value}",
        _ => Value,
    };
}

/// <summary>
/// Per-device passwords (the reference product's <c>password</c> key plus the client's "Remember" flow).
/// A device with an entry here answers <c>BIND_PASSWORD_REQUIRED</c> until the client presents the
/// secret. The password-protected set is exactly what <see cref="HasPassword"/> reports for, and the
/// server never serves those devices on the plaintext listener.
/// </summary>
public sealed class DevicePasswordStore
{
    private readonly Dictionary<string, DevicePassword> _entries;

    public DevicePasswordStore(IReadOnlyDictionary<string, DevicePassword> entries)
    {
        _entries = new Dictionary<string, DevicePassword>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            _entries[NicknameStore.TryNormalizeKey(key, out var normalized, out _) ? normalized : key.Trim().ToLowerInvariant()] = value;
        }
    }

    public static DevicePasswordStore Empty { get; } = new(new Dictionary<string, DevicePassword>());

    public int Count => _entries.Count;

    public IReadOnlyDictionary<string, DevicePassword> Entries => _entries;

    public bool IsEmpty => _entries.Count == 0;

    public DevicePasswordStore With(DevicePassword password)
    {
        var copy = new Dictionary<string, DevicePassword>(_entries, StringComparer.OrdinalIgnoreCase)
        {
            [NicknameStore.NormalizeKey(password.Selector)] = password,
        };
        return new DevicePasswordStore(copy);
    }

    public DevicePasswordStore Without(string selectorOrKey)
    {
        var copy = new Dictionary<string, DevicePassword>(_entries, StringComparer.OrdinalIgnoreCase);
        copy.Remove(NicknameStore.NormalizeKey(selectorOrKey));
        return new DevicePasswordStore(copy);
    }

    /// <summary>Same lookup order as nicknames: exact key, then vid:pid:serial, then vid:pid.</summary>
    public DevicePassword? Find(DeviceDescriptor descriptor, string deviceKey)
    {
        if (_entries.TryGetValue(deviceKey, out var byKey))
        {
            return byKey;
        }

        if (_entries.TryGetValue($"key:{deviceKey}", out var byExplicitKey))
        {
            return byExplicitKey;
        }

        if (descriptor.UsableSerial is { } serial
            && _entries.TryGetValue($"{descriptor.VendorId:x4}:{descriptor.ProductId:x4}:{serial}", out var bySerial))
        {
            return bySerial;
        }

        return _entries.TryGetValue(descriptor.VidPid, out var byVidPid) ? byVidPid : null;
    }

    public bool HasPassword(DeviceDescriptor descriptor, string deviceKey) => Find(descriptor, deviceKey) is not null;

    /// <summary>Verify a client-supplied secret. Returns false for an unknown device as well as a wrong
    /// password: the caller must not be able to distinguish "no password set" from "wrong password" on
    /// the wire.</summary>
    public bool Verify(DeviceDescriptor descriptor, string deviceKey, string? candidate)
    {
        var password = Find(descriptor, deviceKey);
        if (password is null)
        {
            return false;
        }

        return candidate is not null && password.Verify(candidate);
    }
}