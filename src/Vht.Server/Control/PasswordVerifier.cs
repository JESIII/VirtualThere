using System.Security.Cryptography;
using System.Text;

namespace Vht.Server.Control;

/// <summary>
/// Derives and stores the per-device password verifier.
/// </summary>
/// <remarks>
/// <para>
/// The transport card owns the challenge-response that <i>uses</i> this verifier
/// (ARCHITECTURE.md §6.2: PBKDF2 + HMAC over a server challenge). Defining the storage format
/// here means the control plane never holds a plaintext password after the verb returns, and
/// the CLI and the UI can set one today.
/// </para>
/// <para>
/// Format: <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c> — self-describing,
/// so the iteration count can be raised later without invalidating existing verifiers.
/// </para>
/// </remarks>
public static class PasswordVerifier
{
    /// <summary>PBKDF2 work factor. Deliberately high: this is a per-device GUI secret, not a KDF for bulk data.</summary>
    public const int DefaultIterations = 210_000;

    /// <summary>Salt length in bytes.</summary>
    public const int SaltLength = 16;

    /// <summary>Derived-key length in bytes.</summary>
    public const int HashLength = 32;

    /// <summary>Creates a verifier string for <paramref name="password"/>.</summary>
    public static string Create(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashLength);

        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>True when <paramref name="password"/> matches <paramref name="verifier"/>.</summary>
    public static bool Verify(string password, string verifier)
    {
        if (!TryParse(verifier, out int iterations, out byte[] salt, out byte[] expected))
            return false;

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? string.Empty), salt, iterations,
            HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True when a stored value is a verifier this build can check.</summary>
    public static bool IsVerifier(string? verifier) =>
        TryParse(verifier ?? string.Empty, out _, out _, out _);

    private static bool TryParse(string verifier, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();

        string[] parts = verifier.Split('$');
        if (parts.Length != 4) return false;
        if (!string.Equals(parts[0], "pbkdf2-sha256", StringComparison.Ordinal)) return false;
        if (!int.TryParse(parts[1], out iterations) || iterations < 1) return false;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && hash.Length > 0;
    }
}
