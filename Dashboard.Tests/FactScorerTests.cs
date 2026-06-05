using System.Collections.Generic;
using System.Linq;
using PerformanceMonitor.Analysis;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Tests FactScorer Layer 1 (base severity) and Layer 2 (amplifiers).
/// Validates threshold formulas, amplifier firing, and severity capping.
/// </summary>
public class FactScorerTests
{
    /* ── Threshold formula unit tests ── */

    [Theory]
    [InlineData(0.0, 0.25, null, 0.0)]        // Zero → 0.0
    [InlineData(0.125, 0.25, null, 0.5)]       // Half of concerning → 0.5
    [InlineData(0.25, 0.25, null, 1.0)]        // At concerning (no critical) → 1.0
    [InlineData(0.50, 0.25, null, 1.0)]        // Above concerning (no critical) → capped at 1.0
    [InlineData(0.0, 0.25, 0.75, 0.0)]         // Zero → 0.0
    [InlineData(0.125, 0.25, 0.75, 0.25)]      // Half of concerning → 0.25
    [InlineData(0.25, 0.25, 0.75, 0.5)]        // At concerning → 0.5
    [InlineData(0.50, 0.25, 0.75, 0.75)]       // Midway → 0.75
    [InlineData(0.75, 0.25, 0.75, 1.0)]        // At critical → 1.0
    [InlineData(1.00, 0.25, 0.75, 1.0)]        // Above critical → 1.0
    public void ApplyThresholdFormula_ReturnsExpected(
        double value, double concerning, double? critical, double expected)
    {
        var result = FactScorer.ApplyThresholdFormula(value, concerning, critical);
        Assert.Equal(expected, result, precision: 4);
    }

    /* ── Unknown wait types ── */

    [Fact]
    public void Score_UnknownWaitType_GetsSeverityZero()
    {
        var facts = new List<Fact>
        {
            new() { Source = "waits", Key = "UNKNOWN_WAIT_XYZ", Value = 0.50 }
        };

        var scorer = new FactScorer();
        scorer.ScoreAll(facts);

        Assert.Equal(0.0, facts[0].BaseSeverity);
    }

    /* ── Layer 2: Amplifier tests ── */

    [Fact]
    public void Amplifier_SeverityCappedAt2()
    {
        // Synthetic: create a fact set where amplifiers would push past 2.0
        var facts = new List<Fact>
        {
            new() { Source = "waits", Key = "CXPACKET", Value = 0.80 },           // base = 1.0
            new() { Source = "waits", Key = "SOS_SCHEDULER_YIELD", Value = 0.50 }, // > 25% threshold
            new() { Source = "waits", Key = "THREADPOOL", Value = 0.05,           // real thread exhaustion
                Metadata = new() { ["wait_time_ms"] = 7_200_000, ["avg_ms_per_wait"] = 3_600 } },  // 2h total, 3.6s avg
            new() { Source = "config", Key = "CONFIG_CTFP", Value = 5 },          // bad CTFP
            new() { Source = "config", Key = "CONFIG_MAXDOP", Value = 0 },        // bad MAXDOP
        };

        var scorer = new FactScorer();
        scorer.ScoreAll(facts);

        var cx = facts.First(f => f.Key == "CXPACKET");

        // base 1.0 * (1.0 + 0.3 SOS + 0.4 THREADPOOL + 0.3 CTFP + 0.2 MAXDOP) = 2.2 → capped at 2.0
        Assert.True(cx.Severity <= 2.0, "Severity should never exceed 2.0");
        Assert.Equal(2.0, cx.Severity);
    }

    /* ── WS3: percent-autogrowth-on-large-files config fact ── */

    // A FILE_AUTOGROWTH_PERCENT fact scores the 0.3 advisory base when at least one large
    // percent-growth file was found (file_count > 0) — mirroring DB_CONFIG's single-misconfig
    // base. It is deliberately below the 0.5 incident threshold; it surfaces only because it
    // is a config-advisory root key (see the InferenceEngine rooting test).
    [Fact]
    public void FileAutogrowthPercent_ScoresAdvisoryBase_WhenFilesPresent()
    {
        var facts = new List<Fact>
        {
            new() { Source = "config", Key = "FILE_AUTOGROWTH_PERCENT", Value = 2,
                    Metadata = new() { ["file_count"] = 2, ["database_count"] = 1 } }
        };

        var scorer = new FactScorer();
        scorer.ScoreAll(facts);

        Assert.Equal(0.3, facts[0].BaseSeverity, precision: 4);
    }

    // No offending files (file_count == 0) → no severity. Defends the collector contract that
    // the fact is emitted only when file_count > 0, and the scorer's own guard.
    [Fact]
    public void FileAutogrowthPercent_ScoresZero_WhenNoFiles()
    {
        var facts = new List<Fact>
        {
            new() { Source = "config", Key = "FILE_AUTOGROWTH_PERCENT", Value = 0,
                    Metadata = new() { ["file_count"] = 0, ["database_count"] = 0 } }
        };

        var scorer = new FactScorer();
        scorer.ScoreAll(facts);

        Assert.Equal(0.0, facts[0].BaseSeverity, precision: 4);
    }

    // Advice exists for the fact key (a missing AdviceBlock is the P1 dead-fact bug class —
    // a fact that roots but renders no advice). Headline must be the authored copy.
    [Fact]
    public void FileAutogrowthPercent_HasAdviceBlock()
    {
        var advice = FactAdvice.GetForFactKey("FILE_AUTOGROWTH_PERCENT");

        Assert.NotNull(advice);
        Assert.Equal("Large file(s) growing in percentage steps", advice!.Headline);
        Assert.False(string.IsNullOrWhiteSpace(advice.Investigation));
        Assert.False(string.IsNullOrWhiteSpace(advice.Remediation));
    }
}
