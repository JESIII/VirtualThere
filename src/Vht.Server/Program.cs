using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Vht.Core;
using Vht.Core.Logging;
using Vht.Core.Server;
using Vht.Interop;
using Vht.Interop.Services;
using Vht.Server.Config;
using Vht.Server.Hosting;
using Vht.Server.Logging;
using Vht.Ui.Hosting;

namespace Vht.Server;

/// <summary>
/// <c>vht-server</c>: one visible executable that opens the VirtualThere Server window, plus the
/// USB/IP server that window manages and the console verbs that address it.
/// </summary>
/// <remarks>
/// <para>
/// Three ways in, decided by the command line:
/// </para>
/// <list type="number">
/// <item>
/// <c>--worker</c> - the privileged half, started by the window (elevated and hidden). It runs the
/// server runtime in the foreground, binds the control pipe, and keeps serving until it is asked to
/// stop: the window's Exit, or <c>vht-server stop-server</c> over the pipe. It does **not** follow
/// the window it came from - a worker whose window is gone keeps serving and is adopted by the next
/// launch of the window (<see cref="WorkerLifetime"/>). No window, no console.
/// </item>
/// <item>
/// An explicit verb - <c>vht-server serve</c>, <c>status</c>, <c>list</c>, <c>install</c>, ... The
/// verb set is unchanged, and this is the form the service control manager uses
/// (<see cref="WindowsServiceManager.Install"/> registers the binary with <c>serve</c>).
/// </item>
/// <item>
/// Anything else, including no arguments - the management window (docs/UI.md section 1).
/// </item>
/// </list>
/// <para>
/// The SCM starts the binary with a single argument, <c>serve</c>, so every runtime setting has to
/// come from the configuration file rather than the command line - which is why
/// <see cref="ServerConfigLoader"/> exists. When the process was not launched by the SCM the same
/// <c>serve</c> verb runs it in the foreground, which is what the acceptance test uses.
/// </para>
/// </remarks>
public static class Program
{
    private const string ServiceName = ServiceControl.DefaultServiceName;

    [STAThread]
    public static int Main(string[] args)
    {
        // The window's privileged half: never a window, never a console.
        if (UiHost.IsWorkerInvocation(args))
        {
            return RunWorker(args);
        }

        string? verb = VerbOf(args);
        if (verb is not null)
        {
            return RunVerb(verb, args);
        }

        if (HasFlag(args, "--version"))
        {
            HostConsole.EnsureForVerbs();
            Console.WriteLine(VhtVersion.Banner);
            return 0;
        }

        if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
        {
            // With a console (a shell, or a redirect) the usage goes there; with none - a
            // double-click with --help - it is shown in a dialog rather than vanishing.
            if (!HostConsole.EnsureForVerbs())
            {
                return UiHost.Run(UiRole.Server, args);
            }

            PrintHelp(Console.Out);
            return 0;
        }

        // No verb: this is the server, so this is the server window.
        return UiHost.Run(UiRole.Server, args);
    }

    /// <summary>The verb, when one was given explicitly. No verb means "open the window".</summary>
    private static string? VerbOf(string[] args) =>
        args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;

    /// <summary>Runs the worker a window started: the server runtime, foreground, no window.</summary>
    private static int RunWorker(string[] args) => RunVerb("serve", WithoutWorkerFlag(args));

