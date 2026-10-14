using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vht.Transport.Auth;
using Vht.Transport.Tls;
using Vht.Transport.Wire;

namespace Vht.Transport;

/// <summary>
/// USB/IP inside TLS 1.3 with the mandatory <c>_vht</c> version/capability handshake and HMAC
/// challenge-response authentication (ARCHITECTURE.md §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where this class runs.</b> Both ends: <c>vht-server</c> accepts with
/// <see cref="AcceptAsync"/> on the TLS listener, and <c>vht-client</c>'s loopback bridge connects
/// with <see cref="ConnectAsync"/> on the server-facing hop. The plaintext leg of the bridge is on
/// 127.0.0.1 only and never touches this type.
/// </para>
/// <para>
/// <b>How downgrade is prevented.</b> The handshake is performed inside the factories, before an
/// instance exists. A peer whose first frame is not a well-formed <c>VHT1</c> client hello — for
/// example a stock USB/IP client sending <c>OP_REQ_DEVLIST</c>, whose first bytes are
/// <c>0x80 0x05 ...</c> — is rejected with <see cref="TransportErrorCode.HandshakeRequired"/>. There
/// is no code path that continues without it, on either end.
/// </para>
/// <para>
/// <b>How authentication fails closed.</b> If the negotiated method requires a credential,
/// <see cref="ReadAsync"/> and <see cref="WriteAsync"/> throw until
/// <see cref="AuthenticateAsync"/> has succeeded. A failed or throttled attempt leaves the transport
/// permanently unusable rather than merely reporting an error the caller might ignore.
/// </para>
/// </remarks>
public sealed class TlsTransport : ITransport, IPeerInfo
{
    private enum AuthState
    {
        Pending,
        Authenticated,
        Failed,
    }

    private readonly SslStream _ssl;
    private readonly AuthThrottle? _throttle;
    private readonly SecretVerifier? _challengeVerifier;
    private readonly bool _challengeIsKnown;
    private readonly bool _isServer;
    private X509Certificate2? _ownedCertificate;

    private AuthState _authState;
    private AuthResult? _authResult;

    private TlsTransport(
        SslStream ssl,
        bool isServer,
        TransportOptions options,
        HandshakeProtocol.ClientHello clientHello,
        HandshakeProtocol.ServerHello serverHello,
        AuthThrottle? throttle,
        SecretVerifier? challengeVerifier,
        bool challengeIsKnown,
        string peerLabel,
        EndPoint? remoteEndPoint,
        string? peerSpkiPin)
    {
        _ssl = ssl;
        _isServer = isServer;
        _options = options;
        _clientHello = clientHello;
        _serverHello = serverHello;
        _throttle = throttle;
        _challengeVerifier = challengeVerifier;
        _challengeIsKnown = challengeIsKnown;
        PeerLabel = peerLabel;
        RemoteEndPoint = remoteEndPoint;
        PeerSpkiPin = peerSpkiPin;
        NegotiatedProtocol = ssl.SslProtocol;
        RateLimitKey = isServer ? ThrottleKeyFor(remoteEndPoint) : peerLabel;

        var capabilities = TransportCapabilities.HandshakeRequired;
        if (serverHello.NegotiatedFeatures.HasFlag(FeatureFlags.Lz4))
        {
            capabilities |= TransportCapabilities.Lz4;
            Lz4Negotiated = true;
        }

        if (serverHello.Capabilities.HasFlag(TransportCapabilities.SharedSecret))
        {
            capabilities |= TransportCapabilities.SharedSecret;
        }

        if (serverHello.Capabilities.HasFlag(TransportCapabilities.DevicePassword))
        {
            capabilities |= TransportCapabilities.DevicePassword;
        }

        if (serverHello.NegotiatedFeatures.HasFlag(FeatureFlags.Isochronous))
        {
            capabilities |= TransportCapabilities.Isochronous;
        }

        capabilities |= TransportCapabilities.PeerPinning;
        Capabilities = capabilities;

        _authState = serverHello.RequiredMethod == AuthMethod.None ? AuthState.Authenticated : AuthState.Pending;
        if (serverHello.RequiredMethod == AuthMethod.None)
        {
            _authResult = AuthResult.NoneRequired;
        }
    }

