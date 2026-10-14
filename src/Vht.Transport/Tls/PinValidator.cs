using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Vht.Transport.Tls;

/// <summary>Outcome of evaluating a peer certificate against the pin policy.</summary>
public sealed record PinDecision(
    bool Accepted,
    string SpkiPin,
    bool NewlyRecorded,
    string? Reason);

/// <summary>
/// Trust decisions for the client side of a TLS session, in the absence of a CA chain
/// (ARCHITECTURE.md §6.1, §6.5).
/// </summary>
/// <remarks>
/// <para>
/// The identity that is pinned is the SPKI SHA-256 (RFC 7469-style), not the whole certificate, so a
/// server may renew its certificate without breaking clients as long as the key pair is unchanged —
/// and a server that presents a <em>different key</em> is refused even if its certificate looks valid.
/// </para>
/// <para>
/// A missing <see cref="IPinStore"/> is a configuration error in
/// <see cref="PinMode.TrustOnFirstUse"/> and <see cref="PinMode.Strict"/>, not a licence to accept
/// anything: silently degrading to "trust everyone" is exactly the failure this layer exists to
/// prevent.
/// </para>
/// </remarks>
public static class PinValidator
{
    /// <summary>Evaluates <paramref name="certificate"/> for <paramref name="peerIdentity"/> (e.g. <c>host:port</c>).</summary>
    public static PinDecision Evaluate(
        PinMode mode,
        Auth.IPinStore? store,
        string peerIdentity,
        X509Certificate2? certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerIdentity);

        if (certificate is null)
        {
            return new PinDecision(false, string.Empty, false, "server presented no certificate");
        }

        if (certificate.NotAfter < DateTimeOffset.UtcNow || certificate.NotBefore > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return new PinDecision(
                false,
                ServerCertificateManager.ComputeSpkiPin(certificate),
                false,
                $"server certificate is not valid at the current time ({certificate.NotBefore:u}..{certificate.NotAfter:u})");
        }

        var pin = ServerCertificateManager.ComputeSpkiPin(certificate);

        if (mode == PinMode.Insecure)
        {
            return new PinDecision(true, pin, false, "pinning disabled (loopback/testing only)");
        }

        if (store is null)
        {
            return new PinDecision(false, pin, false, "no pin store is configured, so the server key cannot be remembered or checked");
        }

        if (store is Auth.IPinStoreHealth { Healthy: false })
        {
            return new PinDecision(false, pin, false, "the pin store could not be read; refusing to treat a known peer as unknown");
        }

        if (store.TryGetPin(peerIdentity, out var recorded))
        {
            if (PinsEqual(recorded, pin))
            {
                return new PinDecision(true, pin, false, null);
            }

            return new PinDecision(
                false,
                pin,
                false,
                $"pinned key mismatch for {peerIdentity}: expected SPKI {recorded}, server presented {pin}. " +
                "Either the server's key changed or this is an impersonation attempt; refusing the session.");
        }

        if (mode == PinMode.Strict)
        {
            return new PinDecision(
                false,
                pin,
                false,
                $"no pin recorded for {peerIdentity} and pinning mode is Strict; refusing an unpinned peer");
        }

        store.StorePin(peerIdentity, pin);
        return new PinDecision(true, pin, true, null);
    }

    /// <summary>Constant-time comparison of two base64url pins, tolerant only of padding differences.</summary>
    public static bool PinsEqual(string left, string right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var a = Encoding.ASCII.GetBytes(left.TrimEnd('='));
        var b = Encoding.ASCII.GetBytes(right.TrimEnd('='));
        if (a.Length != b.Length)
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
