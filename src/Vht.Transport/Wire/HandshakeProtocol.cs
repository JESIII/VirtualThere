using System.Buffers.Binary;
using System.Text;

namespace Vht.Transport.Wire;

/// <summary>
/// The <c>_vht</c> version/capability handshake and authentication exchange, as exchanged immediately
/// after the TLS handshake and before any USB/IP byte (ARCHITECTURE.md §6.1, Appendix E).
/// </summary>
/// <remarks>
/// <para>
/// All integers are big-endian, matching USB/IP's own convention. The frames are small and
/// length-prefixed by construction; every length field is bounds-checked on decode and decode never
/// allocates from an unvalidated length (ARCHITECTURE.md §6.1 threat row 4).
/// </para>
/// <para>
/// The exchange is deliberately not optional on a TLS listener: it is the anti-downgrade control.
/// A peer whose first four bytes are not <see cref="Magic"/> is rejected with
/// <see cref="TransportErrorCode.HandshakeRequired"/> — never accepted as a stock USB/IP peer. That
/// is precisely a stock client sending <c>OP_REQ_DEVLIST</c> (magic bytes 0x80 0x05 ...).
/// </para>
/// </remarks>
public static class HandshakeProtocol
{
    /// <summary>Frame magic: ASCII "VHT1".</summary>
    public static ReadOnlySpan<byte> Magic => "VHT1"u8;

    /// <summary>Protocol version implemented by this build.</summary>
    public const ushort Version = 1;

    /// <summary>Oldest protocol version this build still accepts.</summary>
    public const ushort MinVersion = 1;

    /// <summary>Size of every nonce in the exchange.</summary>
    public const int NonceSize = 32;

    /// <summary>Size of the PBKDF2 salt carried in the server hello for the negotiated method.</summary>
    public const int SaltSize = 16;

    /// <summary>Largest client name accepted.</summary>
    public const int MaxClientNameBytes = 96;

    /// <summary>Largest busid accepted in a client hello.</summary>
    public const int MaxDeviceIdBytes = 64;

    /// <summary>Hard cap on any handshake frame, so a hostile peer cannot make us allocate.</summary>
    public const int MaxFrameBytes = 512;

    /// <summary>Frame tag byte written ahead of the authentication frames.</summary>
    public const byte AuthFrameTag = 0xA1;

    /// <summary>Encoded size of a client hello with an empty name and device id.</summary>
    public const int ClientHelloFixedSize = 4 + 2 + 2 + 4 + 4 + 1 + 1 + 1 + NonceSize;

    /// <summary>Encoded size of a server hello.</summary>
    public const int ServerHelloSize = 4 + 2 + 2 + 4 + 4 + 4 + 4 + 4 + 4 + NonceSize + SaltSize + 1 + 1;

    /// <summary>Encoded size of a server hello that carries no salt (method <see cref="AuthMethod.None"/>); the field is still present, zeroed.</summary>
    public const int AuthRequestFixedSize = 1 + 4 + 2 + 1 + 1 + NonceSize;

    /// <summary>Encoded size of an authentication reply.</summary>
    public const int AuthReplySize = 1 + 4 + 2 + 4 + 1 + 1 + 4 + NonceSize;

    /// <summary>Context label mixed into the client proof. Distinct from <see cref="ServerProofLabel"/> so the two proofs are not interchangeable.</summary>
    public static ReadOnlySpan<byte> ClientProofLabel => "vht-auth-client-v1"u8;

    /// <summary>Context label mixed into the server's mutual proof.</summary>
    public static ReadOnlySpan<byte> ServerProofLabel => "vht-auth-server-v1"u8;

