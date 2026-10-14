using System.Collections.Concurrent;

namespace Vht.Transport.Auth;

/// <summary>
/// Server-side authentication material, in the vocabulary a config file offers
/// (ARCHITECTURE.md §6.1, Appendix C: <c>server-wide shared secret + per-device password</c>).
/// </summary>
/// <remarks>
/// Plaintext secrets appear here because that is what an operator writes into a config file; they are
/// converted to PBKDF2 verifiers once, lazily, and are not retained by the transport.
/// </remarks>
public sealed record AuthenticationOptions
{
    /// <summary>Server-wide shared secret. Null when the server requires no shared secret.</summary>
    public string? SharedSecret { get; init; }

    /// <summary>Fixed salt for the shared secret; generated on first use when null.</summary>
    public byte[]? SharedSecretSalt { get; init; }

    /// <summary>Per-device passwords, keyed by busid.</summary>
    public IReadOnlyDictionary<string, string> DevicePasswords { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>PBKDF2-HMAC-SHA256 work factor. ARCHITECTURE.md §6.1 fixes 600 000.</summary>
    public int Pbkdf2Iterations { get; init; } = SecretVerifier.DefaultIterations;

    /// <summary>Rate-limiting policy for failed attempts.</summary>
    public RateLimitOptions RateLimit { get; init; } = new();

    /// <summary>True when this configuration can authenticate anyone at all.</summary>
    public bool RequiresAuthentication => SharedSecret is not null || DevicePasswords.Count > 0;

    /// <summary>Validates the material.</summary>
    public void Validate()
    {
        if (SharedSecret is { Length: 0 })
        {
            throw new ArgumentException("shared secret cannot be the empty string", nameof(SharedSecret));
        }

        if (SharedSecretSalt is { Length: var len } && len != SecretVerifier.SaltSize)
        {
            throw new ArgumentException($"shared-secret salt must be {SecretVerifier.SaltSize} bytes", nameof(SharedSecretSalt));
        }

        if (Pbkdf2Iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Pbkdf2Iterations));
        }

        if (DevicePasswords.Count > 4096)
        {
            throw new ArgumentException("refusing to hold more than 4096 per-device passwords", nameof(DevicePasswords));
        }

        foreach (var (deviceId, password) in DevicePasswords)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                throw new ArgumentException("device password keys must be non-empty busids", nameof(DevicePasswords));
            }

            if (password is { Length: 0 })
            {
                throw new ArgumentException($"device password for '{deviceId}' cannot be the empty string", nameof(DevicePasswords));
            }
        }

        RateLimit.Validate();
    }
}

/// <summary>
/// Resolves the verifier to challenge with, for either authentication method.
/// </summary>
/// <remarks>
/// <para>
/// Verifiers are derived once per secret and cached, because PBKDF2 at 600 000 iterations is
/// deliberately expensive (~hundreds of milliseconds once).
/// </para>
/// <para>
/// An unknown device id is answered with a <b>dummy verifier</b> rather than an error: the challenge
/// it produces is byte-indistinguishable from a real one, so an unauthenticated peer cannot use the
/// server as an oracle for which busids have passwords.
/// </para>
/// </remarks>
public sealed class VerifierSet
{
    private readonly AuthenticationOptions _options;
    private readonly ConcurrentDictionary<string, SecretVerifier> _deviceVerifiers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SecretVerifier> _dummyVerifiers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<SecretVerifier> _sharedVerifier;

    /// <summary>Creates the set from server configuration.</summary>
    public VerifierSet(AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _sharedVerifier = new Lazy<SecretVerifier>(
            () => SecretVerifier.Create(
                options.SharedSecret!,
                options.Pbkdf2Iterations,
                options.SharedSecretSalt),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>PBKDF2 iteration count advertised to clients.</summary>
    public int Iterations => _options.Pbkdf2Iterations;

    /// <summary>True when any authentication method is configured.</summary>
    public bool RequiresAuthentication => _options.RequiresAuthentication;

    /// <summary>True when this device id has a real password.</summary>
    public bool IsKnownDevice(string? deviceId) =>
        deviceId is not null && _options.DevicePasswords.ContainsKey(deviceId);

    /// <summary>True when this (method, device) pair can actually authenticate somebody.</summary>
    public bool IsKnown(AuthMethod method, string? deviceId) => method switch
    {
        AuthMethod.SharedSecret => _options.SharedSecret is not null,
        AuthMethod.DevicePassword => IsKnownDevice(deviceId),
        AuthMethod.None => true,
        _ => false,
    };

    /// <summary>
    /// The method the server will demand, given what the client asked for. A client that asks for
    /// nothing gets the server's strongest configured method rather than a free pass.
    /// </summary>
    public AuthMethod ResolveRequiredMethod(AuthMethod requested)
    {
        if (requested == AuthMethod.DevicePassword)
        {
            return AuthMethod.DevicePassword;
        }

        if (requested == AuthMethod.SharedSecret)
        {
            return AuthMethod.SharedSecret;
        }

        if (_options.SharedSecret is not null)
        {
            return AuthMethod.SharedSecret;
        }

        if (_options.DevicePasswords.Count > 0)
        {
            return AuthMethod.DevicePassword;
        }

        return AuthMethod.None;
    }

    /// <summary>The verifier to challenge with: the real one when it exists, a dummy otherwise.</summary>
    public SecretVerifier GetChallengeVerifier(AuthMethod method, string? deviceId)
    {
        switch (method)
        {
            case AuthMethod.SharedSecret when _options.SharedSecret is not null:
                return _sharedVerifier.Value;

            case AuthMethod.DevicePassword when deviceId is not null
                && _options.DevicePasswords.TryGetValue(deviceId, out var password):
                return _deviceVerifiers.GetOrAdd(
                    deviceId,
                    _ => SecretVerifier.Create(password, _options.Pbkdf2Iterations));

            default:
                // Unknown device, or a method that has no configured secret: answer with an
                // indistinguishable challenge and fail the proof.
                return _dummyVerifiers.GetOrAdd(
                    $"{(byte)method}:{deviceId ?? string.Empty}",
                    key => SecretVerifier.Dummy(key));
        }
    }
}
