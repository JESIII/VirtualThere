using Vht.Core.Hooks;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Server;

namespace Vht.Core.Tests;

/// <summary>
/// The hook ABI: the vocabulary, the <c>$VAR$</c> substitutions, the exit-code contract and
/// the hardening rules (path allow-list, identity hooks refused for an unauthenticated peer).
/// </summary>
public sealed class HookTests
{
    private static HookRunner Runner(HookOptions options, FakeProcessRunner runner) =>
        new(options, runner, new StructuredLogger(LogLevel.Critical));

    private static HookContext Context(
        string clientId = "client-1",
        string clientIp = "10.0.0.5",
        bool authenticated = false,
        UsbDevice? device = null) =>
        new()
        {
            ClientId = clientId,
            ClientIp = clientIp,
            Nickname = "nick",
            NewNickname = "newnick",
            DevicePath = "/sys/devices/usb1/1-1",
            VendorId = "1234",
            ProductId = "5678",
            Device = device,
            PeerAuthenticated = authenticated,
        };

    private static FakeProcessRunner Yes() => new()
    {
        Outcome = new ProcessOutcome { Started = true, ExitCode = HookVocabulary.ExitYes },
    };

    // =====================================================================================
    // Vocabulary
    // =====================================================================================

    [Fact]
    public void EveryHookEventHasAUniqueNameThatRoundTrips()
    {
        HookEvent[] events = Enum.GetValues<HookEvent>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HookEvent hook in events)
        {
            string name = hook.Name();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(names.Add(name), $"duplicate hook name '{name}'");
            Assert.True(HookVocabulary.TryParse(name, out HookEvent parsed));
            Assert.Equal(hook, parsed);
        }

