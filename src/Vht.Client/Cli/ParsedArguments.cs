// SPDX-License-Identifier: Apache-2.0
//
// Argument parsing for vht-client. Hand-rolled on purpose: the surface is a handful of verbs, and a
// dependency-free parser keeps the release build free of a CLI package whose licence would have to be
// audited for third_party/notices/THIRD_PARTY_NOTICES (ARCHITECTURE.md App. D).

namespace Vht.Client.Cli;

/// <summary>The command line, parsed. Verbs come first, options anywhere.</summary>
internal sealed class ParsedArguments
{
    private ParsedArguments(string verb)
    {
        Verb = verb;
    }

    internal string Verb { get; private set; } = string.Empty;

    internal List<string> Positional { get; } = new();

    internal string? ConfigPath { get; private set; }

    internal string? PeersFile { get; private set; }

    internal int? BridgePort { get; private set; }

    internal bool UseTls { get; private set; }

    internal bool All { get; private set; }

    internal bool Verbose { get; private set; }

    internal bool Quiet { get; private set; }

    internal bool LogToFile { get; private set; }

    internal bool MeasureCapacity { get; private set; }

    internal bool ShowHelp { get; private set; }

    internal static ParsedArguments Parse(string[] args)
    {
        var result = new ParsedArguments(string.Empty);
        bool verbSeen = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h" or "--help":
                    result.ShowHelp = true;
                    continue;
                case "-v" or "--verbose":
                    result.Verbose = true;
                    continue;
                case "-q" or "--quiet":
                    result.Quiet = true;
                    continue;
                case "--log":
                    result.LogToFile = true;
                    continue;
                case "--tls":
                    result.UseTls = true;
                    continue;
                case "--all":
                    result.All = true;
                    continue;
                case "--measure-capacity":
                    result.MeasureCapacity = true;
                    continue;
                case "--config":
                    result.ConfigPath = Next(args, ref i, "--config");
                    continue;
                case "--peers":
                    result.PeersFile = Next(args, ref i, "--peers");
                    continue;
                case "--port":
                    string portText = Next(args, ref i, "--port");
                    if (!int.TryParse(portText, out int port) || port is < 0 or > 65535)
                    {
                        throw new ArgumentException($"--port expects a port number in 0..65535, got '{portText}'");
                    }
                    result.BridgePort = port;
                    continue;
            }

            if (arg.StartsWith('-'))
            {
                throw new ArgumentException($"unknown option '{arg}'. Try --help.");
            }

            if (!verbSeen)
            {
                result.Verb = arg;
                verbSeen = true;
            }
            else
            {
                result.Positional.Add(arg);
            }
        }

        if (!verbSeen && !result.ShowHelp)
        {
            result.ShowHelp = true;
        }

        return result;
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} needs a value");
        }
        return args[++index];
    }
}