    /// <summary>Drops <c>--worker</c> before the verb parses its own options.</summary>
    private static string[] WithoutWorkerFlag(string[] args)
    {
        var kept = new List<string>(args.Length);
        foreach (string argument in args)
        {
            if (string.Equals(argument, UiHost.WorkerSwitch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(argument);
        }

        return kept.ToArray();
    }

    private static int RunVerb(string verb, string[] args)
    {
        HostConsole.EnsureForVerbs();

        string[] rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

        if (HasFlag(rest, "--help") || HasFlag(rest, "-h") || verb is "help")
        {
            PrintHelp(Console.Out);
            return 0;
        }

        if (verb is "version" || HasFlag(rest, "--version"))
        {
            Console.WriteLine(VhtVersion.Banner);
            return 0;
        }

        ServerOptions options = BuildOptions(rest, out string? configPath);

        switch (verb)
        {
            case "serve":
            case "run":
                return ServeAsync(options, configPath, rest).GetAwaiter().GetResult();

            case "config-template":
                return ServerCommands.WriteConfigTemplate(Console.Out,
                    FlagValue(rest, "--out") ?? ServerConfigLoader.DefaultPath);

            default:
                return Dispatch(Console.Out, verb, rest, options);
        }
    }

    private static int Dispatch(TextWriter output, string verb, string[] args, ServerOptions options)
    {
        string? pipe = FlagValue(args, "--pipe");

        switch (verb)
        {
            case "status":
                return ServerCommands.Status(output, pipe);
            case "list":
                return ServerCommands.List(output, HasFlag(args, "--local"), pipe, options);
            case "kick":
                return RequireBusId(output, args, busId => ServerCommands.Kick(output, busId, pipe));
            case "ignore":
                return RequireBusId(output, args, rule => ServerCommands.Ignore(output, rule, pipe));
            case "unignore":
                return RequireBusId(output, args, rule => ServerCommands.Unignore(output, rule, pipe));
            case "nickname":
                return RequireBusId(output, args,
                    busId => ServerCommands.Nickname(output, busId, FlagValue(args, "--name") ?? string.Empty, pipe));
            case "password":
                return RequireBusId(output, args,
                    busId => ServerCommands.Password(output, busId, FlagValue(args, "--password") ?? string.Empty, pipe));
            case "stop-server":
                return ServerCommands.StopServer(output, pipe);
            case "install":
                return ServerCommands.Install(output, FlagValue(args, "--service") ?? ServiceName);
            case "uninstall":
                return ServerCommands.Uninstall(output, FlagValue(args, "--service") ?? ServiceName);
            case "start":
                return ServerCommands.Start(output, FlagValue(args, "--service") ?? ServiceName);
            case "stop-service":
                return ServerCommands.StopService(output, FlagValue(args, "--service") ?? ServiceName);
            case "service-status":
                return ServerCommands.ServiceStatus(output, FlagValue(args, "--service") ?? ServiceName);

            default:
                output.WriteLine($"unknown verb '{verb}'");
                PrintHelp(output);
                return 64;
        }
    }

    private static int RequireBusId(TextWriter output, string[] args, Func<string, int> action)
    {
        string? value = Positional(args, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            output.WriteLine("this verb needs a device bus id (see 'list')");
            return 64;
        }

        return action(value);
    }

    /// <summary>
    /// Runs the server runtime: as a service when the SCM started this process, otherwise in the
    /// foreground - which is the form the window's worker takes, and the form the acceptance suite
    /// uses. Nothing here ties the runtime's lifetime to any other process: it stops when the SCM
    /// says so, when Ctrl+C is pressed, or when the control pipe receives <c>server.stop</c>.
    /// </summary>
    private static async Task<int> ServeAsync(
        ServerOptions options,
        string? configPath,
        string[] args)
    {
        if (HasFlag(args, "--template") && configPath is not null)
            ServerCommands.WriteConfigTemplate(Console.Out, configPath);

        string path = configPath ?? ServerConfigLoader.DefaultPath;
        var logger = BuildLogger(options);

        IReadOnlyList<string> configProblems = ServerConfigLoader.Apply(path, options, logger);
        foreach (string problem in configProblems)
            logger.Write(LogLevel.Warn, LogEvent.ConfigurationLoaded, $"configuration: {problem}");

        // --pipe applies after the configuration file so it wins: the window passes it to the worker
        // and both ends have to agree on one pipe even when the file names another.
        if (FlagValue(args, "--pipe") is { Length: > 0 } pipeName)
            options.ControlPlanePipeName = pipeName;

        logger.Write(LogLevel.Info, LogEvent.ConfigurationLoaded,
            configPath is null && !File.Exists(path)
                ? $"no configuration file at {path}; running with defaults"
                : $"configuration path: {path}",
            new Dictionary<string, object?> { ["path"] = path });

        await using ServerRuntime runtime = ServerRuntime.Build(options, logger);

        // Service mode: the SCM owns the lifetime, and stop/shutdown arrive as control codes.
        if (Win32ServiceHost.Run(ServiceName, token => runtime.RunAsync(token), out int error))
            return 0;

        if (error != Win32ServiceHost.ErrorFailedServiceControllerConnect)
        {
            logger.Write(LogLevel.Error, LogEvent.ServerStarting,
                $"StartServiceCtrlDispatcher failed (win32 {error}: " +
                $"{new System.ComponentModel.Win32Exception(error).Message})");
            return 1;
        }

        // Foreground mode: a console invocation, or the window's worker.
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        logger.Write(LogLevel.Info, LogEvent.ServerStarting,
            "running in the foreground (not launched by the service control manager); Ctrl+C to stop");

        return await runtime.RunAsync(cancel.Token).ConfigureAwait(false);
    }

    private static ILogger BuildLogger(ServerOptions options)
    {
        var logger = new StructuredLogger(options.LogLevel, new TextWriterLogSink(Console.Out));

        if (!string.IsNullOrWhiteSpace(options.LogFile))
        {
            try
            {
                var sink = new RollingFileLogSink(options.LogFile);
                logger.AddSink(sink);
            }
            catch (IOException ex)
            {
                logger.Write(LogLevel.Warn, LogEvent.ServerStarting,
                    $"could not open the log file {options.LogFile}: {ex.Message}");
            }
        }

        return logger;
    }

    private static ServerOptions BuildOptions(string[] args, out string? configPath)
    {
        configPath = FlagValue(args, "--config");
        var options = new ServerOptions();

        if (FlagValue(args, "--name") is { Length: > 0 } name)
            options.ServerName = name;

        if (FlagValue(args, "--port") is { Length: > 0 } port &&
            int.TryParse(port, out int parsedPort))
            options.PlaintextPort = parsedPort;

        if (HasFlag(args, "--rebind"))
            options.RebindEnabled = true;

        if (FlagValue(args, "--capture-mode") is { Length: > 0 } mode)
            options.CaptureMode = mode;

        if (HasFlag(args, "--quiet"))
            options.LogLevel = LogLevel.Info;

        if (FlagValue(args, "--log-level") is { Length: > 0 } level &&
            Enum.TryParse(level, ignoreCase: true, out LogLevel parsedLevel))
            options.LogLevel = parsedLevel;

        if (FlagValue(args, "--log-file") is { Length: > 0 } file)
            options.LogFile = file;

        return options;
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
        output.WriteLine("usage: vht-server [verb] [options]");
        output.WriteLine();
        output.WriteLine("  (no verb)             open the VirtualThere Server window");
        output.WriteLine();
        output.WriteLine("  serve                 run the server without a window: under the service control");
        output.WriteLine("                        manager as a service, otherwise in the foreground");
        output.WriteLine("  status                ask a running server for its state");
        output.WriteLine("  list [--local]        list exported devices (--local enumerates without a server)");
        output.WriteLine("  kick <busid>          force-detach the client holding a device");
        output.WriteLine("  ignore <vvvv:pppp>    stop exporting a device");
        output.WriteLine("  unignore <vvvv:pppp>  export it again");
        output.WriteLine("  nickname <busid> --name <text>");
        output.WriteLine("  password <busid> --password <text>   (empty clears; TLS listener only)");
        output.WriteLine("  stop-server           ask a running server to shut down");
        output.WriteLine("  install | uninstall | start | stop-service | service-status");
        output.WriteLine("  config-template [--out <path>]");
        output.WriteLine("  version");
        output.WriteLine();
        output.WriteLine("serve options: --config <path> --pipe <name> --port <n>");
        output.WriteLine("               --capture-mode auto|winusb-only|prebound-only");
        output.WriteLine("               --rebind --log-level <level> --log-file <path> --name <text>");
        output.WriteLine();
        output.WriteLine("window options: --pipe <name> --port <n> --minimized --no-elevate --help");
    }
}