        Assert.Equal(17, events.Length);
    }

    [Fact]
    public void TheVocabularyCoversEveryNameFromTheRecon()
    {
        string[] expected =
        {
            "onBind", "onUnbind", "onEnumeration", "onReset", "onClearHalt", "onResetEp",
            "onDeviceUnplug", "onPassToKernel", "onChangeNickname", "OnClientConnect",
            "OnClientDisconnect", "OnDeviceKick", "OnDeviceIgnore", "OnDeviceUnignore",
            "OnServerRename", "ClientAuthorization", "ClientDeauthorization",
        };

        foreach (string name in expected)
        {
            Assert.True(HookVocabulary.TryParse(name, out _), $"'{name}' is not in the vocabulary");
        }
    }

    [Fact]
    public void HookNamesParseCaseInsensitively()
    {
        Assert.True(HookVocabulary.TryParse("ONBIND", out HookEvent hook));
        Assert.Equal(HookEvent.OnBind, hook);
    }

    [Fact]
    public void AnUnknownHookNameIsRejected()
    {
        Assert.False(HookVocabulary.TryParse("onTea", out _));
    }

    [Fact]
    public void OnlyIdentityDependentHooksRequireAnAuthenticatedPeer()
    {
        HookEvent[] gated =
        {
            HookEvent.ClientAuthorization,
            HookEvent.ClientDeauthorization,
            HookEvent.OnClientConnect,
            HookEvent.OnClientDisconnect,
        };

        foreach (HookEvent hook in Enum.GetValues<HookEvent>())
            Assert.Equal(gated.Contains(hook), hook.RequiresAuthenticatedPeer());
    }

    // =====================================================================================
    // Substitutions
    // =====================================================================================

    [Fact]
    public void EveryDocumentedSubstitutionIsApplied()
    {
        string script =
            "hook.cmd $CLIENT_ID$ $CLIENT_IP$ $NICKNAME$ $NEW_NICKNAME$ $DEVPATH$ $VENDOR_ID$ $PRODUCT_ID$";

        string result = HookRunner.Substitute(script, Context());

        Assert.Equal("hook.cmd client-1 10.0.0.5 nick newnick /sys/devices/usb1/1-1 1234 5678", result);
    }

    [Fact]
    public void AnUnknownPlaceholderIsLeftAlone()
    {
        Assert.Equal("hook.cmd $NOT_A_VARIABLE$", HookRunner.Substitute("hook.cmd $NOT_A_VARIABLE$", Context()));
    }

    // =====================================================================================
    // Exit-code ABI
    // =====================================================================================

    [Fact]
    public async Task ExitOneMeansAllow()
    {
        var options = new HookOptions().Set(HookEvent.OnBind, "hook.cmd");

        HookResult result = await Runner(options, Yes()).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.True(result.Allow);
        Assert.False(result.UnknownExitCode);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task ExitZeroMeansDeny()
    {
        var runner = new FakeProcessRunner { Outcome = new ProcessOutcome { Started = true, ExitCode = HookVocabulary.ExitNo } };
        var options = new HookOptions().Set(HookEvent.OnBind, "hook.cmd");

        HookResult result = await Runner(options, runner).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.False(result.Allow);
        Assert.False(result.UnknownExitCode);
    }

    [Fact]
    public async Task AnUnknownExitCodeIsLoggedAndTheEventIsAllowed()
    {
        var runner = new FakeProcessRunner { Outcome = new ProcessOutcome { Started = true, ExitCode = 42 } };
        var options = new HookOptions().Set(HookEvent.OnBind, "hook.cmd");

        HookResult result = await Runner(options, runner).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.True(result.Allow);
        Assert.True(result.UnknownExitCode);
        Assert.Equal(42, result.ExitCode);
        Assert.Contains("unexpected exit code 42", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHookThatNeverRanIsSkippedAndAllowed()
    {
        var runner = new FakeProcessRunner
        {
            Outcome = new ProcessOutcome { Started = false, FailureDetail = "no such interpreter" },
        };
        var options = new HookOptions().Set(HookEvent.OnBind, "hook.cmd");

        HookResult result = await Runner(options, runner).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.True(result.Allow);
        Assert.True(result.Skipped);
        Assert.Equal("no such interpreter", result.Detail);
    }

    [Fact]
    public async Task ATimedOutHookIsKilledSkippedAndAllowed()
    {
        var runner = new FakeProcessRunner
        {
            Outcome = new ProcessOutcome { Started = true, TimedOut = true, FailureDetail = "hook exceeded the 1s timeout" },
        };
        var options = new HookOptions { Timeout = TimeSpan.FromSeconds(1) };
        options.Set(HookEvent.OnBind, "hook.cmd");

        HookResult result = await Runner(options, runner).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.True(result.Allow);
        Assert.True(result.Skipped);
        Assert.Contains("timeout", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventWithNoScriptIsNotConfiguredAndCostsNothing()
    {
        var runner = new FakeProcessRunner();

        HookResult result = await Runner(new HookOptions(), runner)
            .RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.True(result.Allow);
        Assert.True(result.Skipped);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ExecutionCountOnlyCountsHooksThatActuallyRan()
    {
        var options = new HookOptions().Set(HookEvent.OnBind, "hook.cmd");
        HookRunner hooks = Runner(options, Yes());

        await hooks.RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);
        await hooks.RunAsync(HookEvent.OnUnbind, Context(), CancellationToken.None);

        Assert.Equal(1, hooks.ExecutionCount);
    }

    // =====================================================================================
    // Hardening
    // =====================================================================================

    [Fact]
    public async Task AnIdentityHookIsRefusedOnAnUnauthenticatedPeer()
    {
        var runner = new FakeProcessRunner();
        var options = new HookOptions().Set(HookEvent.OnClientConnect, "hook.cmd");

        HookResult result = await Runner(options, runner)
            .RunAsync(HookEvent.OnClientConnect, Context(authenticated: false), CancellationToken.None);

        Assert.False(result.Allow);
        Assert.True(result.Skipped);
        Assert.Contains("not authenticated", result.Detail, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task AnIdentityHookRunsForAnAuthenticatedPeer()
    {
        var runner = Yes();
        var options = new HookOptions().Set(HookEvent.OnClientConnect, "hook.cmd");

        HookResult result = await Runner(options, runner)
            .RunAsync(HookEvent.OnClientConnect, Context(authenticated: true), CancellationToken.None);

        Assert.True(result.Allow);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task AScriptOutsideEveryAllowedRootIsRefused()
    {
        var runner = new FakeProcessRunner();
        var options = new HookOptions();
        options.AllowedScriptRoots.Add(@"C:\allowed");
        options.Set(HookEvent.OnBind, @"C:\elsewhere\hook.cmd");

        HookResult result = await Runner(options, runner).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.False(result.Allow);
        Assert.Contains("outside every configured allowed root", result.Detail, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task AScriptUnderAnAllowedRootRuns()
    {
        var runner = Yes();
        var options = new HookOptions();
        options.AllowedScriptRoots.Add(@"C:\allowed");
        options.Set(HookEvent.OnBind, @"C:\allowed\hook.cmd");

        HookResult result = await Runner(options, runner).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.True(result.Allow);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task AScriptWithArgumentsIsJudgedByItsPathOnly()
    {
        var options = new HookOptions();
        options.AllowedScriptRoots.Add(@"C:\allowed");
        options.Set(HookEvent.OnBind, @"C:\allowed\hook.cmd --verbose");

        HookResult result = await Runner(options, Yes()).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.True(result.Allow);
    }

    [Fact]
    public async Task AnEmptyAllowListMeansAnyPath()
    {
        var options = new HookOptions().Set(HookEvent.OnBind, @"D:\anywhere\hook.cmd");

        HookResult result = await Runner(options, Yes()).RunAsync(HookEvent.OnBind, Context(), CancellationToken.None);

        Assert.True(result.Allow);
    }

    // =====================================================================================
    // Wiring through the server state
    // =====================================================================================

    [Fact]
    public async Task TheServerStateRunsAHookWithTheDeviceContextSubstituted()
    {
        var runner = Yes();
        var options = new HookOptions().Set(HookEvent.OnBind, "hook.cmd $VENDOR_ID$:$PRODUCT_ID$ $CLIENT_IP$");
        var hooks = new HookRunner(options, runner, new StructuredLogger(LogLevel.Critical));

        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device }, hooks: () => hooks);

        HookResult result = await state.RunHookAsync(
            HookEvent.OnBind, device, CancellationToken.None, "client-9", "192.168.0.9", peerAuthenticated: true);

        Assert.True(result.Allow);
        Assert.Single(runner.Commands);
        Assert.Equal("hook.cmd 1234:5678 192.168.0.9", runner.Commands[0]);
    }

    [Fact]
    public async Task ARefusingHookIsVisibleToTheCaller()
    {
        var runner = new FakeProcessRunner { Outcome = new ProcessOutcome { Started = true, ExitCode = HookVocabulary.ExitNo } };
        var options = new HookOptions().Set(HookEvent.OnBind, "hook.cmd");
        var hooks = new HookRunner(options, runner, new StructuredLogger(LogLevel.Critical));

        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device }, hooks: () => hooks);

        HookResult result = await state.RunHookAsync(HookEvent.OnBind, device, CancellationToken.None);

        Assert.False(result.Allow);
        Assert.False(result.Skipped);
    }

    [Fact]
    public async Task WithNoHooksConfiguredTheServerStateReportsNotConfigured()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device });

        HookResult result = await state.RunHookAsync(HookEvent.OnBind, device, CancellationToken.None);

        Assert.True(result.Allow);
        Assert.True(result.Skipped);
    }

    [Fact]
    public async Task TheServerStateRefusesAnIdentityHookForAnUnauthenticatedPeer()
    {
        var runner = new FakeProcessRunner();
        var options = new HookOptions().Set(HookEvent.ClientAuthorization, "hook.cmd");
        var hooks = new HookRunner(options, runner, new StructuredLogger(LogLevel.Critical));

        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device }, hooks: () => hooks);

        HookResult result = await state.RunHookAsync(
            HookEvent.ClientAuthorization, device, CancellationToken.None, peerAuthenticated: false);

        Assert.False(result.Allow);
        Assert.True(result.Skipped);
    }
}
