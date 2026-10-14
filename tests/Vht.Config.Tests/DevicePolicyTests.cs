using Vht.Config.DeviceIdentity;
using Vht.Config.Policy;
using Xunit;

namespace Vht.Config.Tests;

/// <summary>
/// The allow/ignore acceptance criteria, verbatim:
///   * "An ignored device never appears in the export list"
///   * "an allow-list-only configuration exports exactly the allowed devices"
/// </summary>
public sealed class DevicePolicyTests
{
    private static DevicePolicy Policy(string[] allow, string[] ignore)
    {
        var allowSelectors = allow.Select(a =>
        {
            Assert.True(DeviceSelector.TryParse(a, out var s, out var error), error);
            return s;
        }).ToList();
        var ignoreSelectors = ignore.Select(i =>
        {
            Assert.True(DeviceSelector.TryParse(i, out var s, out var error), error);
            return s;
        }).ToList();

        return new DevicePolicy(allowSelectors, ignoreSelectors);
    }

    [Fact]
    public void Empty_allow_list_exports_everything_that_is_not_ignored()
    {
        var policy = Policy(Array.Empty<string>(), new[] { "dead:beef" });
        var decision = policy.Evaluate(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB");

        Assert.True(decision.Exported);
        Assert.False(decision.AllowListOnly);
    }

    [Fact]
    public void An_ignored_device_is_never_exported_even_when_it_is_also_allowed()
    {
        var policy = Policy(new[] { "1050:0407" }, new[] { "1050:0407" });
        var decision = policy.Evaluate(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB");

        Assert.False(decision.Exported);
        Assert.Contains("policy.ignore", decision.Reason);
        Assert.Contains(policy.DescribeConflicts(), c => c.Contains("appears in both"));
    }

    [Fact]
    public void An_allow_list_only_configuration_exports_exactly_the_allowed_devices()
    {
        var policy = Policy(new[] { "1050:0407", "090c:1000" }, Array.Empty<string>());
        var allowed = TestSupport.SerialDevice();
        var alsoAllowed = TestSupport.SerialLessDevice();
        var notAllowed = TestSupport.AnonymousDevice();

        Assert.True(policy.Evaluate(allowed, "s-1050-0407-0123456789AB").Exported);
        Assert.True(policy.Evaluate(alsoAllowed, "f-090c-1000-1100-abcdef12").Exported);

        var refused = policy.Evaluate(notAllowed, "t-1234abcd");
        Assert.False(refused.Exported);
        Assert.True(refused.AllowListOnly);
        Assert.Contains("allow-list mode", refused.Reason);
    }

    [Fact]
    public void Selectors_cover_wildcards_serials_and_explicit_device_keys()
    {
        Assert.True(DeviceSelector.TryParse("1050:*", out var vendorWildcard, out _));
        Assert.True(vendorWildcard.Matches(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB"));
        Assert.False(vendorWildcard.Matches(TestSupport.SerialLessDevice(), "f-090c-1000-1100-abcdef12"));

        Assert.True(DeviceSelector.TryParse("*:0407", out var productWildcard, out _));
        Assert.True(productWildcard.Matches(TestSupport.SerialDevice(), "anything"));

        Assert.True(DeviceSelector.TryParse("1050:0407:0123456789AB", out var withSerial, out _));
        Assert.True(withSerial.Matches(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB"));
        Assert.False(withSerial.Matches(TestSupport.SerialDevice("FFFFFFFFFFFF"), "s-1050-0407-FFFFFFFFFFFF"));

        Assert.True(DeviceSelector.TryParse("serial:0123456789ab", out var bySerial, out _));
        Assert.True(bySerial.Matches(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB"));

        Assert.True(DeviceSelector.TryParse("key:s-1050-0407-0123456789AB", out var byKey, out _));
        Assert.True(byKey.Matches(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB"));
        Assert.False(byKey.Matches(TestSupport.SerialDevice(), "s-1050-0407-OTHER"));
    }

    [Theory]
    [InlineData("1050")]
    [InlineData("1050:zzzz")]
    [InlineData("1050:0407:")]
    [InlineData("key:")]
    [InlineData("")]
    public void Malformed_selectors_are_refused_with_a_reason(string text)
    {
        Assert.False(DeviceSelector.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}