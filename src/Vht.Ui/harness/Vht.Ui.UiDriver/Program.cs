using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Vht.Ui.UiDriver;

/// <summary>
/// Drives the tray application the way a user would: through UI Automation, on the real
/// windows, over a real control pipe served by the mock service. Produces screenshots and a
/// step log.
///
/// This is the honest form of the acceptance claim "the server and client can be fully driven
/// from the UI": every action below is an automation invocation on a control in the shipped
/// window, and the service-side evidence is the mock's request transcript.
/// </summary>
internal static class Program
{
    private static readonly List<JsonObject> Steps = new();

    /// <summary>PID of the window process currently under test, for dialog diagnostics.</summary>
    private static int _activeWindowPid;

    private static async System.Threading.Tasks.Task<int> Main(string[] args)
    {
        DriverOptions options = DriverOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);

        string transcript = Path.Combine(options.OutputDirectory, "mock-transcript.jsonl");
        string driverLog = Path.Combine(options.OutputDirectory, "driver-log.json");
        File.Delete(transcript);

        Process? mock = StartProcess(
            options.MockExecutable,
            $"--server-pipe {options.ServerPipe} --client-pipe {options.ClientPipe} --transcript \"{transcript}\"",
            waitForReadyLine: true);

        if (mock is null)
        {
            Console.Error.WriteLine("mock service failed to start");
            return 2;
        }

        Process? serverWindowProcess = null;
        Process? clientWindowProcess = null;
        Process? standAloneWindow = null;

