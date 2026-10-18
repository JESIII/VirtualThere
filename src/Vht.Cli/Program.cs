using Vht.Core;
using Vht.Core.Logging;
using Vht.Core.Server;
using Vht.Interop;
using Vht.Interop.Services;
using Vht.Server.Config;
using Vht.Server.Hosting;

namespace Vht.Cli;

/// <summary>
/// <c>vht</c>: the operator's command line for VirtualThere.
/// </summary>
/// <remarks>
/// <para>
/// Every verb that talks to a running server, or to the service control manager, is the same
/// code the service binary runs — <see cref="ServerCommands"/> — so the two binaries cannot
/// drift. This file is argument parsing and help text only.
/// </para>
/// <para>
/// Licences do not appear here because there is no licence concept anywhere in the product: no
/// verb is gated, no verb is paid, and no device count is compared against anything. That is
/// visible as the absence of the vocabulary, not as a bypass (ARCHITECTURE.md §5.4).
/// </para>
/// <para>
/// The client verbs (<c>attach</c>, <c>detach</c>, peers, auto-attach) belong to the client
/// card's own binary (<c>Vht.Client</c>) and are deliberately not shadowed here.
/// </para>
/// </remarks>
public static class Program
{
    private const string DefaultServiceName = ServiceControl.DefaultServiceName;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "help";
        string[] rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

        if (HasFlag(rest, "--help") || HasFlag(rest, "-h") || verb is "help" or "--help")
        {
            PrintHelp(Console.Out);
            return 0;
        }

        TextWriter output = Console.Out;
        string? pipe = FlagValue(rest, "--pipe");
        string serviceName = FlagValue(rest, "--service") ?? DefaultServiceName;

        switch (verb)
        {
            case "version":
                output.WriteLine(VhtVersion.Banner);
                return 0;

            // Serving is the service binary's job: one implementation of the runtime, no fork.
            // The verb path is synchronous, so the window never opens from here.
            case "serve":
            case "run":
                return Vht.Server.Program.Main(["serve", .. rest]);

            case "status":
                // The log line follows the configuration the caller named, not the environment default.
                return ServerCommands.Status(output, pipe, FlagValue(rest, "--config"));

            case "list":
                return ServerCommands.List(output, HasFlag(rest, "--local"), pipe, LocalOptions(rest));

            case "kick":
                return Positional(rest, 0) is { Length: > 0 } busId
                    ? ServerCommands.Kick(output, busId, pipe)
                    : Missing(output, "kick", "<busid>");

            case "ignore":
                return Positional(rest, 0) is { Length: > 0 } ignoreRule
                    ? ServerCommands.Ignore(output, ignoreRule, pipe)
                    : Missing(output, "ignore", "<vvvv:pppp[:serial][@server]>");

            case "unignore":
                return Positional(rest, 0) is { Length: > 0 } unignoreRule
                    ? ServerCommands.Unignore(output, unignoreRule, pipe)
                    : Missing(output, "unignore", "<vvvv:pppp[:serial][@server]>");

            case "nickname":
                return Positional(rest, 0) is { Length: > 0 } nickBusId
                    ? ServerCommands.Nickname(output, nickBusId, FlagValue(rest, "--name") ?? string.Empty, pipe)
                    : Missing(output, "nickname", "<busid> --name <text>");

            case "password":
                return Positional(rest, 0) is { Length: > 0 } passBusId
                    ? ServerCommands.Password(output, passBusId, FlagValue(rest, "--password") ?? string.Empty, pipe)
                    : Missing(output, "password", "<busid> --password <text>  (empty clears)");

            case "stop-server":
                return ServerCommands.StopServer(output, pipe);

            case "install":
                return ServerCommands.Install(output, serviceName);

            case "uninstall":
                return ServerCommands.Uninstall(output, serviceName);

            case "start":
                return ServerCommands.Start(output, serviceName);

            case "stop-service":
                return ServerCommands.StopService(output, serviceName);

            case "service-status":
                return ServerCommands.ServiceStatus(output, serviceName);

            case "config-template":
                return ServerCommands.WriteConfigTemplate(output,
                    FlagValue(rest, "--out") ?? ServerConfigLoader.DefaultPath);

            default:
                output.WriteLine($"unknown verb '{verb}'");
                output.WriteLine();
                PrintHelp(output);
                return 64;
        }
    }

    private static ServerOptions LocalOptions(string[] args)
    {
        var options = new ServerOptions();

        if (FlagValue(args, "--capture-mode") is { Length: > 0 } mode)
            options.CaptureMode = mode;

        if (FlagValue(args, "--name") is { Length: > 0 } name)
            options.ServerName = name;

        if (HasFlag(args, "--rebind"))
            options.RebindEnabled = true;

        if (FlagValue(args, "--log-level") is { Length: > 0 } level &&
            Enum.TryParse(level, ignoreCase: true, out LogLevel parsed))
            options.LogLevel = parsed;

        // The TOML layer as well, so `vht list --local` sees the operator's ignore list.
        ServerConfigLoader.Apply(FlagValue(args, "--config") ?? ServerConfigLoader.DefaultPath, options,
            new StructuredLogger(LogLevel.Critical));

        return options;
    }

    private static int Missing(TextWriter output, string verb, string usage)
    {
        output.WriteLine($"usage: vht {verb} {usage}");
        return 64;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? FlagValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return a[(flag.Length + 1)..];
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }

        return null;
    }

    private static string? Positional(string[] args, int index)
    {
        var positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                // Skip the flag's value when it is given as a separate token.
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) i++;
                continue;
            }

            positionals.Add(args[i]);
        }

        return index < positionals.Count ? positionals[index] : null;
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine(VhtVersion.Banner);
        output.WriteLine();
        output.WriteLine("usage: vht <verb> [options]");
        output.WriteLine();
        output.WriteLine("serving");
        output.WriteLine("  serve                 run the USB/IP server in the foreground (the service");
        output.WriteLine("                        binary runs the same code under the SCM)");
        output.WriteLine();
        output.WriteLine("inspecting a running server");
        output.WriteLine("  status                server state: exports, attachments, listeners, device limit");
        output.WriteLine("  list [--local]        exported devices; --local enumerates without a server");
        output.WriteLine();
        output.WriteLine("per-device policy (all unconditional - nothing here is licensed or capped)");
        output.WriteLine("  kick <busid>                    force-detach the client holding a device");
        output.WriteLine("  ignore <vvvv:pppp[:serial][@server]>   hide a device from the export list");
        output.WriteLine("  unignore <vvvv:pppp[:serial][@server]> export it again");
        output.WriteLine("  nickname <busid> --name <text>          set a friendly name");
        output.WriteLine("  password <busid> --password <text>      per-device password (TLS listener only)");
        output.WriteLine();
        output.WriteLine("service lifecycle (needs an elevated shell for install/uninstall)");
        output.WriteLine("  install | uninstall | start | stop-service | service-status");
        output.WriteLine("  stop-server           ask a running server to shut down");
        output.WriteLine("  config-template [--out <path>]");
        output.WriteLine();
        output.WriteLine("common options: --pipe <name>  --service <name>  --config <path>");
        output.WriteLine("                --capture-mode auto|winusb-only|prebound-only  --rebind");
        output.WriteLine("                --log-level <level>");
        output.WriteLine();
        output.WriteLine("The client verbs (attach, detach, peers, auto-attach) live in the client binary.");
    }
}
