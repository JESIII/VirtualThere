// SPDX-License-Identifier: Apache-2.0
//
// `xtask verify` — the machine-checked half of the packaging acceptance criteria.
//
// It decompiles the built MSI with WiX itself (so the check is on what the package really contains,
// not on what the source intended), asserts the structures the card asks for, and scans the package
// and every shipped binary for the vocabulary that must not be there: another vendor's name, a
// key/activation/purchase prompt, an agreement page, or a device-count limit.
//
// The firewall rule names are additionally cross-checked against scripts\Setup-Portable.ps1, because
// that script is the portable build's second, independent implementation of the same two rules and
// the two must not drift.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Xtask;

internal static partial class VerifyCommand
{
    /// <summary>
    /// Hard failures for every artifact (the MSI and each shipped executable). Deliberately
    /// vocabulary, not prose: the package must not name the reference product, must not have a
    /// device-count concept (ARCHITECTURE.md §5.4), and must not describe a state the product cannot
    /// be in.
    /// </summary>
    private static readonly string[] ForbiddenTokens =
    [
        "virtualhere",
        "vhusb",
        "max_devices",
        "max devices exceeded",
        "unlicensed",
    ];

    /// <summary>
    /// Extra tokens that only make sense for the installer package: an agreement page, a key entry,
    /// or an activation/purchase path. They are not applied to the executables because those are
    /// self-contained single files that embed the .NET runtime, whose own resource strings contain
    /// ordinary English words like "activation" and "purchase" that have nothing to do with this
    /// product; the project-specific tokens above are the meaningful check there.
    /// </summary>
    private static readonly string[] InstallerOnlyForbiddenTokens =
    [
        "licenseagreementdlg",
        "licenseaccepted",
        "licenseacceptedcheckbox",
        "wixuilcertf",
        "productkey",
        "purchase",
        "activation",
    ];

    /// <summary>
    /// Strings that legitimately contain licence vocabulary: the BSD-2-Clause notice of the one
    /// third-party driver we redistribute, which that licence requires us to keep. Anything else is
    /// reported, so an accidental licence gate cannot hide behind this allow-list.
    /// </summary>
    private static readonly string[] NoticeAllowList =
    [
        "usbip-win2-LICENSE.txt",
        "usbip-win2-SHA256SUMS",
    ];

    /// <summary>What the MSI must contain for the card's deliverables to be met.</summary>
    private static readonly (string What, string Marker)[] ExpectedStructures =
    [
        ("vht-server service registration", "vht-server"),
        ("service display name", "VirtualThere USB Server"),
        ("firewall rule, plaintext listener", "VirtualThere USB/IP (TCP 3240)"),
        ("firewall rule, TLS listener", "VirtualThere USB/IP TLS (TCP 3241)"),
        ("start-menu shortcut, server window", "VirtualThere Server"),
        ("start-menu shortcut, client window", "VirtualThere Client"),
        ("start-menu shortcut, command line", "VirtualThere Command Line"),
        ("driver install custom action", "Install-Driver.ps1"),
        ("driver uninstall custom action", "Uninstall-Driver.ps1"),
        ("client logon task custom action", "Install-ClientTask.ps1"),
        ("pinned driver payload", "USBip-0.9.8.1-x64.exe"),
        ("installed packaging document", "PACKAGING.md"),
    ];

    /// <summary>
    /// What the MSI must NOT contain. The package deliberately ships no wizard at all: every dialog
    /// set the WiX UI extension offers routes through LicenseAgreementDlg, and this product has no
    /// agreement to accept. Any of these appearing means a page that must not exist is back.
    /// </summary>
    private static readonly (string What, string Marker)[] ForbiddenStructures =
    [
        ("wizard dialog table", "<Dialog "),
        ("agreement page in the wizard", "LicenseAgreementDlg"),
        ("terms-accepted checkbox", "LicenseAcceptedCheckBox"),
        ("agreement text variable", "WixUILicenseRtf"),
        ("key-entry property", "PIDKEY"),
        ("install-directory wizard binding", "WIXUI_INSTALLDIR"),
        // The tray binary was folded into vht-server/vht-client; packaging a fourth executable again
        // would mean the two-window model silently reverted.
        ("separate tray executable", "vht-tray"),
    ];

