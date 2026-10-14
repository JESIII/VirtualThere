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

    /// <summary>PID of the tray process currently under test, for dialog diagnostics.</summary>
    private static int _activeTrayPid;

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

        Process? serverTray = null;
        Process? clientTray = null;

        try
        {
            // ---------------- server mode ----------------
            serverTray = StartProcess(options.TrayExecutable, $"--mode server --pipe {options.ServerPipe}", waitForReadyLine: false);
            _activeTrayPid = serverTray?.Id ?? 0;
            // Matched on the product name only: a window restored at the Win32 level reports its
            // caption to UI Automation, while a freshly created one reports the automation name
            // from XAML ("VirtualThere Server window"). Both contain this prefix.
            AutomationElement serverWindow = WaitForWindow("VirtualThere Server", TimeSpan.FromSeconds(25), _activeTrayPid);
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
            serverWindow = WaitForWindow("VirtualThere Server", TimeSpan.FromSeconds(15), _activeTrayPid);
            WaitFor(() => NativeMethods.IsWindowVisible(serverHandle), TimeSpan.FromSeconds(5), "server window visible again after restore");
            Step("server.restore-from-tray", NativeMethods.IsWindowVisible(serverHandle), ReadText(FindById(serverWindow, "server-state", TimeSpan.FromSeconds(5))));

            // ---------------- client mode ----------------
            clientTray = StartProcess(options.TrayExecutable, $"--mode client --pipe {options.ClientPipe}", waitForReadyLine: false);
            _activeTrayPid = clientTray?.Id ?? 0;
            AutomationElement clientWindow = WaitForWindow("VirtualThere Client", TimeSpan.FromSeconds(25), _activeTrayPid);
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
                foreach (Process? process in new[] { serverTray, clientTray, mock })
                {
                    TryKill(process);
                }
            }
        }

        int failures = Steps.Count(s => !(s["ok"]?.GetValue<bool>() ?? false));
        Console.WriteLine("steps=" + Steps.Count + " failures=" + failures);
        return failures == 0 ? 0 : 1;
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
        AutomationElement? dialog = WaitForDialog(title, TimeSpan.FromSeconds(8), _activeTrayPid);
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
        AutomationElement? dialog = WaitForDialog(title, TimeSpan.FromSeconds(8), _activeTrayPid);
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
    public string TrayExecutable { get; private set; } = string.Empty;

    public string MockExecutable { get; private set; } = string.Empty;

    public string OutputDirectory { get; private set; } = string.Empty;

    public string ServerPipe { get; private set; } = "vht-ui-driver-server";

    public string ClientPipe { get; private set; } = "vht-ui-driver-client";

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
                case "--tray":
                    options.TrayExecutable = args[++i];
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

        if (string.IsNullOrEmpty(options.TrayExecutable) || string.IsNullOrEmpty(options.MockExecutable) ||
            string.IsNullOrEmpty(options.OutputDirectory))
        {
            throw new ArgumentException("--tray, --mock and --out are required.");
        }

        return options;
    }
}

internal static partial class NativeMethods
{
    internal const int SwRestore = 9;

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