    /// <summary>Reads exactly <paramref name="count"/> bytes or throws on end of stream.</summary>
    public static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"peer closed the connection during the handshake after {read} of {buffer.Length} expected bytes");
            }

            read += n;
        }
    }

    /// <summary>Client hello: what the client is and what it wants.</summary>
    public sealed record ClientHello
    {
        /// <summary>Client protocol version.</summary>
        public ushort Version { get; init; } = HandshakeProtocol.Version;

        /// <summary>Features the client can use.</summary>
        public FeatureFlags RequestedFeatures { get; init; } = FeatureFlags.None;

        /// <summary>Capabilities the client implements.</summary>
        public TransportCapabilities Capabilities { get; init; } = TransportCapabilities.HandshakeRequired;

        /// <summary>Authentication method the client intends to use.</summary>
        public AuthMethod Method { get; init; } = AuthMethod.None;

        /// <summary>Client's name, for logs and for the <c>onClientConnect</c> hook.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Busid the client intends to import. Meaningful when <see cref="Method"/> is <see cref="AuthMethod.DevicePassword"/>.</summary>
        public string DeviceId { get; init; } = string.Empty;

        /// <summary>Fresh 32-byte client nonce.</summary>
        public byte[] Nonce { get; init; } = new byte[NonceSize];

        /// <summary>Encoded length.</summary>
        public int EncodedLength =>
            ClientHelloFixedSize + Encoding.UTF8.GetByteCount(Name) + Encoding.UTF8.GetByteCount(DeviceId);

        /// <summary>Encodes the frame.</summary>
        public byte[] Encode()
        {
            var nameBytes = Encoding.UTF8.GetBytes(Name);
            if (nameBytes.Length > MaxClientNameBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(Name), $"client name exceeds {MaxClientNameBytes} bytes");
            }

            var deviceIdBytes = Encoding.UTF8.GetBytes(DeviceId);
            if (deviceIdBytes.Length > MaxDeviceIdBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(DeviceId), $"device id exceeds {MaxDeviceIdBytes} bytes");
            }

            if (Nonce.Length != NonceSize)
            {
                throw new ArgumentOutOfRangeException(nameof(Nonce), $"nonce must be {NonceSize} bytes");
            }

            var frame = new byte[EncodedLength];
            var span = frame.AsSpan();
            Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt16BigEndian(span[4..], Version);
            BinaryPrimitives.WriteUInt16BigEndian(span[6..], 0);
            BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)Capabilities);
            BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)RequestedFeatures);
            span[16] = (byte)Method;
            span[17] = (byte)nameBytes.Length;
            nameBytes.CopyTo(span[18..]);
            var deviceIdOffset = 18 + nameBytes.Length;
            span[deviceIdOffset] = (byte)deviceIdBytes.Length;
            deviceIdBytes.CopyTo(span[(deviceIdOffset + 1)..]);
            Nonce.CopyTo(span[(deviceIdOffset + 1 + deviceIdBytes.Length)..]);
            return frame;
        }

        /// <summary>Decodes a frame, validating magic, version and every length.</summary>
        public static ClientHello Decode(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < ClientHelloFixedSize)
            {
                throw new TransportException(
                    TransportErrorCode.HandshakeRequired,
                    $"client hello is {frame.Length} bytes, shorter than the {ClientHelloFixedSize}-byte minimum");
            }

            RequireMagic(frame);
            var version = BinaryPrimitives.ReadUInt16BigEndian(frame[4..]);
            RequireVersion(version);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(frame[6..]);
            if (flags != 0)
            {
                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"client hello reserved flags must be zero, got 0x{flags:x4}");
            }

            var capabilities = (TransportCapabilities)BinaryPrimitives.ReadUInt32BigEndian(frame[8..]);
            var features = (FeatureFlags)BinaryPrimitives.ReadUInt32BigEndian(frame[12..]);
            var methodByte = frame[16];
            if (!Enum.IsDefined(typeof(AuthMethod), methodByte))
            {
                throw new TransportException(TransportErrorCode.Protocol, $"unknown authentication method {methodByte}");
            }

            var nameLength = frame[17];
            if (nameLength > MaxClientNameBytes)
            {
                throw new TransportException(TransportErrorCode.Protocol, $"client name length {nameLength} exceeds {MaxClientNameBytes}");
            }

            var name = nameLength == 0 ? string.Empty : Encoding.UTF8.GetString(frame[18..(18 + nameLength)]);
            var deviceIdOffset = 18 + nameLength;
            if (frame.Length < deviceIdOffset + 1)
            {
                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"client hello declares a {nameLength}-byte name but is only {frame.Length} bytes");
            }

            var deviceIdLength = frame[deviceIdOffset];
            if (deviceIdLength > MaxDeviceIdBytes)
            {
                throw new TransportException(TransportErrorCode.Protocol, $"device id length {deviceIdLength} exceeds {MaxDeviceIdBytes}");
            }

            var expected = ClientHelloFixedSize + nameLength + deviceIdLength;
            if (frame.Length != expected)
            {
                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"client hello declares {nameLength} name and {deviceIdLength} device-id bytes but is {frame.Length} bytes");
            }

            var deviceId = deviceIdLength == 0
                ? string.Empty
                : Encoding.UTF8.GetString(frame[(deviceIdOffset + 1)..(deviceIdOffset + 1 + deviceIdLength)]);
            var nonce = frame[(deviceIdOffset + 1 + deviceIdLength)..].ToArray();

            return new ClientHello
            {
                Version = version,
                Capabilities = capabilities,
                RequestedFeatures = features,
                Method = (AuthMethod)methodByte,
                Name = name,
                DeviceId = deviceId,
                Nonce = nonce,
            };
        }
    }

    /// <summary>Server hello: the negotiation result plus the authentication challenge material.</summary>
    public sealed record ServerHello
    {
        /// <summary>Server protocol version.</summary>
        public ushort Version { get; init; } = HandshakeProtocol.Version;

        /// <summary>Features both ends can use.</summary>
        public FeatureFlags NegotiatedFeatures { get; init; } = FeatureFlags.None;

        /// <summary>Capabilities the server offers.</summary>
        public TransportCapabilities Capabilities { get; init; } = TransportCapabilities.None;

        /// <summary>Method the server will require.</summary>
        public AuthMethod RequiredMethod { get; init; } = AuthMethod.None;

        /// <summary>Server's verdict on the hello, before any credential is presented.</summary>
        public AuthStatus Status { get; init; } = AuthStatus.Success;

        /// <summary>Threshold below which payload frames are not compressed.</summary>
        public int Lz4ThresholdBytes { get; init; }

        /// <summary>Largest payload frame either peer may send.</summary>
        public int MaxPayloadFrameBytes { get; init; }

        /// <summary>PBKDF2 iteration count for the negotiated method's verifier.</summary>
        public int Pbkdf2Iterations { get; init; }

        /// <summary>Fresh 32-byte server nonce.</summary>
        public byte[] Nonce { get; init; } = new byte[NonceSize];

        /// <summary>Salt for the negotiated method's verifier. All zeros when <see cref="AuthMethod.None"/>.</summary>
        public byte[] Salt { get; init; } = new byte[SaltSize];

        /// <summary>Encodes the frame.</summary>
        public byte[] Encode()
        {
            if (Nonce.Length != NonceSize || Salt.Length != SaltSize)
            {
                throw new InvalidOperationException("server hello nonce/salt have the wrong size");
            }

            var frame = new byte[ServerHelloSize];
            var span = frame.AsSpan();
            Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt16BigEndian(span[4..], Version);
            BinaryPrimitives.WriteUInt16BigEndian(span[6..], 0);
            BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)Capabilities);
            BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)NegotiatedFeatures);
            BinaryPrimitives.WriteInt32BigEndian(span[16..], Lz4ThresholdBytes);
            BinaryPrimitives.WriteInt32BigEndian(span[20..], MaxPayloadFrameBytes);
            BinaryPrimitives.WriteInt32BigEndian(span[24..], Pbkdf2Iterations);
            BinaryPrimitives.WriteInt32BigEndian(span[28..], (int)Status);
            Nonce.CopyTo(span[32..]);
            Salt.CopyTo(span[64..]);
            span[80] = (byte)RequiredMethod;
            span[81] = 0;
            return frame;
        }

        /// <summary>Decodes and validates a server hello.</summary>
        public static ServerHello Decode(ReadOnlySpan<byte> frame)
        {
            if (frame.Length != ServerHelloSize)
            {
                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"server hello must be exactly {ServerHelloSize} bytes, got {frame.Length}");
            }

            RequireMagic(frame);
            var version = BinaryPrimitives.ReadUInt16BigEndian(frame[4..]);
            RequireVersion(version);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(frame[6..]);
            if (flags != 0)
            {
                throw new TransportException(TransportErrorCode.Protocol, $"server hello reserved flags must be zero, got 0x{flags:x4}");
            }

            var methodByte = frame[80];
            if (!Enum.IsDefined(typeof(AuthMethod), methodByte))
            {
                throw new TransportException(TransportErrorCode.Protocol, $"unknown authentication method {methodByte}");
            }

            var status = BinaryPrimitives.ReadInt32BigEndian(frame[28..]);
            if (!Enum.IsDefined(typeof(AuthStatus), status))
            {
                status = (int)AuthStatus.Error;
            }

            var threshold = BinaryPrimitives.ReadInt32BigEndian(frame[16..]);
            var maxFrame = BinaryPrimitives.ReadInt32BigEndian(frame[20..]);
            var iterations = BinaryPrimitives.ReadInt32BigEndian(frame[24..]);
            if (threshold < 0 || maxFrame <= 0 || iterations < 0)
            {
                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"server hello carries out-of-range parameters (threshold={threshold}, maxFrame={maxFrame}, iterations={iterations})");
            }

            return new ServerHello
            {
                Version = version,
                Capabilities = (TransportCapabilities)BinaryPrimitives.ReadUInt32BigEndian(frame[8..]),
                NegotiatedFeatures = (FeatureFlags)BinaryPrimitives.ReadUInt32BigEndian(frame[12..]),
                Lz4ThresholdBytes = threshold,
                MaxPayloadFrameBytes = maxFrame,
                Pbkdf2Iterations = iterations,
                Status = (AuthStatus)status,
                Nonce = frame[32..(32 + NonceSize)].ToArray(),
                Salt = frame[64..(64 + SaltSize)].ToArray(),
                RequiredMethod = (AuthMethod)methodByte,
            };
        }
    }

    /// <summary>Client's proof of knowledge of the verifier.</summary>
    public sealed record AuthRequestFrame
    {
        /// <summary>Method being used.</summary>
        public AuthMethod Method { get; init; }

        /// <summary>Busid, for <see cref="AuthMethod.DevicePassword"/>; empty otherwise.</summary>
        public string DeviceId { get; init; } = string.Empty;

        /// <summary>HMAC-SHA256 over the negotiated context, keyed by the PBKDF2 verifier.</summary>
        public byte[] Proof { get; init; } = new byte[32];

        /// <summary>Encodes the frame.</summary>
        public byte[] Encode()
        {
            var deviceIdBytes = Encoding.UTF8.GetBytes(DeviceId);
            if (deviceIdBytes.Length > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(DeviceId), "device id exceeds 255 bytes");
            }

            if (Proof.Length != 32)
            {
                throw new ArgumentOutOfRangeException(nameof(Proof), "proof must be 32 bytes");
            }

            var frame = new byte[AuthRequestFixedSize + deviceIdBytes.Length];
            var span = frame.AsSpan();
            span[0] = AuthFrameTag;
            Magic.CopyTo(span[1..]);
            BinaryPrimitives.WriteUInt16BigEndian(span[5..], Version);
            span[7] = (byte)Method;
            span[8] = (byte)deviceIdBytes.Length;
            deviceIdBytes.CopyTo(span[9..]);
            Proof.CopyTo(span[(9 + deviceIdBytes.Length)..]);
            return frame;
        }

        /// <summary>Decodes and validates an authentication request frame.</summary>
        public static AuthRequestFrame Decode(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < AuthRequestFixedSize)
            {
                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"authentication request is {frame.Length} bytes, shorter than the {AuthRequestFixedSize}-byte minimum");
            }

            if (frame[0] != AuthFrameTag)
            {
                throw new TransportException(TransportErrorCode.Protocol, $"expected authentication frame tag 0x{AuthFrameTag:x2}, got 0x{frame[0]:x2}");
            }

            RequireMagic(frame[1..]);
            RequireVersion(BinaryPrimitives.ReadUInt16BigEndian(frame[5..]));
            var methodByte = frame[7];
            if (!Enum.IsDefined(typeof(AuthMethod), methodByte))
            {
                throw new TransportException(TransportErrorCode.Protocol, $"unknown authentication method {methodByte}");
            }

            var deviceIdLength = frame[8];
            var expected = AuthRequestFixedSize + deviceIdLength;
            if (frame.Length != expected)
            {
                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"authentication request declares {deviceIdLength} device-id bytes but carries {frame.Length - AuthRequestFixedSize}");
            }

            var deviceId = deviceIdLength == 0 ? string.Empty : Encoding.UTF8.GetString(frame[9..(9 + deviceIdLength)]);
            return new AuthRequestFrame
            {
                Method = (AuthMethod)methodByte,
                DeviceId = deviceId,
                Proof = frame[(9 + deviceIdLength)..].ToArray(),
            };
        }
    }

    /// <summary>Server's verdict plus its mutual proof, so a rogue server cannot complete the exchange either.</summary>
    public sealed record AuthReplyFrame
    {
        /// <summary>Verdict.</summary>
        public AuthStatus Status { get; init; }

        /// <summary>Method the verdict concerns.</summary>
        public AuthMethod Method { get; init; }

        /// <summary>Retry hint when throttled, in whole milliseconds (0 otherwise).</summary>
        public int RetryAfterMs { get; init; }

        /// <summary>Server proof over the same context with a distinct label; all zeros when not authenticated.</summary>
        public byte[] Proof { get; init; } = new byte[32];

        /// <summary>Encodes the frame.</summary>
        public byte[] Encode()
        {
            if (Proof.Length != 32)
            {
                throw new InvalidOperationException("proof must be 32 bytes");
            }

            var frame = new byte[AuthReplySize];
            var span = frame.AsSpan();
            span[0] = AuthFrameTag;
            Magic.CopyTo(span[1..]);
            BinaryPrimitives.WriteUInt16BigEndian(span[5..], Version);
            BinaryPrimitives.WriteInt32BigEndian(span[7..], (int)Status);
            span[11] = (byte)Method;
            span[12] = 0;
            BinaryPrimitives.WriteInt32BigEndian(span[13..], RetryAfterMs);
            Proof.CopyTo(span[17..]);
            return frame;
        }

        /// <summary>Decodes and validates a reply.</summary>
        public static AuthReplyFrame Decode(ReadOnlySpan<byte> frame)
        {
            if (frame.Length != AuthReplySize)
            {
                throw new TransportException(TransportErrorCode.Protocol, $"authentication reply must be exactly {AuthReplySize} bytes, got {frame.Length}");
            }

            if (frame[0] != AuthFrameTag)
            {
                throw new TransportException(TransportErrorCode.Protocol, $"expected authentication frame tag 0x{AuthFrameTag:x2}, got 0x{frame[0]:x2}");
            }

            RequireMagic(frame[1..]);
            RequireVersion(BinaryPrimitives.ReadUInt16BigEndian(frame[5..]));
            var status = BinaryPrimitives.ReadInt32BigEndian(frame[7..]);
            if (!Enum.IsDefined(typeof(AuthStatus), status))
            {
                throw new TransportException(TransportErrorCode.Protocol, $"unknown authentication status {status}");
            }

            var methodByte = frame[11];
            if (!Enum.IsDefined(typeof(AuthMethod), methodByte))
            {
                throw new TransportException(TransportErrorCode.Protocol, $"unknown authentication method {methodByte}");
            }

            return new AuthReplyFrame
            {
                Status = (AuthStatus)status,
                Method = (AuthMethod)methodByte,
                RetryAfterMs = BinaryPrimitives.ReadInt32BigEndian(frame[13..]),
                Proof = frame[17..].ToArray(),
            };
        }
    }

    /// <summary>
    /// Reads the client hello, which is the one frame that is <em>not</em> length-prefixed.
    /// </summary>
    /// <remarks>
    /// The magic is checked against the very first four bytes of the session, before any length field
    /// is interpreted. That is what makes "a peer that skips the version/capability handshake is
    /// rejected" exact: a stock USB/IP client's opening <c>OP_REQ_DEVLIST</c> (0x80 0x05 0x00 0x00 ...)
    /// fails the magic comparison immediately and is refused with
    /// <see cref="TransportErrorCode.HandshakeRequired"/>, never reinterpreted as a frame length.
    /// Every later frame is length-prefixed, so its size is still bounded before allocation.
    /// </remarks>
    public static async ValueTask<ClientHello> ReadClientHelloAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[18];
        await ReadExactlyAsync(stream, head, ct).ConfigureAwait(false);

        if (!head.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new TransportException(
                TransportErrorCode.HandshakeRequired,
                "peer did not begin with the _vht handshake magic (\"VHT1\"); refusing the session rather than treating it as a stock USB/IP peer");
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        RequireVersion(version);

        var flags = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6));
        if (flags != 0)
        {
            throw new TransportException(TransportErrorCode.Protocol, $"client hello reserved flags must be zero, got 0x{flags:x4}");
        }

        var methodByte = head[16];
        if (!Enum.IsDefined(typeof(AuthMethod), methodByte))
        {
            throw new TransportException(TransportErrorCode.Protocol, $"unknown authentication method {methodByte}");
        }

        var nameLength = head[17];
        if (nameLength > MaxClientNameBytes)
        {
            throw new TransportException(TransportErrorCode.Protocol, $"client name length {nameLength} exceeds {MaxClientNameBytes}");
        }

        var nameBytes = new byte[nameLength];
        if (nameLength > 0)
        {
            await ReadExactlyAsync(stream, nameBytes, ct).ConfigureAwait(false);
        }

        var deviceIdLengthBuffer = new byte[1];
        await ReadExactlyAsync(stream, deviceIdLengthBuffer, ct).ConfigureAwait(false);
        var deviceIdLength = deviceIdLengthBuffer[0];
        if (deviceIdLength > MaxDeviceIdBytes)
        {
            throw new TransportException(TransportErrorCode.Protocol, $"device id length {deviceIdLength} exceeds {MaxDeviceIdBytes}");
        }

        var tail = new byte[deviceIdLength + NonceSize];
        await ReadExactlyAsync(stream, tail, ct).ConfigureAwait(false);

        return new ClientHello
        {
            Version = version,
            Capabilities = (TransportCapabilities)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8)),
            RequestedFeatures = (FeatureFlags)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(12)),
            Method = (AuthMethod)methodByte,
            Name = nameLength == 0 ? string.Empty : Encoding.UTF8.GetString(nameBytes),
            DeviceId = deviceIdLength == 0 ? string.Empty : Encoding.UTF8.GetString(tail, 0, deviceIdLength),
            Nonce = tail[deviceIdLength..],
        };
    }

    /// <summary>Writes a client hello: the only frame that carries no outer length prefix.</summary>
    public static async ValueTask WriteClientHelloAsync(Stream stream, ClientHello hello, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hello);
        await stream.WriteAsync(hello.Encode(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one handshake frame prefixed by a 4-byte big-endian length.</summary>
    /// <remarks>
    /// The length prefix exists so the reader never has to guess a frame's size from its type, and is
    /// hard-capped at <see cref="MaxFrameBytes"/> before any allocation.
    /// </remarks>
    public static async ValueTask<byte[]> ReadLengthPrefixedAsync(Stream stream, CancellationToken ct)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        if (length is <= 0 or > MaxFrameBytes)
        {
            throw new TransportException(
                TransportErrorCode.Protocol,
                $"handshake frame length {length} is outside the permitted range 1..{MaxFrameBytes}");
        }

        var frame = new byte[length];
        await ReadExactlyAsync(stream, frame, ct).ConfigureAwait(false);
        return frame;
    }

    /// <summary>Writes one handshake frame prefixed by its 4-byte big-endian length.</summary>
    public static async ValueTask WriteLengthPrefixedAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (frame.Length is <= 0 or > MaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), $"handshake frame must be 1..{MaxFrameBytes} bytes");
        }

        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, frame.Length);
        await stream.WriteAsync(lengthBuffer, ct).ConfigureAwait(false);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void RequireMagic(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 || !frame[..4].SequenceEqual(Magic))
        {
            throw new TransportException(
                TransportErrorCode.HandshakeRequired,
                "peer did not begin with the _vht handshake magic (\"VHT1\"); refusing the session rather than downgrading it");
        }
    }

    private static void RequireVersion(ushort version)
    {
        if (version < MinVersion || version > Version)
        {
            throw new TransportException(
                TransportErrorCode.HandshakeRequired,
                $"peer protocol version {version} is outside the supported range {MinVersion}..{Version}");
        }
    }
}

/// <summary>Transport features negotiated between the two ends. Distinct from <see cref="TransportCapabilities"/>: this is the wire bitmask.</summary>
[Flags]
public enum FeatureFlags : uint
{
    /// <summary>Nothing beyond the baseline.</summary>
    None = 0,

    /// <summary>LZ4 block compression of bulk/interrupt payload frames.</summary>
    Lz4 = 1u << 0,

    /// <summary>Isochronous transfers supported by both ends (best-effort; ARCHITECTURE.md §10 risk 6).</summary>
    Isochronous = 1u << 1,

    /// <summary>Per-device passwords are meaningful on this session.</summary>
    DevicePassword = 1u << 2,

    /// <summary>Server-wide shared secret is meaningful on this session.</summary>
    SharedSecret = 1u << 3,
}
