using System.Security.Cryptography;
using System.Text;
using Vht.Transport.Wire;

namespace Vht.Transport.Auth;

/// <summary>
/// Stored verifier for one secret (a per-device password or the server-wide shared secret).
/// </summary>
/// <remarks>
/// <para>
/// The plaintext secret is never stored and is never transmitted. What is stored is
/// <c>V = PBKDF2-HMAC-SHA256(secret, salt, iterations, 32)</c> (ARCHITECTURE.md §6.1). The client
/// derives the same <c>V</c> locally from the salt and iteration count the server sends in the
/// server hello, and proves knowledge of it with an HMAC over the nonces instead of sending it.
/// </para>
/// <para>
/// <b>Honest property, documented rather than hidden:</b> <c>V</c> is password-equivalent — anyone
/// who reads the store can authenticate. That is inherent to a verifier-based challenge-response
/// where both ends must derive the same key from the same salt; the mitigation is file permissions
/// (see <see cref="Tls.SecureFileStore"/>), the PBKDF2 work factor against the plaintext, and the
/// fact that the exchange only ever happens inside TLS. SRP would remove that property at the cost
/// of a bespoke protocol; it is out of v1 scope and noted in <c>docs/TRANSPORT.md</c>.
/// </para>
/// </remarks>
public sealed class SecretVerifier
{
    /// <summary>Default PBKDF2 iteration count, fixed by ARCHITECTURE.md §6.1.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Per-device salt size, fixed by ARCHITECTURE.md §6.1.</summary>
    public const int SaltSize = HandshakeProtocol.SaltSize;

    /// <summary>Derived-key size (SHA-256 output).</summary>
    public const int VerifierSize = 32;

    private readonly byte[] _verifier;

    /// <summary>Creates a verifier from explicit material (used by the store and by tests).</summary>
    public SecretVerifier(byte[] salt, int iterations, byte[] verifier)
    {
        if (salt.Length != SaltSize)
        {
            throw new ArgumentException($"salt must be {SaltSize} bytes", nameof(salt));
        }

        if (verifier.Length != VerifierSize)
        {
            throw new ArgumentException($"verifier must be {VerifierSize} bytes", nameof(verifier));
        }

        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        Salt = salt;
        Iterations = iterations;
        _verifier = verifier;
    }

    /// <summary>Salt sent to the client in the server hello.</summary>
    public byte[] Salt { get; }

    /// <summary>Iteration count sent to the client in the server hello.</summary>
    public int Iterations { get; }

    /// <summary>Generates a fresh 16-byte salt with the cryptographic RNG.</summary>
    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    /// <summary>Derives a verifier from a secret.</summary>
    public static SecretVerifier Create(string secret, int iterations = DefaultIterations, byte[]? salt = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        salt ??= NewSalt();
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            VerifierSize);
        return new SecretVerifier(salt, iterations, derived);
    }

    /// <summary>
    /// Computes the proof a peer must present: <c>HMAC-SHA256(V, label ‖ clientNonce ‖ serverNonce ‖ method ‖ deviceId)</c>.
    /// </summary>
    /// <remarks>
    /// Both nonces are fresh per attempt and both are covered by the MAC, so a captured proof is
    /// useless on a second connection and cannot be replayed against a different peer.
    /// </remarks>
    public byte[] ComputeProof(
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        AuthMethod method,
        string deviceId)
    {
        if (clientNonce.Length != HandshakeProtocol.NonceSize || serverNonce.Length != HandshakeProtocol.NonceSize)
        {
            throw new ArgumentException("both nonces must be 32 bytes");
        }

        var deviceIdBytes = Encoding.UTF8.GetBytes(deviceId);
        var context = new byte[label.Length + clientNonce.Length + serverNonce.Length + 1 + deviceIdBytes.Length];
        var span = context.AsSpan();
        label.CopyTo(span);
        clientNonce.CopyTo(span[label.Length..]);
        serverNonce.CopyTo(span[(label.Length + clientNonce.Length)..]);
        span[label.Length + clientNonce.Length + serverNonce.Length] = (byte)method;
        deviceIdBytes.CopyTo(span[(label.Length + clientNonce.Length + serverNonce.Length + 1)..]);

        var mac = new HMACSHA256(_verifier);
        try
        {
            return mac.ComputeHash(context);
        }
        finally
        {
            mac.Dispose();
        }
    }

    /// <summary>Constant-time comparison of a presented proof against the expected one.</summary>
    public static bool Verify(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> presented)
    {
        // FixedTimeEquals is constant-time for equal-length inputs; the length check itself is not
        // secret (proof length is fixed by the protocol).
        if (presented.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>
    /// A verifier whose derived key is random, used to answer unknown-device challenges with a
    /// response indistinguishable from a real one (no device-existence oracle).
    /// </summary>
    public static SecretVerifier Dummy(string deviceId)
    {
        // Deterministic per device id so the *salt* stays stable across probes; the key is random
        // and never matches any client's derivation.
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("vht-dummy-salt:" + deviceId))[..SaltSize];
        return new SecretVerifier(salt, DefaultIterations, RandomNumberGenerator.GetBytes(VerifierSize));
    }
}