    private readonly TransportOptions _options;
    private readonly HandshakeProtocol.ClientHello _clientHello;
    private readonly HandshakeProtocol.ServerHello _serverHello;

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Tls;

    /// <inheritdoc />
    public TransportCapabilities Capabilities { get; }

    /// <inheritdoc />
    public string PeerLabel { get; }

    /// <inheritdoc />
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>Peer address only, without the ephemeral port: the rate-limiting key of ARCHITECTURE.md §6.1.</summary>
    public string RateLimitKey { get; }

    /// <summary>Session counters.</summary>
    public TransportStatistics Statistics { get; } = new();

    /// <summary>The negotiated TLS protocol version.</summary>
    public SslProtocols NegotiatedProtocol { get; }

    /// <summary>SPKI pin of the peer certificate, when the peer presented one.</summary>
    public string? PeerSpkiPin { get; }

    /// <summary>Client name from the client hello (server side) or our own configured name (client side).</summary>
    public string ClientName => _clientHello.Name;

    /// <summary>Busid the client declared in its hello, if any.</summary>
    public string ClientDeviceId => _clientHello.DeviceId;

    /// <summary>Method the server requires for this session.</summary>
    public AuthMethod RequiredAuthMethod => _serverHello.RequiredMethod;

    /// <summary>Features negotiated with the peer.</summary>
    public FeatureFlags NegotiatedFeatures => _serverHello.NegotiatedFeatures;

    /// <summary>True when LZ4 was negotiated; the factory then layers <see cref="Lz4Transport"/> on top.</summary>
    public bool Lz4Negotiated { get; }

    /// <summary>Compression threshold the server advertised.</summary>
    public int Lz4ThresholdBytes => _serverHello.Lz4ThresholdBytes;

    /// <summary>Largest payload frame the server will accept.</summary>
    public int ServerMaxPayloadFrameBytes => _serverHello.MaxPayloadFrameBytes;

    /// <summary>The result of authentication, once it has run.</summary>
    public AuthResult? AuthOutcome => _authResult;

    /// <summary>
    /// TLS layer, exposed so the loopback bridge and the benchmark can measure the underlying
    /// stream without re-implementing the session.
    /// </summary>
    public SslStream SslStream => _ssl;

