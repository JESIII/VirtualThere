using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Vht.Ui.MockService;

/// <summary>
/// Test double for the two control pipes. It speaks exactly the frozen vocabulary
/// (docs/ARCHITECTURE.md Appendix C) and nothing else, keeps state between requests, and
/// appends every request/response pair to a JSONL transcript.
///
/// Why it exists: the real <c>vht-server</c> and <c>vht-client</c> services are built by
/// sibling cards, so the UI must be development-testable and screenshot-able without them.
/// The transcript is the evidence that a given UI interaction really produced a given verb.
///
/// This is harness code and is never shipped: it lives under src/Vht.Ui/harness/ and is not
/// referenced by the tray project.
/// </summary>
internal static class Program
{
    private static string _transcriptPath = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        var options = MockOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(MockOptions.HelpText);
            return 0;
        }

        _transcriptPath = options.TranscriptPath;
        var state = new MockState(options.Scenario);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var serverPipe = new PipeHost(options.ServerPipe, "vht-server", state, isServer: true);
        var clientPipe = new PipeHost(options.ClientPipe, "vht-client", state, isServer: false);

        Task serverTask = serverPipe.RunAsync(cts.Token);
        Task clientTask = clientPipe.RunAsync(cts.Token);

        Console.WriteLine("READY server=" + options.ServerPipe + " client=" + options.ClientPipe);
        Console.Out.Flush();

        try
        {
            await Task.WhenAll(serverTask, clientTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        return 0;
    }

    internal static void Record(string pipe, string role, JsonObject request, JsonObject response)
    {
        if (string.IsNullOrEmpty(_transcriptPath))
        {
            return;
        }

        var entry = new JsonObject
        {
            ["timestamp"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["pipe"] = pipe,
            ["role"] = role,
            ["request"] = request.DeepClone(),
            ["response"] = response.DeepClone(),
        };

        try
        {
            File.AppendAllText(_transcriptPath, entry.ToJsonString() + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException)
        {
            // The transcript is diagnostic only; never fail a request because of it.
        }
    }
}

internal sealed class MockOptions
{
    public string ServerPipe { get; private set; } = "vht-server";

    public string ClientPipe { get; private set; } = "vht-client";

    public string TranscriptPath { get; private set; } = string.Empty;

    public string Scenario { get; private set; } = "default";

    public bool ShowHelp { get; private set; }

    public static MockOptions Parse(IReadOnlyList<string> args)
    {
        var options = new MockOptions();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--server-pipe":
                    options.ServerPipe = args[++i];
                    break;
                case "--client-pipe":
                    options.ClientPipe = args[++i];
                    break;
                case "--transcript":
                    options.TranscriptPath = args[++i];
                    break;
                case "--scenario":
                    options.Scenario = args[++i];
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    public static string HelpText =>
        "vht-mock-control-plane - VirtualThere control-plane test double\n" +
        "\n" +
        "  --server-pipe <name>   default: vht-server\n" +
        "  --client-pipe <name>   default: vht-client\n" +
        "  --transcript <path>    JSONL log of every request/response pair\n" +
        "  --scenario <name>      default | minimal\n";
}

/// <summary>One listening pipe: accepts a connection, reads one request line, answers one line.</summary>
internal sealed class PipeHost
{
    private readonly string _pipeName;
    private readonly string _role;
    private readonly MockState _state;
    private readonly bool _isServer;

    public PipeHost(string pipeName, string role, MockState state, bool isServer)
    {
        _pipeName = pipeName;
        _role = role;
        _state = state;
        _isServer = isServer;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client disconnected mid-exchange; accept the next one.
            }
        }
    }

    private async Task HandleAsync(Stream pipe, CancellationToken cancellationToken)
    {
        string? line = await ReadLineAsync(pipe, cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            return;
        }

        JsonObject response = _state.Handle(line, _isServer);

        var request = new JsonObject();
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? new JsonObject { ["raw"] = line };
        }
        catch (System.Text.Json.JsonException)
        {
            request = new JsonObject { ["raw"] = line };
        }

        Program.Record(_pipeName, _role, request, response);

        byte[] payload = Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
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