        try
        {
            // ---------------- server mode ----------------
            // Each role is its own executable now: no --mode argument, and the window is what the
            // process starts with. The mock serves the pipe, so the window attaches to it.
            serverWindowProcess = StartProcess(options.ServerExecutable, $"--pipe {options.ServerPipe}", waitForReadyLine: false);
            _activeWindowPid = serverWindowProcess?.Id ?? 0;
            // Matched on the product name only: a window restored at the Win32 level reports its
            // caption to UI Automation, while a freshly created one reports the automation name
            // from XAML ("VirtualThere Server window"). Both contain this prefix.
            AutomationElement serverWindow = WaitForWindow("VirtualThere Server", TimeSpan.FromSeconds(25), _activeWindowPid);
            Step("server.window", true, "found the server window");

            AutomationElement? statusBefore = FindById(serverWindow, "server-state", TimeSpan.FromSeconds(5));
            Step("server.state.initial", statusBefore is not null, ReadText(statusBefore));

            Invoke(serverWindow, "start-server", "server.start");
            WaitFor(() => ReadText(FindById(serverWindow, "server-state"))!.Contains("Running", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8), "server reports Running");
            Step("server.start", true, ReadText(FindById(serverWindow, "server-state")));

            WaitFor(() => FindById(serverWindow, "kick:1-4") is not null, TimeSpan.FromSeconds(8), "device rows appear");
            Invoke(serverWindow, "nickname:1-5", "devices.nickname (label printer)");
            CompleteTextDialog("Device nickname", "Front-desk label printer");

            Invoke(serverWindow, "ignore:1-10", "devices.ignore (USB-to-serial adapter)");
            WaitFor(() => ReadText(FindById(serverWindow, "ignore:1-10"))!.Contains("Unignore", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8), "row now offers Unignore");

            Invoke(serverWindow, "password:1-6", "devices.password (conference microphone)");
            CompleteSecretDialog("Device password", options.DemoPassword);

            Invoke(serverWindow, "kick:1-4", "devices.kick (barcode scanner)");

            WaitFor(() => true, TimeSpan.FromSeconds(1), "settle");
            Capture(serverWindow, Path.Combine(options.OutputDirectory, "server-devices.png"), "server-devices.png");

            Invoke(serverWindow, "toggle-log", "show log viewer");
            WaitFor(() => true, TimeSpan.FromSeconds(2), "log refresh");
            Capture(serverWindow, Path.Combine(options.OutputDirectory, "server-log.png"), "server-log.png");
            Invoke(serverWindow, "toggle-log", "hide log viewer");

            // Window visibility is checked on the real HWND: the WPF automation element leaves the
            // UIA tree entirely while the window sits in the notification area.
            nint serverHandle = Handle(serverWindow);
            Invoke(serverWindow, "minimize", "minimize to tray");
            WaitFor(() => !NativeMethods.IsWindowVisible(serverHandle), TimeSpan.FromSeconds(5), "window hidden by minimize-to-tray");
            bool hidden = !NativeMethods.IsWindowVisible(serverHandle);
            Step("server.minimize-to-tray", hidden, hidden ? "window hidden; tray icon and process still alive" : "window still visible");

            // Restore the way the notification-area icon does (Show + foreground), then re-acquire
            // the window element: the pre-minimize reference is detached from the automation tree.
            NativeMethods.ShowWindow(serverHandle, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(serverHandle);
            serverWindow = WaitForWindow("VirtualThere Server", TimeSpan.FromSeconds(15), _activeWindowPid);
            WaitFor(() => NativeMethods.IsWindowVisible(serverHandle), TimeSpan.FromSeconds(5), "server window visible again after restore");
            Step("server.restore-from-tray", NativeMethods.IsWindowVisible(serverHandle), ReadText(FindById(serverWindow, "server-state", TimeSpan.FromSeconds(5))));

            // ---------------- client mode ----------------
            clientWindowProcess = StartProcess(options.ClientExecutable, $"--pipe {options.ClientPipe}", waitForReadyLine: false);
            _activeWindowPid = clientWindowProcess?.Id ?? 0;
            AutomationElement clientWindow = WaitForWindow("VirtualThere Client", TimeSpan.FromSeconds(25), _activeWindowPid);
            Step("client.window", true, "found the client window");

            WaitFor(() => FindById(clientWindow, "toggle:workshop-pi:1-3") is not null, TimeSpan.FromSeconds(10), "peer devices appear");

            Invoke(clientWindow, "toggle:workshop-pi:1-3", "clients.attach (industrial camera)");
            WaitFor(() => ReadText(FindById(clientWindow, "toggle:workshop-pi:1-3"))!.Contains("Detach", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8), "camera attached");

            Invoke(clientWindow, "toggle:workshop-pi:1-4", "clients.attach (GPS receiver)");
            WaitFor(() => ReadText(FindById(clientWindow, "toggle:workshop-pi:1-4"))!.Contains("Detach", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8), "GPS receiver attached (two devices attached)");

            ToggleCheck(clientWindow, "auto:workshop-pi:1-4", "clients.attach auto=true (auto-attach grant)");

            Invoke(clientWindow, "toggle:workshop-pi:1-4", "clients.detach (GPS receiver)");
            WaitFor(() => ReadText(FindById(clientWindow, "toggle:workshop-pi:1-4"))!.Contains("Attach", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8), "GPS receiver detached");

            Invoke(clientWindow, "toggle:workshop-pi:1-4", "clients.attach (GPS receiver, re-attach)");
            WaitFor(() => ReadText(FindById(clientWindow, "toggle:workshop-pi:1-4"))!.Contains("Detach", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8), "GPS receiver attached again");

            Invoke(clientWindow, "add-server", "peers.add (manual server)");
            CompleteTextDialog("Add a server", options.DemoPeer);

            WaitFor(() => true, TimeSpan.FromSeconds(2), "settle");
            Capture(clientWindow, Path.Combine(options.OutputDirectory, "client-devices.png"), "client-devices.png");

            Invoke(clientWindow, "toggle-log", "show log viewer");
            WaitFor(() => true, TimeSpan.FromSeconds(2), "log refresh");
            Capture(clientWindow, Path.Combine(options.OutputDirectory, "client-log.png"), "client-log.png");

            // ---------------- stand-alone pass: the window starts its own worker ----------------
            if (!options.SkipWorkerPass)
            {
                WorkerPass(options, ref standAloneWindow);
            }

            Step("driver.complete", true, "all steps executed");
        }
        catch (Exception ex)
        {
            Step("driver.failed", false, ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine(ex);
        }
        finally
        {
            WriteDriverLog(driverLog);
            WriteVerbSummary(transcript, Path.Combine(options.OutputDirectory, "verb-summary.json"));

            if (!options.KeepOpen)
            {
                foreach (Process? process in new[] { serverWindowProcess, clientWindowProcess, standAloneWindow, mock })
                {
                    TryKill(process);
                }
            }
        }

        int failures = Steps.Count(s => !(s["ok"]?.GetValue<bool>() ?? false));
        Console.WriteLine("steps=" + Steps.Count + " failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ stand-alone pass

    /// <summary>
    /// Starts the real server executable with nothing serving its pipe, presses Start, closes the
    /// window, restores it and then Exits - measuring what the card claims: that the window brings
    /// its own privileged worker up (exactly one extra process), that no console window exists for
    /// either process, that closing the window hides it and leaves the worker serving (docs/UI.md
    /// section 1), and that Exit is the one path that stops the worker, leaving nothing behind.
    /// </summary>
    /// <remarks>
    /// The worker is started with <c>--no-elevate</c> because nothing here can answer a consent
    /// prompt. That changes who authorises the process, not what is measured: the same executable,
    /// the same worker switch, the same supervisor and the same shutdown path. The elevated run
    /// differs only in the one prompt the user clicks.
    /// </remarks>
    private static void WorkerPass(DriverOptions options, ref Process? standAloneWindow)
    {
        var baseline = ProcessIds(ServerImageName(options.ServerExecutable));

        standAloneWindow = StartProcess(
            options.ServerExecutable,
            $"--pipe {options.WorkerPipe} --port {options.WorkerPort} --no-elevate",
            waitForReadyLine: false);
        _activeWindowPid = standAloneWindow?.Id ?? 0;

        AutomationElement window = WaitForWindow("VirtualThere Server", TimeSpan.FromSeconds(25), _activeWindowPid);
        Step("standalone.window", true,
            "the server executable opened its window with no command-line verb");

        // A console window would be a top-level window of class ConsoleWindowClass owned by this
        // process. Enumerate the process's windows and say what is there, rather than asserting.
        Step("standalone.no-console-window", !HasConsoleWindow(_activeWindowPid), DescribeProcessWindows(_activeWindowPid));

        Invoke(window, "start-server", "server.start (starts the worker the window owns)");
        WaitFor(
            () => ReadText(FindById(window, "server-state")) is { } text && text.Contains("Running", StringComparison.Ordinal),
            TimeSpan.FromSeconds(45),
            "the worker bound the control pipe and the server reports Running");
        Step("standalone.worker-running", true, ReadText(FindById(window, "server-state")));

        // The device list comes from the real server, over the real pipe: rows mean the window is
        // attached to the process it started, not to the mock.
        WaitFor(() => FindById(window, "kick:1-4") is not null || HasDeviceRows(window),
            TimeSpan.FromSeconds(20),
            "the device list populated from the real server");
        Capture(window, Path.Combine(options.OutputDirectory, "server-worker.png"), "server-worker.png");

        WaitFor(() => true, TimeSpan.FromSeconds(2), "settle before measuring the worker");
        int[] withWindow = ProcessIds(ServerImageName(options.ServerExecutable));
        int[] extra = withWindow.Where(id => id != _activeWindowPid && !baseline.Contains(id)).ToArray();
        Step("standalone.worker-spawned", extra.Length == 1,
            "process ids named " + ServerImageName(options.ServerExecutable) + ": [" +
            string.Join(", ", withWindow) + "], window pid " + _activeWindowPid +
            ", worker pid(s) [" + string.Join(", ", extra) + "]");

        // ---- closing the window: hide to the notification area, worker keeps serving ----
        // (docs/UI.md section 1, and the binding decision on this card: only Exit stops the worker.)
        int workerPid = extra.Length > 0 ? extra[0] : 0;
        nint standaloneHandle = Handle(window);
        NativeMethods.PostMessage(standaloneHandle, NativeMethods.WmClose, 0, 0);
        WaitFor(() => !NativeMethods.IsWindowVisible(standaloneHandle), TimeSpan.FromSeconds(10),
            "the window hid itself instead of closing");
        WaitFor(() => true, TimeSpan.FromSeconds(2), "settle after the close");

        bool windowStillAlive = standAloneWindow is { HasExited: false };
        bool workerStillAlive = workerPid != 0 && IsProcessRunning(workerPid);
        Step("standalone.close-hides-and-worker-survives",
            !NativeMethods.IsWindowVisible(standaloneHandle) && windowStillAlive && workerStillAlive,
            "window hidden=" + !NativeMethods.IsWindowVisible(standaloneHandle) +
            ", window process alive=" + windowStillAlive +
            ", worker pid " + workerPid + " alive=" + workerStillAlive);

        // Restore the way the notification-area icon does, then re-acquire the window element: a
        // hidden WPF window leaves the automation tree.
        NativeMethods.ShowWindow(standaloneHandle, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(standaloneHandle);
        window = WaitForWindow("VirtualThere Server", TimeSpan.FromSeconds(15), _activeWindowPid);
        Step("standalone.restored-after-close", NativeMethods.IsWindowVisible(standaloneHandle),
            ReadText(FindById(window, "server-state", TimeSpan.FromSeconds(5))));

        Invoke(window, "exit", "exit the window (the worker it started must go with it)");

        bool windowExited = standAloneWindow is not null && standAloneWindow.WaitForExit(30000);
        Step("standalone.window-exited", windowExited, "window pid " + _activeWindowPid + " exited=" + windowExited);

        // Exit is the one path that stops the worker: the supervisor stops the worker it owns
        // (graceful verb, then the process). A worker whose window died keeps serving - measured
        // above with the close step, which left it running.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        int[] after = ProcessIds(ServerImageName(options.ServerExecutable));
        while (DateTime.UtcNow < deadline && after.Any(id => id != _activeWindowPid && !baseline.Contains(id)))
        {
            Thread.Sleep(500);
            after = ProcessIds(ServerImageName(options.ServerExecutable));
        }

        int[] leftover = after.Where(id => id != _activeWindowPid && !baseline.Contains(id)).ToArray();
        Step("standalone.no-orphan-worker", leftover.Length == 0,
            "after exit, process ids named " + ServerImageName(options.ServerExecutable) + ": [" +
            string.Join(", ", after) + "]; orphans [" + string.Join(", ", leftover) + "]");
    }

    /// <summary>The image name of an executable path, without its directory or extension.</summary>
    private static string ServerImageName(string executablePath) =>
        Path.GetFileNameWithoutExtension(executablePath);

    /// <summary>True while a process with that id is alive.</summary>
    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Process ids of every running process with that image name.</summary>
    private static int[] ProcessIds(string imageName)
    {
        var ids = new System.Collections.Generic.List<int>();
        foreach (Process process in Process.GetProcessesByName(imageName))
        {
            try
            {
                ids.Add(process.Id);
            }
            catch (InvalidOperationException)
            {
                // Exited between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }

        return ids.ToArray();
    }

    /// <summary>
    /// True when the process owns a window of class <c>ConsoleWindowClass</c> - i.e. when a console
    /// window really exists for it. The measurement, not the project property.
    /// </summary>
    private static bool HasConsoleWindow(int pid)
    {
        bool found = false;
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid == (uint)pid &&
                    string.Equals(NativeMethods.GetClassNameOf(hwnd), "ConsoleWindowClass", StringComparison.Ordinal))
                {
                    found = true;
                    return false;
                }

                return true;
            },
            nint.Zero);

        return found;
    }

    private static bool HasDeviceRows(AutomationElement window)
    {
        try
        {
            AutomationElementCollection rows = window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
            if (rows.Count > 0)
            {
                return true;
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Window rebuilding; the caller retries.
        }

        return false;
    }

    // ------------------------------------------------------------------ steps

    private static void Step(string name, bool ok, string? detail)
    {
        Steps.Add(new JsonObject
        {
            ["step"] = name,
            ["ok"] = ok,
            ["detail"] = detail,
        });
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name + " - " + detail);
    }

    /// <summary>
    /// True for the failures UI Automation throws at a driver rather than at an application: the
    /// element went away mid-call, or a cross-process call timed out because the desktop was busy
    /// or the application was inside a modal dialog. All of them are retried, never fatal.
    /// </summary>
    private static bool IsTransient(Exception ex) =>
        ex is ElementNotAvailableException or COMException;

    /// <summary>Fetches a UIA pattern, treating a transient bridge failure as "not available".</summary>
    private static bool TryPattern(
        AutomationElement element,
        AutomationPattern pattern,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? result)
    {
        try
        {
            return element.TryGetCurrentPattern(pattern, out result);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            result = null;
            return false;
        }
    }

    private static void Invoke(AutomationElement root, string automationId, string what)
    {
        AutomationElement? element = WaitForEnabled(root, automationId, TimeSpan.FromSeconds(10));
        if (element is null)
        {
            Step(what, false, "control '" + automationId + "' not found or never became enabled");
            return;
        }

        if (!TryPattern(element, InvokePattern.Pattern, out object? pattern))
        {
            Step(what, false, "control '" + automationId + "' does not support Invoke");
            return;
        }

        try
        {
            ((InvokePattern)pattern).Invoke();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Step(what, false, "invoking '" + automationId + "' failed: " + ex.Message);
            return;
        }

        Step(what, true, "invoked automation id " + automationId);
        Thread.Sleep(450);
    }

    /// <summary>
    /// Waits for a control to exist and be enabled: the toolbar is disabled until the first
    /// poll answers, which is itself part of the behaviour under test.
    /// </summary>
    private static AutomationElement? WaitForEnabled(AutomationElement root, string automationId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? element = FindById(root, automationId, TimeSpan.FromSeconds(1));
            if (element is not null)
            {
                try
                {
                    if (element.Current.IsEnabled)
                    {
                        return element;
                    }
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    // Window is rebuilding; retry.
                }
            }

            Thread.Sleep(200);
        }

        return null;
    }

    private static void ToggleCheck(AutomationElement root, string automationId, string what)
    {
        AutomationElement? element = FindById(root, automationId, TimeSpan.FromSeconds(6));
        if (element is null)
        {
            Step(what, false, "control '" + automationId + "' not found");
            return;
        }

        if (!TryPattern(element, TogglePattern.Pattern, out object? pattern))
        {
            Step(what, false, "control '" + automationId + "' does not support Toggle");
            return;
        }

        try
        {
            ((TogglePattern)pattern).Toggle();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Step(what, false, "toggling '" + automationId + "' failed: " + ex.Message);
            return;
        }

        Step(what, true, "toggled automation id " + automationId);
        Thread.Sleep(600);
    }

    private static void CompleteTextDialog(string title, string value)
    {
        AutomationElement? dialog = WaitForDialog(title, TimeSpan.FromSeconds(8), _activeWindowPid);
        if (dialog is null)
        {
            Step("dialog:" + title, false, "dialog did not appear");
            return;
        }

        AutomationElement? box = FindById(dialog, "Value", TimeSpan.FromSeconds(4));
        if (box is null || !box.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern))
        {
            Step("dialog:" + title, false, "value box not found");
            return;
        }

        ((ValuePattern)pattern).SetValue(value);

        AutomationElement? ok = FindById(dialog, "OK", TimeSpan.FromSeconds(3));
        if (ok is not null && ok.TryGetCurrentPattern(InvokePattern.Pattern, out object? okPattern))
        {
            ((InvokePattern)okPattern).Invoke();
            Step("dialog:" + title, true, "typed '" + value + "' and confirmed");
        }
        else
        {
            Step("dialog:" + title, false, "OK button not found");
        }

        Thread.Sleep(700);
    }

    private static void CompleteSecretDialog(string title, string secret)
    {
        AutomationElement? dialog = WaitForDialog(title, TimeSpan.FromSeconds(8), _activeWindowPid);
        if (dialog is null)
        {
            Step("dialog:" + title, false, "dialog did not appear");
            return;
        }

        Focus(dialog);

        // A password box deliberately does not expose a value pattern, so the driver types
        // into the focused control the same way a user would.
        AutomationElement? box = FindById(dialog, "Device password", TimeSpan.FromSeconds(4));
        if (box is not null)
        {
            box.SetFocus();
            Thread.Sleep(250);
            SendKeys.SendWait(EscapeSendKeys(secret));
            Thread.Sleep(250);
        }

        AutomationElement? ok = FindById(dialog, "OK", TimeSpan.FromSeconds(3));
        if (ok is not null && ok.TryGetCurrentPattern(InvokePattern.Pattern, out object? okPattern))
        {
            ((InvokePattern)okPattern).Invoke();
            Step("dialog:" + title, true, "typed password and confirmed");
        }
        else
        {
            Step("dialog:" + title, false, "OK button not found");
        }

        Thread.Sleep(700);
    }

    private static string EscapeSendKeys(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(c switch
            {
                '+' or '^' or '%' or '~' or '(' or ')' or '[' or ']' or '{' or '}' => "{" + c + "}",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------ UIA helpers

    private static AutomationElement WaitForWindow(string name, TimeSpan timeout, int pidHint = 0)
    {
        var seen = new System.Collections.Generic.List<string>();
        AutomationElement? found = WaitForElement(
            () =>
            {
                seen.Clear();
                AutomationElementCollection windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    Condition.TrueCondition);

                foreach (AutomationElement candidate in windows)
                {
                    string? candidateName;
                    int candidatePid;
                    try
                    {
                        candidateName = candidate.Current.Name;
                        candidatePid = candidate.Current.ProcessId;
                    }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        continue;
                    }

                    if (pidHint != 0 && candidatePid == pidHint)
                    {
                        seen.Add("'" + candidateName + "' class='" + candidate.Current.ClassName + "'");
                    }

                    if (candidateName is null)
                    {
                        continue;
                    }

                    if (candidateName.Equals(name, StringComparison.Ordinal) ||
                        candidateName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }

                return null;
            },
            timeout);

        if (found is null)
        {
            // A tray process that failed to start its mode shows a Win32 message box (#32770)
            // instead of its window. Read that text, otherwise the failure says nothing.
            string messageBox = DescribeMessageBox(pidHint);
            Step(
                "window:" + name,
                false,
                "not found; windows of the tray process seen live: " + string.Join(" | ", seen) +
                "; top-level windows at failure: " + string.Join(" | ", TopLevelWindowNames()) +
                "; message box: " + messageBox);
            throw new InvalidOperationException("window '" + name + "' did not appear; message box: " + messageBox);
        }

        return found;
    }

    /// <summary>
    /// Finds a modal prompt. The desktop-child UIA enumeration does not always surface a WPF
    /// dialog window, so this falls back to enumerating top-level HWNDs of the tray process and
    /// wrapping the handle with <see cref="AutomationElement.FromHandle"/> - the controls inside
    /// are still driven through UIA patterns, which is what the accessibility claim rests on.
    /// </summary>
    private static AutomationElement? WaitForDialog(string title, TimeSpan timeout, int pid)
    {
        // HWND enumeration first, deliberately. It is two cheap Win32 calls in this process,
        // whereas a desktop-wide UIA scan is a cross-process call that also touches every other
        // window on the desktop - including the tray window whose UI thread is blocked inside
        // ShowDialog at this very moment, which stalls the scan. The dialog's controls are still
        // driven through real UIA patterns; only the lookup of the top-level window differs.
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            nint handle = FindTopLevelWindow(pid, title);
            if (handle != 0)
            {
                Step("dialog:" + title + ".handle", true, "window found by HWND enumeration; driving it through UIA");
                return AutomationElement.FromHandle(handle);
            }

            Thread.Sleep(150);
        }

        // Last resort: the dialog may belong to a different process than the tray.
        foreach (AutomationElement candidate in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                if (candidate.Current.Name.Contains(title, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // Window closed while enumerating.
            }
        }

        Step(
            "dialog:" + title,
            false,
            "not found by UIA name or HWND; tray process top-level windows: " + DescribeProcessWindows(pid));
        return null;
    }

    private static nint FindTopLevelWindow(int pid, string title)
    {
        nint result = 0;
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid != (uint)pid)
                {
                    return true;
                }

                string text = NativeMethods.GetWindowTextOf(hwnd);
                if (text.Contains(title, StringComparison.OrdinalIgnoreCase))
                {
                    result = hwnd;
                    return false;
                }

                return true;
            },
            nint.Zero);

        return result;
    }