    /// <summary>
    /// Server side: performs the TLS handshake, requires the <c>_vht</c> handshake, and answers with
    /// the server hello. The returned transport is not usable for device traffic until
    /// <see cref="AuthenticateAsync"/> succeeds.
    /// </summary>
    public static async Task<TlsTransport> AcceptAsync(
        Stream rawStream,
        TransportOptions options,
        EndPoint? remoteEndPoint,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawStream);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Kind == TransportKind.PlainTcp)
        {
            throw new ArgumentException("plaintext sessions are served by PlainTcpTransport", nameof(options));
        }

        var verifiers = options.Verifiers ?? new VerifierSet(options.Authentication);
        var throttle = options.Throttle ?? new AuthThrottle(options.Authentication.RateLimit);
        var ownsCertificate = options.Tls.ServerCertificate is null;
        var certificate = options.Tls.ServerCertificate ?? ServerCertificateManager.LoadOrCreate(
            options.Tls.ResolveCertificateDirectory(),
            options.ServerName,
            out _,
            options.Tls.AllowInsecureKeyStorage);

        var ssl = new SslStream(
            await RequireTlsRecordAsync(rawStream, options.HandshakeTimeout, ct).ConfigureAwait(false),
            leaveInnerStreamOpen: false);
        using var timeout = CreateHandshakeTimeout(options.HandshakeTimeout, ct);

        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = options.Tls.Protocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                timeout.Token).ConfigureAwait(false);

            EnforceProtocolFloor(ssl, options);

            var clientHello = await HandshakeProtocol.ReadClientHelloAsync(ssl, timeout.Token).ConfigureAwait(false);

            var requiredMethod = verifiers.ResolveRequiredMethod(clientHello.Method);
            var deviceId = clientHello.Method == AuthMethod.DevicePassword ? clientHello.DeviceId : null;
            var challengeIsKnown = verifiers.IsKnown(requiredMethod, deviceId);
            var challengeVerifier = verifiers.GetChallengeVerifier(requiredMethod, deviceId);

            var negotiated = FeatureFlags.None;
            if (Lz4Available(options) && clientHello.RequestedFeatures.HasFlag(FeatureFlags.Lz4))
            {
                negotiated |= FeatureFlags.Lz4;
            }

            if (options.RequestIsochronous && clientHello.RequestedFeatures.HasFlag(FeatureFlags.Isochronous))
            {
                negotiated |= FeatureFlags.Isochronous;
            }

            if (requiredMethod == AuthMethod.SharedSecret)
            {
                negotiated |= FeatureFlags.SharedSecret;
            }

            if (options.Authentication.DevicePasswords.Count > 0)
            {
                negotiated |= FeatureFlags.DevicePassword;
            }

            var serverHello = new HandshakeProtocol.ServerHello
            {
                Version = HandshakeProtocol.Version,
                NegotiatedFeatures = negotiated,
                Capabilities = ServerCapabilities(options),
                RequiredMethod = requiredMethod,
                Status = AuthStatus.Success,
                Lz4ThresholdBytes = Lz4Available(options) ? options.Lz4.ThresholdBytes : 0,
                MaxPayloadFrameBytes = options.Lz4.MaxPayloadFrameBytes,
                Pbkdf2Iterations = verifiers.Iterations,
                Nonce = RandomNumberGenerator.GetBytes(HandshakeProtocol.NonceSize),
                Salt = challengeVerifier.Salt,
            };

            await HandshakeProtocol
                .WriteLengthPrefixedAsync(ssl, serverHello.Encode(), timeout.Token)
                .ConfigureAwait(false);

            var label = DescribeEndPoint(remoteEndPoint);
            var transport = new TlsTransport(
                ssl,
                isServer: true,
                options,
                clientHello,
                serverHello,
                throttle,
                challengeVerifier,
                challengeIsKnown,
                label,
                remoteEndPoint,
                peerSpkiPin: null);

            if (ownsCertificate)
            {
                transport.TakeCertificateOwnership(certificate);
            }

            return transport;
        }
        catch
        {
            if (ownsCertificate)
            {
                certificate.Dispose();
            }

            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Client side: performs the TLS handshake with SPKI pinning, sends the client hello and requires
    /// a well-formed server hello. The returned transport is not usable for device traffic until
    /// <see cref="AuthenticateAsync"/> succeeds.
    /// </summary>
    /// <param name="rawStream">Connected stream to the server.</param>
    /// <param name="peerIdentity">Pin key, conventionally <c>host:port</c>.</param>
    /// <param name="options">Transport policy; <see cref="TransportOptions.Tls"/> supplies pinning.</param>
    /// <param name="credential">Credential to declare in the client hello; defaults to <see cref="TransportOptions.ClientCredential"/>.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<TlsTransport> ConnectAsync(
        Stream rawStream,
        string peerIdentity,
        TransportOptions options,
        AuthRequest? credential,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerIdentity);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Kind == TransportKind.PlainTcp)
        {
            throw new ArgumentException("use PlainTcpTransport for plaintext sessions", nameof(options));
        }

        credential ??= options.ClientCredential;
        PinDecision? pinDecision = null;
        string? observedPin = null;

        var ssl = new SslStream(
            rawStream,
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                using var peer = certificate as X509Certificate2 ?? (certificate is null ? null : new X509Certificate2(certificate));
                pinDecision = PinValidator.Evaluate(options.Tls.PinMode, options.Tls.PinStore, peerIdentity, peer);
                observedPin = pinDecision.SpkiPin;
                return pinDecision.Accepted;
            });

        using var timeout = CreateHandshakeTimeout(options.HandshakeTimeout, ct);

        try
        {
            try
            {
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = options.ServerName,
                        EnabledSslProtocols = options.Tls.Protocols,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    },
                    timeout.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException ex) when (pinDecision is { Accepted: false })
            {
                throw new TransportException(TransportErrorCode.PinMismatch, pinDecision.Reason ?? "server certificate rejected", ex);
            }

            EnforceProtocolFloor(ssl, options);

            var deviceId = credential.Method == AuthMethod.DevicePassword ? credential.DeviceId ?? string.Empty : string.Empty;
            var requestedFeatures = FeatureFlags.None;
            if (Lz4Available(options))
            {
                requestedFeatures |= FeatureFlags.Lz4;
            }

            if (options.RequestIsochronous)
            {
                requestedFeatures |= FeatureFlags.Isochronous;
            }

            var clientHello = new HandshakeProtocol.ClientHello
            {
                Version = HandshakeProtocol.Version,
                Capabilities = TransportCapabilities.HandshakeRequired | TransportCapabilities.PeerPinning,
                RequestedFeatures = requestedFeatures,
                Method = credential.Method,
                Name = options.ClientName,
                DeviceId = deviceId,
                Nonce = RandomNumberGenerator.GetBytes(HandshakeProtocol.NonceSize),
            };

            await HandshakeProtocol
                .WriteClientHelloAsync(ssl, clientHello, timeout.Token)
                .ConfigureAwait(false);

            var frame = await HandshakeProtocol.ReadLengthPrefixedAsync(ssl, timeout.Token).ConfigureAwait(false);
            var serverHello = HandshakeProtocol.ServerHello.Decode(frame);

            if (serverHello.Status != AuthStatus.Success)
            {
                throw new TransportException(
                    TransportErrorCode.AuthenticationFailed,
                    $"server refused the handshake with status {serverHello.Status}");
            }

            ValidateNoDowngrade(clientHello, serverHello);

            return new TlsTransport(
                ssl,
                isServer: false,
                options,
                clientHello,
                serverHello,
                throttle: null,
                challengeVerifier: null,
                challengeIsKnown: true,
                peerIdentity,
                rawStream is IPeerInfo info ? info.RemoteEndPoint : null,
                observedPin);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        EnsureUsable();
        var read = await _ssl.ReadAsync(buffer, ct).ConfigureAwait(false);
        Statistics.AddBytesRead(read);
        return read;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        EnsureUsable();
        await _ssl.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _ssl.FlushAsync(ct).ConfigureAwait(false);
        Statistics.AddBytesWritten(buffer.Length);
    }

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_authResult is not null && _authState != AuthState.Pending)
        {
            return _authResult;
        }

        try
        {
            var result = _isServer
                ? await AuthenticateAsServerAsync(ct).ConfigureAwait(false)
                : await AuthenticateAsClientAsync(request, ct).ConfigureAwait(false);

            _authResult = result;
            _authState = result.IsSuccess ? AuthState.Authenticated : AuthState.Failed;
            if (!result.IsSuccess)
            {
                _authResult = result;
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _authState = AuthState.Failed;
            var failed = new AuthResult
            {
                Status = AuthStatus.Error,
                Method = _serverHello.RequiredMethod,
                Message = ex.Message,
            };
            _authResult = failed;
            _authResult = failed;
            return failed;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _ssl.DisposeAsync().ConfigureAwait(false);
        _ownedCertificate?.Dispose();
        _ownedCertificate = null;
    }

    private async Task<AuthResult> AuthenticateAsServerAsync(CancellationToken ct)
    {
        var method = _serverHello.RequiredMethod;
        if (method == AuthMethod.None)
        {
            return AuthResult.NoneRequired;
        }

        if (_throttle is null || _challengeVerifier is null)
        {
            return AuthResult.NotSupported(method, "server transport has no authentication material");
        }

        if (!_throttle.TryAcquire(RateLimitKey, out var retryAfter))
        {
            await WriteAuthReplyAsync(AuthStatus.Throttled, method, null, retryAfter, ct).ConfigureAwait(false);
            return AuthResult.Throttled(method, retryAfter, "too many failed attempts from this address");
        }

        HandshakeProtocol.AuthRequestFrame frame;
        try
        {
            var raw = await HandshakeProtocol.ReadLengthPrefixedAsync(_ssl, ct).ConfigureAwait(false);
            frame = HandshakeProtocol.AuthRequestFrame.Decode(raw);
        }
        catch (TransportException ex)
        {
            _throttle.RecordFailure(RateLimitKey);
            await WriteAuthReplyAsync(AuthStatus.Malformed, method, null, TimeSpan.Zero, ct).ConfigureAwait(false);
            return new AuthResult { Status = AuthStatus.Malformed, Method = method, Message = ex.Message };
        }

        var proofValid = _challengeIsKnown
            && frame.Method == method
            && string.Equals(frame.DeviceId, _clientHello.DeviceId, StringComparison.Ordinal);

        var expected = _challengeVerifier.ComputeProof(
            HandshakeProtocol.ClientProofLabel,
            _clientHello.Nonce,
            _serverHello.Nonce,
            method,
            method == AuthMethod.DevicePassword ? frame.DeviceId : string.Empty);

        proofValid &= SecretVerifier.Verify(expected, frame.Proof);

        var serverProof = _challengeVerifier.ComputeProof(
            HandshakeProtocol.ServerProofLabel,
            _clientHello.Nonce,
            _serverHello.Nonce,
            method,
            method == AuthMethod.DevicePassword ? frame.DeviceId : string.Empty);

        if (proofValid)
        {
            await WriteAuthReplyAsync(AuthStatus.Success, method, serverProof, TimeSpan.Zero, ct).ConfigureAwait(false);
            _throttle.RecordSuccess(RateLimitKey);
            return AuthResult.Success(method);
        }

        await WriteAuthReplyAsync(AuthStatus.Unauthorized, method, null, TimeSpan.Zero, ct).ConfigureAwait(false);
        _throttle.RecordFailure(RateLimitKey);
        return AuthResult.Unauthorized(method, "credential rejected");
    }

    private async Task<AuthResult> AuthenticateAsClientAsync(AuthRequest request, CancellationToken ct)
    {
        var method = _serverHello.RequiredMethod;
        if (method == AuthMethod.None)
        {
            if (request.Method != AuthMethod.None)
            {
                return AuthResult.NotSupported(
                    request.Method,
                    "the server does not authenticate this session, so a credential would be ignored");
            }

            return AuthResult.NoneRequired;
        }

        if (request.Method != method)
        {
            return AuthResult.Unauthorized(
                method,
                $"server requires {method} but the client offered {request.Method}");
        }

        var secret = method switch
        {
            AuthMethod.SharedSecret => request.SharedSecret,
            AuthMethod.DevicePassword => request.DevicePassword,
            _ => null,
        };

        if (string.IsNullOrEmpty(secret))
        {
            return AuthResult.Unauthorized(method, $"no credential was supplied for {method}");
        }

        var deviceId = method == AuthMethod.DevicePassword ? request.DeviceId ?? string.Empty : string.Empty;
        var verifier = SecretVerifier.Create(secret, _serverHello.Pbkdf2Iterations, _serverHello.Salt);

        var proof = verifier.ComputeProof(
            HandshakeProtocol.ClientProofLabel,
            _clientHello.Nonce,
            _serverHello.Nonce,
            method,
            deviceId);

        var requestFrame = new HandshakeProtocol.AuthRequestFrame
        {
            Method = method,
            DeviceId = deviceId,
            Proof = proof,
        };

        await HandshakeProtocol
            .WriteLengthPrefixedAsync(_ssl, requestFrame.Encode(), ct)
            .ConfigureAwait(false);

        var rawReply = await HandshakeProtocol.ReadLengthPrefixedAsync(_ssl, ct).ConfigureAwait(false);
        var reply = HandshakeProtocol.AuthReplyFrame.Decode(rawReply);

        if (reply.Status != AuthStatus.Success)
        {
            return reply.Status switch
            {
                AuthStatus.Throttled => AuthResult.Throttled(method, TimeSpan.FromMilliseconds(reply.RetryAfterMs), "server rate limited this address"),
                AuthStatus.Malformed => new AuthResult { Status = AuthStatus.Malformed, Method = method, Message = "server rejected the authentication frame as malformed" },
                _ => AuthResult.Unauthorized(method, "server rejected the credential"),
            };
        }

        var expectedServerProof = verifier.ComputeProof(
            HandshakeProtocol.ServerProofLabel,
            _clientHello.Nonce,
            _serverHello.Nonce,
            method,
            deviceId);

        if (!SecretVerifier.Verify(expectedServerProof, reply.Proof))
        {
            return AuthResult.Unauthorized(
                method,
                "server could not prove knowledge of the shared verifier; refusing to trust this peer");
        }

        return AuthResult.Success(method);
    }

    private async Task WriteAuthReplyAsync(
        AuthStatus status,
        AuthMethod method,
        byte[]? proof,
        TimeSpan retryAfter,
        CancellationToken ct)
    {
        var frame = new HandshakeProtocol.AuthReplyFrame
        {
            Status = status,
            Method = method,
            RetryAfterMs = retryAfter > TimeSpan.Zero ? (int)Math.Min(int.MaxValue, retryAfter.TotalMilliseconds) : 0,
            Proof = proof is { Length: 32 } ? proof : new byte[32],
        };

        await HandshakeProtocol.WriteLengthPrefixedAsync(_ssl, frame.Encode(), ct).ConfigureAwait(false);
    }

    private void EnsureUsable()
    {
        if (_authState == AuthState.Failed)
        {
            throw new TransportException(
                TransportErrorCode.AuthenticationFailed,
                $"the session is not authenticated ({_authResult?.Status}: {_authResult?.Message}); refusing device traffic");
        }

        if (_authState == AuthState.Pending)
        {
            throw new TransportException(
                TransportErrorCode.InvalidState,
                "AuthenticateAsync must succeed before device traffic is written or read on an authenticated session");
        }
    }

    /// <summary>
    /// Whether this session may put LZ4 on the wire at all. Compression is a property of the session
    /// <em>kind</em>, not of the LZ4 policy record alone: a <see cref="TransportKind.Tls"/> session has
    /// no compression layer regardless of <see cref="Lz4Options.Enabled"/>, so it must neither offer nor
    /// advertise LZ4. <see cref="Lz4Options.Enabled"/> is the per-peer switch <em>within</em> a
    /// <see cref="TransportKind.TlsLz4"/> session.
    /// </summary>
    internal static bool Lz4Available(TransportOptions options) =>
        options.Kind == TransportKind.TlsLz4 && options.Lz4.Enabled;

    private void TakeCertificateOwnership(X509Certificate2 certificate) => _ownedCertificate = certificate;

    internal static TransportCapabilities ServerCapabilities(TransportOptions options)
    {
        var capabilities = TransportCapabilities.HandshakeRequired
            | TransportCapabilities.PeerPinning
            | TransportCapabilities.Isochronous;

        if (Lz4Available(options))
        {
            capabilities |= TransportCapabilities.Lz4;
        }

        if (options.Authentication.SharedSecret is not null)
        {
            capabilities |= TransportCapabilities.SharedSecret;
        }

        if (options.Authentication.DevicePasswords.Count > 0)
        {
            capabilities |= TransportCapabilities.DevicePassword;
        }

        return capabilities;
    }

    /// <summary>
    /// Refuses a server hello that contradicts the client's hello: a capability strip that would
    /// leave the client believing it authenticated when it did not, or asked for compression it will
    /// not get.
    /// </summary>
    private static void ValidateNoDowngrade(
        HandshakeProtocol.ClientHello clientHello,
        HandshakeProtocol.ServerHello serverHello)
    {
        if (clientHello.Method != AuthMethod.None
            && serverHello.RequiredMethod == AuthMethod.None)
        {
            throw new TransportException(
                TransportErrorCode.NotSupported,
                $"the client offered {clientHello.Method} but the server does not authenticate this session; refusing a session that would silently skip authentication");
        }

        if (clientHello.Method == AuthMethod.SharedSecret
            && !serverHello.Capabilities.HasFlag(TransportCapabilities.SharedSecret))
        {
            throw new TransportException(
                TransportErrorCode.NotSupported,
                "the server claims no shared-secret support although the client offered one; refusing the session");
        }

        if (clientHello.Method == AuthMethod.DevicePassword
            && !serverHello.Capabilities.HasFlag(TransportCapabilities.DevicePassword))
        {
            throw new TransportException(
                TransportErrorCode.NotSupported,
                "the server claims no per-device password support although the client offered one; refusing the session");
        }

        if (clientHello.RequestedFeatures.HasFlag(FeatureFlags.Lz4)
            && !serverHello.NegotiatedFeatures.HasFlag(FeatureFlags.Lz4)
            && serverHello.Capabilities.HasFlag(TransportCapabilities.Lz4))
        {
            throw new TransportException(
                TransportErrorCode.Protocol,
                "the server advertises LZ4 support but did not negotiate it; refusing to continue with an inconsistent negotiation");
        }
    }

    private static void EnforceProtocolFloor(SslStream ssl, TransportOptions options)
    {
        if (ssl.SslProtocol == SslProtocols.None)
        {
            throw new TransportException(TransportErrorCode.TransportFailure, "the TLS handshake reported no negotiated protocol");
        }

        if (options.Tls.RequireTls13 && ssl.SslProtocol < SslProtocols.Tls13)
        {
            throw new TransportException(
                TransportErrorCode.TransportFailure,
                $"TLS 1.3 is required but {ssl.SslProtocol} was negotiated");
        }

        if (ssl.SslProtocol < SslProtocols.Tls12)
        {
            throw new TransportException(
                TransportErrorCode.TransportFailure,
                $"refusing a session negotiated below TLS 1.2 ({ssl.SslProtocol})");
        }
    }

    private static CancellationTokenSource CreateHandshakeTimeout(TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>
    /// Reads the first TLS record header and refuses anything that cannot be one.
    /// </summary>
    /// <remarks>
    /// A stock USB/IP client pointed at the TLS listener would otherwise hold a handshake slot until the
    /// handshake timeout expires (Schannel waits for a complete ClientHello that never comes). Sniffing
    /// the record header rejects it in milliseconds with a message that names the right listener, and it
    /// is the cheap half of "a peer that skips the handshake is rejected" (ARCHITECTURE.md §6.3).
    /// The consumed bytes are replayed through <see cref="PrefixedStream"/> so <c>SslStream</c> still
    /// sees the handshake from byte zero.
    /// </remarks>
    private static async Task<Stream> RequireTlsRecordAsync(Stream rawStream, TimeSpan handshakeTimeout, CancellationToken ct)
    {
        var header = new byte[5];
        using var timeout = CreateHandshakeTimeout(handshakeTimeout, ct);
        var read = 0;
        try
        {
            // Byte zero decides it: a stock USB/IP client opens with 0x80, so it is rejected on the first
            // byte that arrives rather than after waiting for a five-byte header that never completes.
            while (read < header.Length)
            {
                var n = await rawStream.ReadAsync(header.AsMemory(read), timeout.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new TransportException(
                        TransportErrorCode.HandshakeRequired,
                        "the peer closed the connection before sending a TLS record; the VirtualThere TLS listener requires TLS on this port");
                }

                read += n;
                if (header[0] != 0x16)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not TransportException)
        {
            await rawStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var complete = read == header.Length;
        var length = complete ? (header[3] << 8) | header[4] : 0;
        var plausible = complete
            && header[0] == 0x16                      // handshake record
            && header[1] == 0x03                      // major version 3 (SSLv3 through TLS 1.3 on the wire)
            && header[2] is >= 0x00 and <= 0x04
            && length is > 0 and <= 0x4000;           // 16 KiB maximum record

        if (!plausible)
        {
            await rawStream.DisposeAsync().ConfigureAwait(false);
            throw new TransportException(
                TransportErrorCode.HandshakeRequired,
                $"the peer did not open with a TLS record (first bytes "
                + $"{header[0]:X2} {header[1]:X2} {header[2]:X2}); a stock USB/IP peer belongs on the "
                + "plaintext listener, and VirtualThere never serves an authenticated export over plaintext");
        }

        return new PrefixedStream(header, rawStream);
    }

    /// <summary>Rate-limit key: the peer's address, without the ephemeral port a reconnect would change.</summary>
    internal static string ThrottleKeyFor(EndPoint? remoteEndPoint) => remoteEndPoint switch
    {
        IPEndPoint ip => ip.Address.ToString(),
        null => "unknown",
        _ => remoteEndPoint.ToString() ?? "unknown",
    };

    private static string DescribeEndPoint(EndPoint? remoteEndPoint) => remoteEndPoint?.ToString() ?? "unknown";
}
