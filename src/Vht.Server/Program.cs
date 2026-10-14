using Vht.Core;
using Vht.Core.Logging;
using Vht.Core.Server;
using Vht.Interop;
using Vht.Interop.Services;
using Vht.Server.Config;
using Vht.Server.Hosting;
using Vht.Server.Logging;

namespace Vht.Server;

/// <summary>
/// <c>vht-server</c>: the USB/IP server, as a Windows service or as a console application.
/// </summary>
/// <remarks>
/// The SCM starts the binary with a single argument, <c>serve</c> (see
/// <see cref="WindowsServiceManager.Install"/>), so every runtime setting has to come from the
/// configuration file rather than the command line — which is why
/// <see cref="ServerConfigLoader"/> exists. When the process was not launched by the SCM the
/// same <c>serve</c> verb runs it in the foreground, which is what the acceptance test uses.
/// </remarks>
public static class Program
{
    private const string ServiceName = ServiceControl.DefaultServiceName;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
        var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

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
                return await ServeAsync(options, configPath, rest).ConfigureAwait(false);

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

    private static async Task<int> ServeAsync(ServerOptions options, string? configPath, string[] args)
    {
        if (HasFlag(args, "--template") && configPath is not null)
            ServerCommands.WriteConfigTemplate(Console.Out, configPath);

        string path = configPath ?? ServerConfigLoader.DefaultPath;
        var logger = BuildLogger(options);

        IReadOnlyList<string> configProblems = ServerConfigLoader.Apply(path, options, logger);
        foreach (string problem in configProblems)
            logger.Write(LogLevel.Warn, LogEvent.ConfigurationLoaded, $"configuration: {problem}");

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

        // Console mode.
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
        output.WriteLine("usage: vht-server <verb> [options]");
        output.WriteLine();
        output.WriteLine("  serve                 run the server (default); as a service under the SCM,");
        output.WriteLine("                        otherwise in the foreground");
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
        output.WriteLine("serve options: --config <path> --port <n> --capture-mode auto|winusb-only|prebound-only");
        output.WriteLine("               --rebind --log-level <level> --log-file <path> --name <text>");
    }
}