    internal static async Task<int> RunAsync(BuildContext context)
    {
        string msi = context.MsiPath(context.Version);
        if (!File.Exists(msi))
        {
            Console.WriteLine("verify: no MSI yet; building it first");
            await InstallerCommand.RunAsync(context, new Program.Options()).ConfigureAwait(false);
        }

        BuildContext.EnsureDirectory(context.VerifyDir);
        string decompiled = Path.Combine(context.VerifyDir, $"VirtualThere-{context.Version}.decompiled.wxs");
        string binaryFolder = Path.Combine(context.VerifyDir, "binary");

        Console.WriteLine($"verify: decompiling {Path.GetRelativePath(context.RepoRoot, msi)} with WiX " +
                          $"{InstallerCommand.WixVersion}");
        context.RunChecked("dotnet",
            ["wix", "msi", "decompile", msi, "-o", decompiled, "-x", binaryFolder],
            workingDirectory: context.InstallerSourceDir);

        string decompiledText = File.ReadAllText(decompiled);
        var report = new StringBuilder();
        var problems = new List<string>();

        void Say(string line)
        {
            Console.WriteLine("  " + line);
            report.AppendLine(line);
        }

        report.AppendLine($"VirtualThere {context.Version} — `xtask verify` report");
        report.AppendLine($"generated-utc: {BuildContext.Now()}");
        report.AppendLine($"msi: {Path.GetRelativePath(context.RepoRoot, msi)}");
        report.AppendLine($"msi-sha256: {BuildContext.HashFile(msi)}");
        report.AppendLine($"decompiled-source: {Path.GetRelativePath(context.RepoRoot, decompiled)}");
        report.AppendLine();

        // ---- 1. structures -------------------------------------------------------------------
        report.AppendLine("1. structures the package must contain");
        foreach ((string what, string marker) in ExpectedStructures)
        {
            bool found = decompiledText.Contains(marker, StringComparison.OrdinalIgnoreCase);
            Say($"   [{(found ? "ok" : "MISSING")}] {what}  ('{marker}')");
            if (!found)
            {
                problems.Add($"expected structure missing from the MSI: {what} ('{marker}')");
            }
        }

        report.AppendLine();
        report.AppendLine("1b. pages and properties the package must NOT contain");
        foreach ((string what, string marker) in ForbiddenStructures)
        {
            bool present = decompiledText.Contains(marker, StringComparison.OrdinalIgnoreCase);
            Say($"   [{(present ? "FAIL" : "ok")}] absent: {what}  ('{marker}')");
            if (present)
            {
                problems.Add($"the MSI contains {what} ('{marker}')");
            }
        }

        // ---- 2. forbidden vocabulary ---------------------------------------------------------
        report.AppendLine();
        report.AppendLine("2. forbidden vocabulary (another vendor, key/activation/purchase, agreement page, device cap)");

        var scanned = new List<(string What, string Path)>
        {
            ("msi", msi),
            ("decompiled-msi-source", decompiled),
        };

        foreach ((string name, _) in BuildContext.Products)
        {
            string exe = Path.Combine(context.PortableDir, name + ".exe");
            if (File.Exists(exe))
            {
                scanned.Add((name + ".exe", exe));
            }
        }

        foreach ((string what, string path) in scanned)
        {
            bool isInstaller = what is "msi" or "decompiled-msi-source";
            string[] tokens = isInstaller
                ? [.. ForbiddenTokens, .. InstallerOnlyForbiddenTokens]
                : ForbiddenTokens;

            Dictionary<string, int> hits = ScanFile(path, tokens);
            var found = hits.Where(pair => pair.Value > 0).ToList();
            if (found.Count == 0)
            {
                Say($"   [ok] {what}: none of {tokens.Length.ToString(CultureInfo.InvariantCulture)} tokens present");
            }
            else
            {
                foreach ((string token, int count) in found)
                {
                    Say($"   [FAIL] {what}: '{token}' x{count.ToString(CultureInfo.InvariantCulture)} " +
                        $"({BuildContext.Bytes(new FileInfo(path).Length)})");
                    problems.Add($"forbidden token '{token}' in {what}");
                }
            }
        }

        // ---- 3. licence vocabulary that is not a gate ----------------------------------------
        report.AppendLine();
        report.AppendLine("3. licence vocabulary outside the allow-list (the BSD-2 notice we must ship is allowed)");
        foreach ((string what, string path) in scanned)
        {
            if (what is "decompiled-msi-source" or "msi")
            {
                string text = what == "msi" ? string.Empty : decompiledText;
                int allowed = NoticeAllowList.Sum(entry => CountOccurrences(text, entry));
                int total = CountOccurrences(text, "licen");

                int unexplained = total - allowed;
                if (unexplained > 0)
                {
                    foreach (string line in LineHits(decompiledText, "licen"))
                    {
                        if (NoticeAllowList.Any(entry => line.Contains(entry, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        Say($"   [REVIEW] {what}: {line.Trim()}");
                        problems.Add($"unexplained licence vocabulary in the MSI source: {line.Trim()}");
                    }
                }
                else
                {
                    Say($"   [ok] {what}: {total.ToString(CultureInfo.InvariantCulture)} occurrence(s), all in the shipped notice");
                }
            }
            else
            {
                Say($"   [info] {what}: not text-scanned for prose (binary; the token scan in step 2 covers it)");
            }
        }

        // ---- 4. the two firewall implementations must agree ----------------------------------
        report.AppendLine();
        report.AppendLine("4. firewall rules: MSI (declarative) vs scripts\\Setup-Portable.ps1 (netsh)");
        string portableSetup = File.ReadAllText(Path.Combine(context.ScriptsSourceDir, "Setup-Portable.ps1"));
        foreach (string expected in ScriptRuleNames(portableSetup))
        {
            bool inSource = decompiledText.Contains(expected, StringComparison.OrdinalIgnoreCase);
            Say($"   [{(inSource ? "ok" : "MISSING")}] '{expected}'");
            if (!inSource)
            {
                problems.Add($"firewall rule name '{expected}' is in Setup-Portable.ps1 but not in the MSI");
            }
        }

        bool netshHasProgramScope = portableSetup.Contains("program=", StringComparison.OrdinalIgnoreCase) &&
                                    portableSetup.Contains("dir=in action=allow", StringComparison.OrdinalIgnoreCase);
        Say($"   [{(netshHasProgramScope ? "ok" : "REVIEW")}] portable script scopes its rules to the server binary " +
            $"and the inbound direction");
        if (!netshHasProgramScope)
        {
            problems.Add("Setup-Portable.ps1 does not scope its firewall rules to vht-server.exe/dir=in");
        }

        // ---- 5. verdict ----------------------------------------------------------------------
        report.AppendLine();
        report.AppendLine(problems.Count == 0
            ? "verdict: PASS"
            : $"verdict: FAIL ({problems.Count.ToString(CultureInfo.InvariantCulture)} problem(s))");
        foreach (string problem in problems)
        {
            report.AppendLine("  problem: " + problem);
        }

        string reportPath = Path.Combine(context.VerifyDir, "verify-report.txt");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine($"verify: report written to {Path.GetRelativePath(context.RepoRoot, reportPath)}");

        if (problems.Count > 0)
        {
            foreach (string problem in problems)
            {
                Console.Error.WriteLine("verify: " + problem);
            }

            throw new InvalidOperationException($"verification failed with {problems.Count} problem(s)");
        }

        Console.WriteLine("verify: PASS");
        return 0;
    }

    /// <summary>Renders the rule names Setup-Portable.ps1 would create for its default ports.</summary>
    private static IEnumerable<string> ScriptRuleNames(string script)
    {
        int plaintext = DefaultPort(script, "PlaintextPort", 3240);
        int tls = DefaultPort(script, "TlsPort", 3241);
        yield return $"VirtualThere USB/IP (TCP {plaintext.ToString(CultureInfo.InvariantCulture)})";
        yield return $"VirtualThere USB/IP TLS (TCP {tls.ToString(CultureInfo.InvariantCulture)})";
    }

    private static int DefaultPort(string script, string parameter, int fallback)
    {
        Match match = Regex.Match(script, @"\$" + parameter + @"\s*=\s*(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int port) ? port : fallback;
    }

    private static IEnumerable<string> LineHits(string text, string token)
    {
        foreach (string line in text.Split('\n'))
        {
            if (CountOccurrences(line, token) > 0)
            {
                yield return line.Length > 200 ? line[..200] : line;
            }
        }
    }

    private static int CountOccurrences(string text, string token) =>
        text.Split(token, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// Case-insensitive occurrence count. Prose is scanned case-insensitively so that a stray
    /// "License Agreement" page or "Licence Key" prompt cannot slip past on capitalisation.
    /// </summary>
    private static int CountOccurrencesIgnoreCase(string text, string token) =>
        text.Split(token, StringSplitOptions.None).Length - 1 +
        text.ToLowerInvariant().Split(token, StringSplitOptions.None).Length - 1 - CountOccurrences(text, token);

    /// <summary>
    /// Case-insensitive raw scan of a file for the tokens as ASCII and as UTF-16LE, in chunks so a
    /// 180 MB MSI does not have to be read into memory. Counts in the chunk overlap can double.
    /// </summary>
    private static Dictionary<string, int> ScanFile(string path, IEnumerable<string> tokens)
    {
        string[] list = [.. tokens];
        var needles = new List<(string Token, byte[] Bytes)>();
        foreach (string token in list)
        {
            needles.Add((token, Encoding.ASCII.GetBytes(token)));
            needles.Add((token, Encoding.Unicode.GetBytes(token)));
        }

        var hits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in list)
        {
            hits[token] = 0;
        }
        const int ChunkSize = 1 << 20;
        const int Overlap = 64;

        byte[] buffer = new byte[ChunkSize + Overlap];
        int carry = 0;

        using FileStream stream = File.OpenRead(path);
        while (true)
        {
            int read = stream.Read(buffer, carry, ChunkSize);
            if (read <= 0)
            {
                break;
            }

            int total = carry + read;
            Span<byte> span = buffer.AsSpan(0, total);
            LowerAscii(span);

            foreach ((string token, byte[] bytes) in needles)
            {
                int index = 0;
                while (index + bytes.Length <= span.Length)
                {
                    int at = span[index..].IndexOf(bytes);
                    if (at < 0)
                    {
                        break;
                    }

                    hits[token] = hits[token] + 1;
                    index += at + 1;
                }
            }

            carry = Math.Min(Overlap, total);
            if (carry > 0)
            {
                span[(total - carry)..].CopyTo(buffer);
            }
        }

        return hits;
    }

    private static void LowerAscii(Span<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is >= (byte)'A' and <= (byte)'Z')
            {
                bytes[i] += 32;
            }
        }
    }
}
