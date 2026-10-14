using System.Security.Cryptography.X509Certificates;
using System.Text;
using Vht.Transport.Auth;
using Vht.Transport.Tls;
using Vht.Transport.Wire;
using Xunit;

namespace Vht.Transport.Tests;

public sealed class Crc32CTests
{
    [Fact]
    public void MatchesThePublishedTestVector()
    {
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
    }

    [Fact]
    public void EmptyInputIsZero()
    {
        Assert.Equal(0u, Crc32C.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void TwoSegmentOverloadMatchesConcatenation()
    {
        var a = Encoding.ASCII.GetBytes("hello ");
        var b = Encoding.ASCII.GetBytes("world");
        var both = Encoding.ASCII.GetBytes("hello world");
        Assert.Equal(Crc32C.Compute(both), Crc32C.Compute(a, b));
    }

    [Fact]
    public void SliceByEightMatchesAByteAtATimeReference()
    {
        // The table-driven slice-by-8 path processes eight bytes per iteration and falls back to one
        // byte per iteration for the tail, so every residue length 0..16 has to be exercised, and the
        // two-segment overload has to chain the running CRC rather than restart it.
        var random = new Random(20261014);
        for (var length = 0; length <= 16; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);
            Assert.Equal(Reference(data), Crc32C.Compute(data));
        }

        for (var trial = 0; trial < 64; trial++)
        {
            var split = random.Next(0, 257);
            var total = split + random.Next(0, 257);
            var data = new byte[total];
            random.NextBytes(data);

            Assert.Equal(Reference(data), Crc32C.Compute(data));
            Assert.Equal(Reference(data), Crc32C.Compute(data.AsSpan(0, split), data.AsSpan(split)));
        }

        // A one-megabyte buffer, i.e. the frame size the transport actually uses.
        var megabyte = new byte[1024 * 1024];
        random.NextBytes(megabyte);
        Assert.Equal(Reference(megabyte), Crc32C.Compute(megabyte));
    }

    /// <summary>The byte-at-a-time definition, independent of the production tables.</summary>
    private static uint Reference(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0x82F63B78u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}

public sealed class HandshakeFrameTests
{
    [Fact]
    public void ClientHelloRoundTrips()
    {
        var hello = new HandshakeProtocol.ClientHello
        {
            Capabilities = TransportCapabilities.HandshakeRequired | TransportCapabilities.PeerPinning,
            RequestedFeatures = FeatureFlags.Lz4 | FeatureFlags.Isochronous,
            Method = AuthMethod.DevicePassword,
            Name = "workstation",
            DeviceId = "1-4.2",
            Nonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
        };

        var decoded = HandshakeProtocol.ClientHello.Decode(hello.Encode());

        Assert.Equal(hello.Name, decoded.Name);
        Assert.Equal(hello.DeviceId, decoded.DeviceId);
        Assert.Equal(hello.Method, decoded.Method);
        Assert.Equal(hello.RequestedFeatures, decoded.RequestedFeatures);
        Assert.Equal(hello.Nonce, decoded.Nonce);
        Assert.Equal(hello.EncodedLength, hello.Encode().Length);
    }

    [Fact]
    public async Task ClientHelloRoundTripsOverAStream()
    {
        var hello = new HandshakeProtocol.ClientHello
        {
            Method = AuthMethod.SharedSecret,
            Name = "client",
            DeviceId = "1-1",
            Nonce = new byte[32],
        };

        using var stream = new MemoryStream();
        await HandshakeProtocol.WriteClientHelloAsync(stream, hello, CancellationToken.None);
        stream.Position = 0;
        var decoded = await HandshakeProtocol.ReadClientHelloAsync(stream, CancellationToken.None);

        Assert.Equal(hello.Name, decoded.Name);
        Assert.Equal(hello.DeviceId, decoded.DeviceId);
        Assert.Equal(hello.Method, decoded.Method);
    }

    [Fact]
    public async Task StockUsbipBytesAreRejectedAsAHandshakeSkip()
    {
        // OP_REQ_DEVLIST 0x8005 in big-endian, plus a second frame's worth of bytes.
        var usbip = new byte[] { 0x80, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        using var stream = new MemoryStream(usbip);

        var ex = await Assert.ThrowsAsync<TransportException>(
            async () => await HandshakeProtocol.ReadClientHelloAsync(stream, CancellationToken.None));

        Assert.Equal(TransportErrorCode.HandshakeRequired, ex.Code);
    }

    [Fact]
    public void BadMagicIsRejected()
    {
        var frame = new HandshakeProtocol.ServerHello().Encode().ToArray();
        frame[0] = (byte)'X';
        var ex = Assert.Throws<TransportException>(() => HandshakeProtocol.ServerHello.Decode(frame));
        Assert.Equal(TransportErrorCode.HandshakeRequired, ex.Code);
    }

    [Fact]
    public void UnsupportedVersionIsRejected()
    {
        var hello = new HandshakeProtocol.ClientHello { Version = 99, Nonce = new byte[32] };
        var ex = Assert.Throws<TransportException>(() => HandshakeProtocol.ClientHello.Decode(hello.Encode()));
        Assert.Equal(TransportErrorCode.HandshakeRequired, ex.Code);
    }

    [Fact]
    public void ServerHelloRoundTrips()
    {
        var hello = new HandshakeProtocol.ServerHello
        {
            NegotiatedFeatures = FeatureFlags.Lz4,
            Capabilities = TransportCapabilities.HandshakeRequired | TransportCapabilities.Lz4,
            RequiredMethod = AuthMethod.SharedSecret,
            Lz4ThresholdBytes = 1024,
            MaxPayloadFrameBytes = 4 * 1024 * 1024,
            Pbkdf2Iterations = 600_000,
            Nonce = Enumerable.Repeat((byte)7, 32).ToArray(),
            Salt = Enumerable.Repeat((byte)9, 16).ToArray(),
        };

        var decoded = HandshakeProtocol.ServerHello.Decode(hello.Encode());

        Assert.Equal(hello.RequiredMethod, decoded.RequiredMethod);
        Assert.Equal(hello.NegotiatedFeatures, decoded.NegotiatedFeatures);
        Assert.Equal(hello.Pbkdf2Iterations, decoded.Pbkdf2Iterations);
        Assert.Equal(hello.Salt, decoded.Salt);
        Assert.Equal(HandshakeProtocol.ServerHelloSize, hello.Encode().Length);
    }

    [Fact]
    public void AuthFramesRoundTrip()
    {
        var request = new HandshakeProtocol.AuthRequestFrame
        {
            Method = AuthMethod.DevicePassword,
            DeviceId = "1-4.2",
            Proof = Enumerable.Repeat((byte)3, 32).ToArray(),
        };

        var decodedRequest = HandshakeProtocol.AuthRequestFrame.Decode(request.Encode());
        Assert.Equal(request.Method, decodedRequest.Method);
        Assert.Equal(request.DeviceId, decodedRequest.DeviceId);
        Assert.Equal(request.Proof, decodedRequest.Proof);

        var reply = new HandshakeProtocol.AuthReplyFrame
        {
            Status = AuthStatus.Throttled,
            Method = AuthMethod.SharedSecret,
            RetryAfterMs = 1500,
            Proof = Enumerable.Repeat((byte)5, 32).ToArray(),
        };

        var decodedReply = HandshakeProtocol.AuthReplyFrame.Decode(reply.Encode());
        Assert.Equal(reply.Status, decodedReply.Status);
        Assert.Equal(reply.Method, decodedReply.Method);
        Assert.Equal(reply.RetryAfterMs, decodedReply.RetryAfterMs);
        Assert.Equal(reply.Proof, decodedReply.Proof);
    }

    [Fact]
    public async Task OversizedLengthPrefixIsRefusedBeforeAllocation()
    {
        var frame = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 };
        using var stream = new MemoryStream(frame);

        var ex = await Assert.ThrowsAsync<TransportException>(
            async () => await HandshakeProtocol.ReadLengthPrefixedAsync(stream, CancellationToken.None));

        Assert.Equal(TransportErrorCode.Protocol, ex.Code);
    }
}

public sealed class SecretVerifierTests
{
    [Fact]
    public void DefaultWorkFactorIsSixHundredThousand()
    {
        Assert.Equal(600_000, SecretVerifier.DefaultIterations);
    }

    [Fact]
    public void SameSecretAndSaltProduceTheSameProof()
    {
        var salt = SecretVerifier.NewSalt();
        var a = SecretVerifier.Create("hunter2", Harness.FastIterations, salt);
        var b = SecretVerifier.Create("hunter2", Harness.FastIterations, salt);
        var nonceA = new byte[32];
        var nonceB = Enumerable.Repeat((byte)1, 32).ToArray();

        Assert.Equal(
            a.ComputeProof(HandshakeProtocol.ClientProofLabel, nonceA, nonceB, AuthMethod.SharedSecret, string.Empty),
            b.ComputeProof(HandshakeProtocol.ClientProofLabel, nonceA, nonceB, AuthMethod.SharedSecret, string.Empty));
    }

    [Fact]
    public void DifferentSaltOrNonceOrDeviceIdChangesTheProof()
    {
        var verifier = SecretVerifier.Create("hunter2", Harness.FastIterations);
        var other = SecretVerifier.Create("hunter2", Harness.FastIterations);
        var serverNonce = new byte[32];
        var otherNonce = Enumerable.Repeat((byte)2, 32).ToArray();

        var baseline = verifier.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], serverNonce, AuthMethod.DevicePassword, "1-1");

        Assert.NotEqual(baseline, other.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], serverNonce, AuthMethod.DevicePassword, "1-1"));
        Assert.NotEqual(baseline, verifier.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], otherNonce, AuthMethod.DevicePassword, "1-1"));
        Assert.NotEqual(baseline, verifier.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], serverNonce, AuthMethod.DevicePassword, "1-2"));
        Assert.NotEqual(baseline, verifier.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], serverNonce, AuthMethod.SharedSecret, "1-1"));
    }

    [Fact]
    public void ClientAndServerProofsAreNotInterchangeable()
    {
        var verifier = SecretVerifier.Create("hunter2", Harness.FastIterations);
        var client = verifier.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], new byte[32], AuthMethod.SharedSecret, string.Empty);
        var server = verifier.ComputeProof(HandshakeProtocol.ServerProofLabel, new byte[32], new byte[32], AuthMethod.SharedSecret, string.Empty);

        Assert.NotEqual(client, server);
    }

    [Fact]
    public void VerificationRejectsWrongAndTruncatedProofs()
    {
        var verifier = SecretVerifier.Create("hunter2", Harness.FastIterations);
        var proof = verifier.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], new byte[32], AuthMethod.SharedSecret, string.Empty);
        var wrong = proof.ToArray();
        wrong[31] ^= 0x01;

        Assert.True(SecretVerifier.Verify(proof, proof));
        Assert.False(SecretVerifier.Verify(proof, wrong));
        Assert.False(SecretVerifier.Verify(proof, proof[..16]));
    }

    [Fact]
    public void DummyVerifierNeverMatchesARealOne()
    {
        var dummy = SecretVerifier.Dummy("1-1");
        var real = SecretVerifier.Create("hunter2", Harness.FastIterations, dummy.Salt);

        Assert.NotEqual(
            dummy.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], new byte[32], AuthMethod.DevicePassword, "1-1"),
            real.ComputeProof(HandshakeProtocol.ClientProofLabel, new byte[32], new byte[32], AuthMethod.DevicePassword, "1-1"));
    }
}

