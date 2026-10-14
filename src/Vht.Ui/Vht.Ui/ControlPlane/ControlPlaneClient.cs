using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Vht.Ui.ControlPlane;

/// <summary>The UI's view of a service control pipe. Test doubles implement this.</summary>
public interface IControlPlane
{
    /// <summary>Pipe name, e.g. <c>vht-server</c>; used to resolve <c>\\.\pipe\vht-server</c>.</summary>
    string PipeName { get; }

    Task<ControlPlaneResponse> SendAsync(
        string verb,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Named-pipe JSON-lines client: one request line, one response line, one pipe instance per
/// exchange. That is the frozen shape (docs/ARCHITECTURE.md Appendix C) and it keeps the UI
/// free of any protocol, USB or driver knowledge. Every failure mode is returned as a
/// <see cref="ControlPlaneResponse"/> rather than thrown, because "the service is not
/// running" is a normal UI state, not an exception.
/// </summary>
public sealed class ControlPlaneClient : IControlPlane
{
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _exchangeTimeout;

    public ControlPlaneClient(
        string pipeName,
        TimeSpan? connectTimeout = null,
        TimeSpan? exchangeTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        }

        PipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(700);
        _exchangeTimeout = exchangeTimeout ?? TimeSpan.FromSeconds(5);
    }

    public string PipeName { get; }

    public static string ResolvePath(string pipeName) => @"\\.\pipe\" + pipeName;

    public async Task<ControlPlaneResponse> SendAsync(
        string verb,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(verb))
        {
            throw new ArgumentException("A verb is required.", nameof(verb));
        }

        string request = BuildRequest(verb, arguments);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_exchangeTimeout);

            await using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);

            await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);

            byte[] payload = Encoding.UTF8.GetBytes(request + "\n");
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            string? line = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (line is null)
            {
                return ControlPlaneResponse.Malformed(verb, "the service closed the pipe without a reply.");
            }

            return ControlPlaneResponse.Parse(line, verb);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ControlPlaneResponse.Unavailable(verb, "the service did not answer in time.");
        }
        catch (TimeoutException)
        {
            return ControlPlaneResponse.Unavailable(
                verb,
                "no listener on \\\\.\\pipe\\" + PipeName + " - is the service running?");
        }
        catch (IOException ex)
        {
            return ControlPlaneResponse.Unavailable(verb, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ControlPlaneResponse.Unavailable(verb, ex.Message);
        }
    }

    internal static string BuildRequest(string verb, IReadOnlyDictionary<string, object?>? arguments)
    {
        var obj = new JsonObject { ["verb"] = verb };
        if (arguments is not null)
        {
            foreach (KeyValuePair<string, object?> pair in arguments)
            {
                obj[pair.Key] = pair.Value switch
                {
                    null => null,
                    bool b => JsonValue.Create(b),
                    int i => JsonValue.Create(i),
                    long l => JsonValue.Create(l),
                    string s => JsonValue.Create(s),
                    JsonNode node => node.DeepClone(),
                    _ => JsonValue.Create(pair.Value.ToString()),
                };
            }
        }

        return obj.ToJsonString();
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var accumulated = new List<byte>(256);

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return accumulated.Count == 0 ? null : Encoding.UTF8.GetString(accumulated.ToArray());
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(accumulated.ToArray());
            }

            if (buffer[0] != (byte)'\r')
            {
                accumulated.Add(buffer[0]);
            }
        }
    }
}
