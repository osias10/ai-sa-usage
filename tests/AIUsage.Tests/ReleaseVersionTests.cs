using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.4.10", "0.4.10")]
    [InlineData("0.4.10.0", "0.4.10")]
    [InlineData("0.4.10.2", "0.4.10.2")]
    [InlineData("0.4.11-beta.2", "0.4.11-beta.2")]
    [InlineData("v0.4.11-rc.10+abcdef", "0.4.11-rc.10")]
    [InlineData("0.4.10+build.9", "0.4.10")]
    public void NormalizesReleaseVersions(string input, string expected)
        => Assert.Equal(expected, ReleaseVersion.Parse(input).ToString());

    [Theory]
    [InlineData("0.4.10-beta.2", "0.4.10-beta.10")]
    [InlineData("0.4.10-beta.10", "0.4.10-rc.1")]
    [InlineData("0.4.10-rc.1", "0.4.10")]
    [InlineData("0.4.10", "0.4.11-beta.1")]
    [InlineData("0.4.10", "0.4.10.1")]
    [InlineData("1.0.0-9", "1.0.0-a")]
    [InlineData("1.0.0-beta", "1.0.0-beta.1")]
    [InlineData("1.0.0-beta.9999999999999999999999", "1.0.0-beta.10000000000000000000000")]
    public void SortsByReleasePrecedence(string older, string newer)
    {
        Assert.True(ReleaseVersion.Parse(older).CompareTo(ReleaseVersion.Parse(newer)) < 0);
        Assert.True(ReleaseVersion.Parse(newer).CompareTo(ReleaseVersion.Parse(older)) > 0);
    }

    [Theory]
    [InlineData("0.4.10", "v0.4.10.0")]
    [InlineData("0.4.10+one", "0.4.10+two")]
    [InlineData("0.4.11-beta.1+one", "0.4.11-beta.1+two")]
    public void EquivalentVersionsHaveEqualHashes(string first, string second)
    {
        var left = ReleaseVersion.Parse(first);
        var right = ReleaseVersion.Parse(second);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.4")]
    [InlineData("0.4.10\n")]
    [InlineData(" 0.4.10")]
    [InlineData("0.04.10")]
    [InlineData("0.4.10-beta.01")]
    [InlineData("0.4.10.0-beta.1")]
    [InlineData("2147483648.0.0")]
    [InlineData("0.4.10-")]
    [InlineData("0.4.10+build..one")]
    public void InvalidVersionsAreRejected(string? value)
        => Assert.False(ReleaseVersion.TryParse(value, out _));

    [Fact]
    public void ConvertsLegacyWindowsVersion() => Assert.Equal("0.4.10", ReleaseVersion.FromVersion(new(0, 4, 10, 0)).ToString());

    [Theory]
    [InlineData("0.4.10", UpdateChannel.Stable, "0.4.10", false, UpdateAction.None)]
    [InlineData("0.4.10", UpdateChannel.Stable, "0.4.9", false, UpdateAction.None)]
    [InlineData("0.4.10", UpdateChannel.Preview, "0.4.11-beta.2", true, UpdateAction.Upgrade)]
    [InlineData("0.4.11-beta.2", UpdateChannel.Stable, "0.4.10", false, UpdateAction.ReturnToStable)]
    [InlineData("0.4.11-beta.2", UpdateChannel.Stable, "0.4.11", false, UpdateAction.Upgrade)]
    [InlineData("0.4.11-beta.2", UpdateChannel.Preview, "0.4.10", false, UpdateAction.None)]
    [InlineData("0.4.11-beta.2", UpdateChannel.Preview, "0.4.11-beta.10", true, UpdateAction.Upgrade)]
    [InlineData("0.4.11-beta.2", UpdateChannel.Preview, "0.4.11", false, UpdateAction.Upgrade)]
    [InlineData("0.4.10", UpdateChannel.Stable, "0.4.11-beta.1", true, UpdateAction.None)]
    [InlineData("0.4.10", UpdateChannel.Preview, "0.4.11-beta.1", false, UpdateAction.None)]
    [InlineData("0.4.10", UpdateChannel.Preview, "0.4.11", true, UpdateAction.None)]
    public void ChoosesUpgradeOrExplicitStableReturn(string current, UpdateChannel channel, string target, bool preview, UpdateAction expected)
    {
        var release = new ReleaseInfo(target, new("https://github.com/example/ai-sa/releases/tag/v" + target), null, null, preview);
        Assert.Equal(expected, UpdateDecision.GetAction(ReleaseVersion.Parse(current), channel, release));
    }
}