    private static string DescribeProcessWindows(int pid)
    {
        var described = new System.Collections.Generic.List<string>();
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid == (uint)pid)
                {
                    described.Add(
                        "'" + NativeMethods.GetWindowTextOf(hwnd) + "' class='" +
                        NativeMethods.GetClassNameOf(hwnd) + "' visible=" + NativeMethods.IsWindowVisible(hwnd));
                }

                return true;
            },
            nint.Zero);

        return described.Count == 0 ? "(none)" : string.Join(" | ", described);
    }

    /// <summary>
    /// Text of the Win32 message box (class <c>#32770</c>) owned by the tray process, if any. A
    /// tray process that threw while starting a mode shows one instead of its window, so without
    /// its text a "window did not appear" failure says nothing about the cause.
    /// </summary>
    private static string DescribeMessageBox(int pid)
    {
        var boxes = new System.Collections.Generic.List<string>();
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid != (uint)pid ||
                    !string.Equals(NativeMethods.GetClassNameOf(hwnd), "#32770", StringComparison.Ordinal))
                {
                    return true;
                }

                boxes.Add("'" + NativeMethods.GetWindowTextOf(hwnd) + "': " +
                          string.Join(" / ", ChildWindowTexts(hwnd)));
                return true;
            },
            nint.Zero);

        return boxes.Count == 0 ? "(none)" : string.Join(" | ", boxes);
    }

    private static System.Collections.Generic.List<string> ChildWindowTexts(nint parent)
    {
        var texts = new System.Collections.Generic.List<string>();
        NativeMethods.EnumChildWindows(
            parent,
            (hwnd, _) =>
            {
                string text = NativeMethods.GetWindowTextOf(hwnd);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text);
                }

                return true;
            },
            nint.Zero);

        return texts;
    }

    private static System.Collections.Generic.List<string> TopLevelWindowNames()
    {
        var names = new System.Collections.Generic.List<string>();
        foreach (AutomationElement candidate in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                string name = candidate.Current.Name;
                names.Add(
                    "'" + name + "' class='" + candidate.Current.ClassName +
                    "' pid=" + candidate.Current.ProcessId + " offscreen=" + candidate.Current.IsOffscreen);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // Ignore.
            }
        }

        return names;
    }

    private static AutomationElement? FindById(AutomationElement root, string automationId, TimeSpan timeout = default)
    {
        if (timeout == TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(3);
        }

        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        return WaitForElement(() => root.FindFirst(TreeScope.Descendants, condition), timeout);
    }

    private static AutomationElement? WaitForElement(Func<AutomationElement?> finder, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                AutomationElement? element = finder();
                if (element is not null)
                {
                    return element;
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // Window recreated; retry.
            }

            Thread.Sleep(200);
        }

        return null;
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout, string what)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (IsTransient(ex) || ex is InvalidOperationException)
            {
                // Retry while the UI settles.
            }

            Thread.Sleep(200);
        }

        Step("wait:" + what, false, "timed out after " + timeout.TotalSeconds + "s");
    }

    private static string? ReadText(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePattern))
            {
                return ((ValuePattern)valuePattern).Current.Value;
            }

            return element.Current.Name;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return null;
        }
    }

    private static nint Handle(AutomationElement element)
    {
        try
        {
            return (nint)element.Current.NativeWindowHandle;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return 0;
        }
    }

    private static bool IsVisible(AutomationElement element)
    {
        try
        {
            return !element.Current.IsOffscreen;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return false;
        }
    }

    private static void Show(AutomationElement element)
    {
        nint handle = (nint)element.Current.NativeWindowHandle;
        if (handle != 0)
        {
            NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(handle);
        }

        Thread.Sleep(600);
    }

    private static void Focus(AutomationElement element)
    {
        try
        {
            nint handle = (nint)element.Current.NativeWindowHandle;
            if (handle != 0)
            {
                NativeMethods.SetForegroundWindow(handle);
            }

            element.SetFocus();
            Thread.Sleep(300);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Nothing to do; the following steps will report the failure.
        }
    }

    // ------------------------------------------------------------------ screenshots

    private static void Capture(AutomationElement window, string path, string label)
    {
        try
        {
            nint handle = (nint)window.Current.NativeWindowHandle;
            if (handle == 0)
            {
                Step("screenshot:" + label, false, "window has no handle");
                return;
            }

            NativeMethods.SetForegroundWindow(handle);
            Thread.Sleep(700);

            if (!NativeMethods.GetWindowRect(handle, out NativeMethods.Rect rect))
            {
                Step("screenshot:" + label, false, "GetWindowRect failed");
                return;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            bitmap.Save(path, ImageFormat.Png);
            bool blank = IsUniform(bitmap);
            Step("screenshot:" + label, !blank, path + " (" + width + "x" + height + (blank ? ", appears blank - no interactive desktop?" : "") + ")");
        }
        catch (Exception ex)
        {
            Step("screenshot:" + label, false, ex.Message);
        }
    }

    private static bool IsUniform(Bitmap bitmap)
    {
        Color first = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        for (int y = 4; y < bitmap.Height; y += 17)
        {
            for (int x = 4; x < bitmap.Width; x += 17)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R != first.R || pixel.G != first.G || pixel.B != first.B)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ process helpers

    private static Process? StartProcess(string executable, string arguments, bool waitForReadyLine)
    {
        var info = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process? process = Process.Start(info);
        if (process is null)
        {
            return null;
        }

        if (waitForReadyLine)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                string? line = process.StandardOutput.ReadLine();
                if (line is null)
                {
                    break;
                }

                Console.WriteLine("mock: " + line);
                if (line.StartsWith("READY", StringComparison.Ordinal))
                {
                    return process;
                }
            }

            return process;
        }

        return process;
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    // ------------------------------------------------------------------ reporting

    private static void WriteDriverLog(string path)
    {
        var log = new JsonObject
        {
            ["steps"] = new JsonArray(Steps.Select(s => s.DeepClone()).ToArray()),
            ["failures"] = Steps.Count(s => !(s["ok"]?.GetValue<bool>() ?? false)),
        };

        File.WriteAllText(path, log.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine("driver log: " + path);
    }

    private static void WriteVerbSummary(string transcriptPath, string outputPath)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var details = new JsonArray();

        if (File.Exists(transcriptPath))
        {
            foreach (string line in File.ReadAllLines(transcriptPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonNode? node = JsonNode.Parse(line);
                string verb = node?["request"]?["verb"]?.GetValue<string>() ?? "unknown";
                counts[verb] = counts.TryGetValue(verb, out int current) ? current + 1 : 1;

                details.Add(new JsonObject
                {
                    ["verb"] = verb,
                    ["pipe"] = node?["pipe"]?.GetValue<string>(),
                    ["ok"] = node?["response"]?["ok"]?.GetValue<bool>() ?? false,
                    ["request"] = node?["request"]?.DeepClone(),
                });
            }
        }

        var summary = new JsonObject
        {
            ["verbCounts"] = new JsonObject(counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
            ["totalRequests"] = details.Count,
            ["requests"] = details,
        };

        File.WriteAllText(outputPath, summary.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine("verb summary: " + outputPath);
        foreach (KeyValuePair<string, int> pair in counts)
        {
            Console.WriteLine("  " + pair.Key + " x" + pair.Value);
        }
    }
}

internal sealed class DriverOptions
{
    /// <summary>The server executable under test: <c>vht-server.exe</c>.</summary>
    public string ServerExecutable { get; private set; } = string.Empty;

    /// <summary>The client executable under test: <c>vht-client.exe</c>.</summary>
    public string ClientExecutable { get; private set; } = string.Empty;

    public string MockExecutable { get; private set; } = string.Empty;

    public string OutputDirectory { get; private set; } = string.Empty;

    public string ServerPipe { get; private set; } = "vht-ui-driver-server";

    public string ClientPipe { get; private set; } = "vht-ui-driver-client";

    /// <summary>
    /// Pipe for the stand-alone pass: nothing serves it, so the window has to start its own worker.
    /// </summary>
    public string WorkerPipe { get; private set; } = "vht-ui-driver-worker";

    /// <summary>Plaintext listener port handed to that worker (not the shipped default).</summary>
    public int WorkerPort { get; private set; } = 33240;

    /// <summary>
    /// True to skip the stand-alone pass. The pass starts a real worker process and exits the
    /// window, which is what the orphan assertion measures, so it is on by default.
    /// </summary>
    public bool SkipWorkerPass { get; private set; }

    public string DemoPassword { get; private set; } = "device-secret";

    public string DemoPeer { get; private set; } = "10.0.0.9:3241";

    public bool KeepOpen { get; private set; }

    public static DriverOptions Parse(IReadOnlyList<string> args)
    {
        var options = new DriverOptions();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--server":
                    options.ServerExecutable = args[++i];
                    break;
                case "--client":
                    options.ClientExecutable = args[++i];
                    break;
                case "--mock":
                    options.MockExecutable = args[++i];
                    break;
                case "--out":
                    options.OutputDirectory = args[++i];
                    break;
                case "--server-pipe":
                    options.ServerPipe = args[++i];
                    break;
                case "--client-pipe":
                    options.ClientPipe = args[++i];
                    break;
                case "--worker-pipe":
                    options.WorkerPipe = args[++i];
                    break;
                case "--worker-port":
                    options.WorkerPort = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--skip-worker-pass":
                    options.SkipWorkerPass = true;
                    break;
                case "--password":
                    options.DemoPassword = args[++i];
                    break;
                case "--peer":
                    options.DemoPeer = args[++i];
                    break;
                case "--keep-open":
                    options.KeepOpen = true;
                    break;
            }
        }

        if (string.IsNullOrEmpty(options.ServerExecutable) || string.IsNullOrEmpty(options.ClientExecutable) ||
            string.IsNullOrEmpty(options.MockExecutable) || string.IsNullOrEmpty(options.OutputDirectory))
        {
            throw new ArgumentException("--server, --client, --mock and --out are required.");
        }

        return options;
    }
}

internal static partial class NativeMethods
{
    internal const int SwRestore = 9;

    /// <summary>WM_CLOSE: what the window's X button sends. The window decides what it means.</summary>
    internal const uint WmClose = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out Rect rect);

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(nint hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(nint hWnd, System.Text.StringBuilder className, int maxCount);

    internal static string GetWindowTextOf(nint hWnd)
    {
        var buffer = new System.Text.StringBuilder(512);
        GetWindowTextW(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static string GetClassNameOf(nint hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        GetClassNameW(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
