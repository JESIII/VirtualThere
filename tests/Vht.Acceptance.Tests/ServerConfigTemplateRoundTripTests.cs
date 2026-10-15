using Vht.Core.Server;
using Vht.Server.Config;

namespace Vht.Acceptance.Tests;

/// <summary>
/// The configuration template the product writes must load back through the product's own loader.
/// </summary>
/// <remarks>
/// <para>
/// The defect this covers: <c>vht config-template</c> emitted <c>rebind.enabled = false</c> inside the
/// <c>[capture]</c> section, but the loader's line parser dropped the section for a <i>dotted</i>
/// left-hand side and kept the literal text, so the key arrived as <c>rebind.enabled</c> - which the
/// loader does not accept. Setting the documented key therefore produced an
/// <c>unknown configuration key 'rebind.enabled'</c> warning and silently did nothing, and the only
/// working route to the P2 rebind path was the <c>--rebind</c> switch. Nothing about the failure was
/// obvious to the operator, and nothing failed the build.
/// </para>
/// <para>
/// These tests are the guard that makes the class of bug impossible: the template is written exactly
/// as the product writes it (<see cref="ServerConfigLoader.Template"/>, the same string
/// <c>WriteConfigTemplate</c> puts on disk), loaded back through
/// <see cref="ServerConfigLoader.Apply(string, ServerOptions, Core.Logging.ILogger?)"/>, and required
/// to produce <b>zero</b> problems. Each key is then asserted on its own, so a key the parser cannot
/// resolve fails by name - including a key added to the template later, which is the whole point:
/// a template that does not load cleanly is a build failure.
/// </para>
/// </remarks>
public sealed class ServerConfigTemplateRoundTripTests
{
    /// <summary>
    /// Every section the template is expected to emit. A section that disappears from the template is
    /// a regression the round-trip test alone would not see (fewer keys can still mean zero problems),
    /// so the set is asserted explicitly.
    /// </summary>
    private static readonly string[] ExpectedSections =
    [
        "server",
        "transport.plaintext",
        "transport.tls",
        "capture",
        "policy",
        "connections",
        "logging",
        "control_plane",
        "hooks",
    ];

    [Fact]
    public void The_generated_template_loads_back_with_no_problems()
    {
        string path = WriteTemplate(out string directory);
        try
        {
            var options = new ServerOptions();
            IReadOnlyList<string> problems = ServerConfigLoader.Apply(path, options);

            Assert.Empty(problems);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Every_key_the_template_emits_is_a_key_the_loader_accepts()
    {
        string path = WriteTemplate(out string directory);
        try
        {
            var problems = new List<string>();
            Dictionary<string, string> keys = ServerConfigLoader.Parse(File.ReadAllLines(path), problems);

            // Vacuous passes must not be possible: the parser has to have found the template's keys.
            Assert.Empty(problems);
            Assert.True(keys.Count >= 20, $"the template parsed to only {keys.Count} key(s)");
            Assert.Contains("capture.rebind.enabled", keys);

            // Every section the template emits has at least one key, so the per-key loop below really
            // does walk all of them.
            foreach (string section in ExpectedSections)
            {
                Assert.Contains(keys.Keys, key =>
                    key.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase));
            }

            // Each key on its own: a dotted key at the top level is a fully-qualified key, so this feeds
            // the loader exactly the key the parser produced and proves the loader accepts it by name.
            foreach ((string key, string raw) in keys)
            {
                string single = Path.Combine(directory, "single.toml");
                File.WriteAllText(single, $"{key} = {raw}{Environment.NewLine}");

                var options = new ServerOptions();
                IReadOnlyList<string> singleProblems = ServerConfigLoader.Apply(single, options);

                Assert.True(singleProblems.Count == 0,
                    $"the template emits '{key}' but the loader rejects it: {string.Join("; ", singleProblems)}");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_template_reaches_the_capture_rebind_key()
    {
        string path = WriteTemplate(out string directory);
        try
        {
            // The template writes the key inside [capture] as `rebind.enabled`; turning it on the way the
            // template presents it must reach ServerOptions.RebindEnabled and raise no warning.
            string text = File.ReadAllText(path);
            Assert.Contains("rebind.enabled = false", text, StringComparison.Ordinal);
            File.WriteAllText(path, text.Replace("rebind.enabled = false", "rebind.enabled = true"));

            var options = new ServerOptions();
            IReadOnlyList<string> problems = ServerConfigLoader.Apply(path, options);

            Assert.Empty(problems);
            Assert.True(options.RebindEnabled,
                "capture.rebind.enabled = true in the generated template did not turn the rebind path on");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_dotted_key_is_resolved_relative_to_its_section()
    {
        // TOML semantics: `rebind.enabled` under `[capture]` is `capture.rebind.enabled`. Both the
        // relative spelling (what the template emits) and the fully-qualified one are accepted.
        var problems = new List<string>();
        Dictionary<string, string> parsed = ServerConfigLoader.Parse(
            ["[capture]", "rebind.enabled = true", "mode = \"auto\""], problems);

        Assert.Empty(problems);
        Assert.True(parsed.ContainsKey("capture.rebind.enabled"));
        Assert.True(parsed.ContainsKey("capture.mode"));

        string directory = NewDirectory("dotted");
        try
        {
            string relative = Path.Combine(directory, "relative.toml");
            File.WriteAllText(relative, $"[capture]{Environment.NewLine}rebind.enabled = true{Environment.NewLine}");

            string qualified = Path.Combine(directory, "qualified.toml");
            File.WriteAllText(qualified, $"capture.rebind.enabled = true{Environment.NewLine}");

            foreach (string path in new[] { relative, qualified })
            {
                var options = new ServerOptions();
                Assert.Empty(ServerConfigLoader.Apply(path, options));
                Assert.True(options.RebindEnabled, $"'{path}' did not set RebindEnabled");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WriteTemplate(out string directory)
    {
        directory = NewDirectory("template");
        string path = Path.Combine(directory, "server.toml");
        File.WriteAllText(path, ServerConfigLoader.Template);
        return path;
    }

    private static string NewDirectory(string label)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "vht-server-config-tests", label + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