public sealed class VerifierSetTests
{
    private static AuthenticationOptions Options => new()
    {
        SharedSecret = "server-wide-secret",
        DevicePasswords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["1-4.2"] = "device-password" },
        Pbkdf2Iterations = Harness.FastIterations,
    };

    [Fact]
    public void UnknownDeviceGetsADummyChallengeButIsNotKnown()
    {
        var set = new VerifierSet(Options);

        Assert.True(set.IsKnown(AuthMethod.DevicePassword, "1-4.2"));
        Assert.False(set.IsKnown(AuthMethod.DevicePassword, "9-9.9"));
        Assert.NotNull(set.GetChallengeVerifier(AuthMethod.DevicePassword, "9-9.9"));
    }

    [Fact]
    public void CliplessClientStillGetsServersStrongestMethod()
    {
        var set = new VerifierSet(Options);
        Assert.Equal(AuthMethod.SharedSecret, set.ResolveRequiredMethod(AuthMethod.None));

        var deviceOnly = new VerifierSet(Options with { SharedSecret = null });
        Assert.Equal(AuthMethod.DevicePassword, deviceOnly.ResolveRequiredMethod(AuthMethod.None));
    }

    [Fact]
    public void RequestedMethodIsHonouredEvenWhenNoSecretIsConfigured()
    {
        var set = new VerifierSet(new AuthenticationOptions { Pbkdf2Iterations = Harness.FastIterations });
        Assert.Equal(AuthMethod.DevicePassword, set.ResolveRequiredMethod(AuthMethod.DevicePassword));
        Assert.False(set.IsKnown(AuthMethod.DevicePassword, "1-1"));
    }
}

