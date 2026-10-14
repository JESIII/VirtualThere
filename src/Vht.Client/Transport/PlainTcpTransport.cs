// SPDX-License-Identifier: Apache-2.0
//
// The agreed stub transport: plain TCP to the peer. No TLS, no LZ4 — those belong to Vht.Transport
// (card transport-workstream) and plug into IClientTransportFactory without any change here.

using System.Net.Sockets;

namespace Vht.Client.Transport;

/// <summary>A plain TCP transport. <see cref="TransportCapabilities.None"/>, exactly as Appendix C requires.</summary>
internal sealed class PlainTcpTransport : IClientTransport
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    internal PlainTcpTransport(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        Kind = TransportKind.PlainTcp;
        Capabilities = TransportCapabilities.None;
    }

    public TransportKind Kind { get; }

    public TransportCapabilities Capabilities { get; }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _stream.ReadAsync(buffer, ct);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        // NetworkStream does not serialise concurrent writes; the bridge writes on one task only, but
        // the op client and the handshake can overlap, so the lock is cheap insurance.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Fails closed. Plaintext USB/IP has no authentication mechanism at all (ARCHITECTURE.md §6.3,
    /// Appendix C), so any attempt to authenticate over it must be refused rather than ignored —
    /// otherwise a password-protected export would be reachable by simply asking for it.
    /// </summary>
    public Task<ClientAuthResult> AuthenticateAsync(ClientAuthRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ClientAuthResult.Failed(
            "plaintext transport has no auth mechanism; a password-protected device requires the TLS listener"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Disposal races with peer teardown by design; nothing useful to report.
        }
        _socket.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>
/// Connects <see cref="PlainTcpTransport"/> instances. Refuses a peer that asks for TLS, rather than
/// silently downgrading it — the §6.1 downgrade control, enforced at the one place a connection is made.
/// </summary>
internal sealed class PlainTcpTransportFactory : IClientTransportFactory
{
    internal static readonly PlainTcpTransportFactory Instance = new();

    public string Description => "plain TCP (no TLS/LZ4: Vht.Transport is not wired into the client yet)";

    public async Task<IClientTransport> ConnectAsync(Peer peer, CancellationToken ct)
    {
        if (peer.UseTls)
        {
            throw new ClientTransportException(
                $"peer '{peer.Name}' is configured with use_tls = true, but the only transport compiled in is " +
                "plain TCP. Refusing to downgrade. Set " +
                $"peer '{peer.Name}' to plaintext explicitly, or wait for the TLS transport to be wired in " +
                "(see docs/TRANSPORT.md).");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(peer.Host, peer.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new PlainTcpTransport(socket);
    }
}
