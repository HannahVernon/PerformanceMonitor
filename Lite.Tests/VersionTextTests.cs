using System;
using PerformanceMonitor.Ui;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Pins <see cref="VersionText"/>, the shared version parse/normalize the Dashboard and Lite server-list
/// views use. The load-bearing case is the two-part-version clamp: the hand-rolled snippets this replaced
/// did <c>new Version(p.Major, p.Minor, p.Build)</c> after <c>Version.TryParse</c>, which THROWS on a
/// two-part version like "3.1" (TryParse leaves Build == -1, and the Version ctor rejects a negative build).
/// The first test is that regression, as an assertion that it no longer throws.
/// </summary>
public class VersionTextTests
{
    [Fact]
    public void TwoPartVersion_ClampsBuildToZero_InsteadOfThrowing()
    {
        // The whole point: this used to throw ArgumentOutOfRangeException at every call site.
        var ex = Record.Exception(() => VersionText.Normalize("3.1"));
        Assert.Null(ex);

        Assert.Equal("3.1.0", VersionText.Normalize("3.1"));
        Assert.Equal(new Version(3, 1, 0), VersionText.Parse("3.1"));
    }

    [Theory]
    [InlineData("3.1.0", "3.1.0")]
    [InlineData("3.1.0.0", "3.1.0")]       // four-part collapses to three
    [InlineData("3.1", "3.1.0")]           // two-part clamps
    [InlineData("3.2.0+abc123", "3.2.0")]  // +build metadata stripped
    [InlineData("3.2.0-rc1", "3.2.0")]     // -prerelease stripped
    [InlineData("  3.1.0  ", "3.1.0")]     // trimmed
    public void Normalize_ProducesThreePartDisplay(string raw, string expected)
    {
        Assert.Equal(expected, VersionText.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrBlank_IsEmptyString(string? raw)
    {
        Assert.Equal(string.Empty, VersionText.Normalize(raw));
    }

    [Theory]
    [InlineData("Unreachable")]
    [InlineData("not-a-version")]
    [InlineData("v3")]
    public void Normalize_Unparseable_PassesTheTrimmedOriginalThrough(string raw)
    {
        // Better to show a weird value than to lose it — the views fall back to the raw string.
        Assert.Equal(raw.Trim(), VersionText.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unreachable")]
    [InlineData("v3")]
    public void Parse_NullBlankOrGarbage_IsNull(string? raw)
    {
        Assert.Null(VersionText.Parse(raw));
    }

    [Theory]
    [InlineData("3.0.0", "3.1.0")]
    [InlineData("3.1", "3.1.1")]           // the two-part clamp on the left still compares correctly
    [InlineData("2.9.0", "3.0.0")]
    [InlineData("3.1.0", "3.2.0")]
    public void Parse_OrdersOlderBeforeNewer(string older, string newer)
    {
        var a = VersionText.Parse(older);
        var b = VersionText.Parse(newer);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a < b, $"{older} should parse older than {newer}");
    }

    [Fact]
    public void Parse_PrereleaseAndReleaseOfSameCore_CompareEqual()
    {
        // "3.0.0-rc1" strips to "3.0.0", so it is NOT treated as older than "3.0.0". This is a known
        // limitation of stripping the suffix (fine here: these views compare RELEASED product versions).
        Assert.Equal(VersionText.Parse("3.0.0"), VersionText.Parse("3.0.0-rc1"));
    }

    [Fact]
    public void Parse_DropsTheFourthPart_MatchingTheOriginalThreePartNormalize()
    {
        // The hand-rolled sites did new Version(p.Major, p.Minor, p.Build) -- three parts, Revision dropped.
        // Preserve that: a four-part version collapses to its three-part core, so "3.1.0.7" == "3.1.0".
        Assert.Equal(VersionText.Parse("3.1.0"), VersionText.Parse("3.1.0.7"));
        Assert.Equal("3.1.0", VersionText.Normalize("3.1.0.7"));
    }
}