public sealed class AuthThrottleTests
{
    private static RateLimitOptions Options => new()
    {
        PeerBurst = 3,
        PeerRefillPerMinute = 60,
        GlobalBurst = 100,
        GlobalRefillPerMinute = 600,
        LockoutAfterFailures = 2,
        LockoutBase = TimeSpan.FromSeconds(2),
        LockoutMax = TimeSpan.FromSeconds(60),
        FailureMemory = TimeSpan.FromMinutes(15),
    };

    [Fact]
    public void BurstIsEnforcedPerPeer()
    {
        var time = new ManualTimeProvider();
        var throttle = new AuthThrottle(Options, time);

        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
        Assert.False(throttle.TryAcquire("10.0.0.1", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // A different peer is unaffected.
        Assert.True(throttle.TryAcquire("10.0.0.2", out _));
    }

    [Fact]
    public void TokensRefillOverTime()
    {
        var time = new ManualTimeProvider();
        var throttle = new AuthThrottle(Options, time);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(throttle.TryAcquire("10.0.0.1", out _));
        }

        Assert.False(throttle.TryAcquire("10.0.0.1", out _));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
    }

    [Fact]
    public void RepeatedFailuresLockThePeerOutExponentially()
    {
        var time = new ManualTimeProvider();
        var throttle = new AuthThrottle(Options, time);

        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
        throttle.RecordFailure("10.0.0.1");
        Assert.Equal(1, throttle.ConsecutiveFailures("10.0.0.1"));

        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
        throttle.RecordFailure("10.0.0.1");

        // LockoutAfterFailures = 2, so the third attempt is refused outright even though tokens remain.
        Assert.False(throttle.TryAcquire("10.0.0.1", out var retryAfter));
        Assert.True(retryAfter >= TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(throttle.TryAcquire("10.0.0.1", out _));

        // A fourth failure doubles the lockout window.
        throttle.RecordFailure("10.0.0.1");
        Assert.False(throttle.TryAcquire("10.0.0.1", out var doubled));
        Assert.True(doubled >= TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void SuccessClearsFailureHistory()
    {
        var time = new ManualTimeProvider();
        var throttle = new AuthThrottle(Options, time);

        throttle.RecordFailure("10.0.0.1");
        throttle.RecordFailure("10.0.0.1");
        Assert.False(throttle.TryAcquire("10.0.0.1", out _));

        throttle.RecordSuccess("10.0.0.1");
        Assert.Equal(0, throttle.ConsecutiveFailures("10.0.0.1"));
        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
    }

    [Fact]
    public void GlobalBucketStopsDistributedGuessing()
    {
        var time = new ManualTimeProvider();
        var options = Options with { GlobalBurst = 2, GlobalRefillPerMinute = 600 };
        var throttle = new AuthThrottle(options, time);

        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
        Assert.True(throttle.TryAcquire("10.0.0.2", out _));
        Assert.False(throttle.TryAcquire("10.0.0.3", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void FailureHistoryExpires()
    {
        var time = new ManualTimeProvider();
        var throttle = new AuthThrottle(Options, time);

        throttle.RecordFailure("10.0.0.1");
        throttle.RecordFailure("10.0.0.1");
        Assert.False(throttle.TryAcquire("10.0.0.1", out _));

        time.Advance(TimeSpan.FromMinutes(20));
        Assert.True(throttle.TryAcquire("10.0.0.1", out _));
        Assert.Equal(0, throttle.ConsecutiveFailures("10.0.0.1"));
    }
}

public sealed class PinValidatorTests
{
    private static X509Certificate2 NewCertificate(string commonName)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        var request = new CertificateRequest($"CN={commonName}", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.EphemeralKeySet);
    }

    [Fact]
    public void TrustOnFirstUseRecordsThenRequiresTheSameKey()
    {
        var store = new InMemoryPinStore();
        using var certificate = NewCertificate("server-a");

        var first = PinValidator.Evaluate(PinMode.TrustOnFirstUse, store, "host:3241", certificate);
        Assert.True(first.Accepted);
        Assert.True(first.NewlyRecorded);

        var second = PinValidator.Evaluate(PinMode.TrustOnFirstUse, store, "host:3241", certificate);
        Assert.True(second.Accepted);
        Assert.False(second.NewlyRecorded);
    }

    [Fact]
    public void ChangedKeyIsRefusedEvenInTrustOnFirstUse()
    {
        var store = new InMemoryPinStore();
        using var original = NewCertificate("server-a");
        PinValidator.Evaluate(PinMode.TrustOnFirstUse, store, "host:3241", original);

        using var impostor = NewCertificate("server-a");
        var decision = PinValidator.Evaluate(PinMode.TrustOnFirstUse, store, "host:3241", impostor);

        Assert.False(decision.Accepted);
        Assert.Contains("pinned key mismatch", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictModeRefusesAnUnpinnedPeer()
    {
        using var certificate = NewCertificate("server-a");
        var decision = PinValidator.Evaluate(PinMode.Strict, new InMemoryPinStore(), "host:3241", certificate);

        Assert.False(decision.Accepted);
        Assert.Contains("Strict", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingStoreIsRefusedRatherThanSilentlyTrusting()
    {
        using var certificate = NewCertificate("server-a");

        var tofu = PinValidator.Evaluate(PinMode.TrustOnFirstUse, null, "host:3241", certificate);
        var strict = PinValidator.Evaluate(PinMode.Strict, null, "host:3241", certificate);

        Assert.False(tofu.Accepted);
        Assert.False(strict.Accepted);
    }

    [Fact]
    public void InsecureModeAcceptsButSaysSo()
    {
        using var certificate = NewCertificate("server-a");
        var decision = PinValidator.Evaluate(PinMode.Insecure, null, "host:3241", certificate);

        Assert.True(decision.Accepted);
        Assert.Contains("pinning disabled", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptPinStoreIsRefused()
    {
        var path = Path.Combine(Harness.TempPath("pins"), "known_peers.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new JsonPinStore(path);

        Assert.False(store.Healthy);
        using var certificate = NewCertificate("server-a");
        var decision = PinValidator.Evaluate(PinMode.Strict, store, "host:3241", certificate);
        Assert.False(decision.Accepted);
    }

    [Fact]
    public void JsonPinStorePersistsAcrossInstances()
    {
        var path = Path.Combine(Harness.TempPath("pins"), "known_peers.json");
        var first = new JsonPinStore(path);
        first.StorePin("host:3241", "AAAABBBBCCCC");

        var second = new JsonPinStore(path);
        Assert.True(second.TryGetPin("host:3241", out var pin));
        Assert.Equal("AAAABBBBCCCC", pin);
        Assert.True(second.RemovePin("host:3241"));
        Assert.False(second.TryGetPin("host:3241", out _));
    }

    [Fact]
    public void CertificatePinIsSpkiNotWholeCertificate()
    {
        using var certificate = NewCertificate("server-a");
        var pin = ServerCertificateManager.ComputeSpkiPin(certificate);

        Assert.Equal(ServerCertificateManager.PinLength, pin.Length);
        Assert.DoesNotContain("=", pin, StringComparison.Ordinal);
        Assert.DoesNotContain("+", pin, StringComparison.Ordinal);
        Assert.DoesNotContain("/", pin, StringComparison.Ordinal);
    }
}

public sealed class Lz4FramingTests
{
    [Fact]
    public void CompressiblePayloadShrinksAndRoundTrips()
    {
        var payload = new byte[64 * 1024];
        Array.Fill(payload, (byte)'A');

        var destination = new byte[Lz4Framing.MaximumCompressedSize(payload.Length)];
        var written = Lz4Framing.TryCompress(payload, destination);
        Assert.True(written > 0 && written < payload.Length);

        var restored = new byte[payload.Length];
        Assert.Equal(payload.Length, Lz4Framing.Decompress(destination.AsSpan(0, written), restored));
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void FrameHeaderIsThirteenBytesBigEndian()
    {
        var header = new byte[Lz4Framing.HeaderSize];
        Lz4Framing.WriteHeader(header, Lz4FrameType.Lz4, 0x01020304, 0x05060708, 0xAABBCCDD);

        Assert.Equal((byte)Lz4FrameType.Lz4, header[0]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, header[1..5]);
        Assert.Equal(new byte[] { 0x05, 0x06, 0x07, 0x08 }, header[5..9]);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, header[9..13]);
    }

    [Fact]
    public void UnknownFrameTypeIsRejected()
    {
        var header = new byte[Lz4Framing.HeaderSize];
        Lz4Framing.WriteHeader(header, Lz4FrameType.Raw, 4, 4, 0);
        header[0] = 0x42;

        var ex = Assert.Throws<TransportException>(() => Lz4Framing.ReadHeader(header, 1024, 1024));
        Assert.Equal(TransportErrorCode.Protocol, ex.Code);
    }

    [Fact]
    public void ImpossibleDeclaredLengthIsRefusedAsAMemoryBomb()
    {
        var header = new byte[Lz4Framing.HeaderSize];
        Lz4Framing.WriteHeader(header, Lz4FrameType.Lz4, 16, 8 * 1024 * 1024, 0);

        var ex = Assert.Throws<TransportException>(() => Lz4Framing.ReadHeader(header, 1024 * 1024, 2 * 1024 * 1024));
        Assert.Equal(TransportErrorCode.FrameTooLarge, ex.Code);
    }

    [Fact]
    public void RawFrameWithMismatchedLengthsIsRefused()
    {
        var header = new byte[Lz4Framing.HeaderSize];
        Lz4Framing.WriteHeader(header, Lz4FrameType.Raw, 10, 20, 0);

        var ex = Assert.Throws<TransportException>(() => Lz4Framing.ReadHeader(header, 1024, 1024));
        Assert.Equal(TransportErrorCode.Protocol, ex.Code);
    }
}

public sealed class TransportOptionsTests
{
    [Fact]
    public void PlaintextCannotCarryAuthentication()
    {
        Assert.Throws<ArgumentException>(() => new TransportOptions
        {
            Kind = TransportKind.PlainTcp,
            Authentication = new AuthenticationOptions { SharedSecret = "x", Pbkdf2Iterations = Harness.FastIterations },
        }.Validate());
    }

    [Fact]
    public void PlaintextCannotCarryAClientCredential()
    {
        Assert.Throws<ArgumentException>(() => new TransportOptions
        {
            Kind = TransportKind.PlainTcp,
            ClientCredential = AuthRequest.ForSharedSecret("x"),
        }.Validate());
    }

    [Fact]
    public void DefaultPortsMatchTheArchitecture()
    {
        Assert.Equal(3240, TransportOptions.DefaultPort(TransportKind.PlainTcp));
        Assert.Equal(3241, TransportOptions.DefaultPort(TransportKind.Tls));
        Assert.Equal(3241, TransportOptions.DefaultPort(TransportKind.TlsLz4));
    }

    [Fact]
    public void DefaultThresholdAndFrameLimitsMatchTheArchitecture()
    {
        var options = new Lz4Options();
        Assert.Equal(1024, options.ThresholdBytes);
        options.Validate();
    }

    [Fact]
    public void SharedAuthStateIsCreatedOnce()
    {
        var options = new TransportOptions
        {
            Authentication = new AuthenticationOptions { SharedSecret = "x", Pbkdf2Iterations = Harness.FastIterations },
        }.WithSharedAuthState();

        Assert.NotNull(options.Throttle);
        Assert.NotNull(options.Verifiers);
        Assert.Same(options.Throttle, options.WithSharedAuthState().Throttle);
    }
}
