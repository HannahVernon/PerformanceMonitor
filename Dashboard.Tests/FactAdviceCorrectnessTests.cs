using PerformanceMonitor.Analysis;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Regression guards for the sourced-wrong claims and the retrospective "kill it" survivors the
/// post-merge broad review flagged in FactAdvice's static blocks. Each was either contradicted by
/// MS Learn (range locks are SERIALIZABLE-only; an UPDATE maintains only changed-column indexes;
/// last-page contention is fixed by OPTIMIZE_FOR_SEQUENTIAL_KEY, not by adding a clustered index;
/// tempdb autogrowth should be ENABLED) or gave live-firefighting advice for a tool that reports on
/// windows that have already passed.
/// </summary>
public class FactAdviceCorrectnessTests
{
    private static string Text(string key)
    {
        var a = FactAdvice.GetForFactKey(key);
        Assert.NotNull(a);
        return $"{a!.Headline}\n{a.Investigation}\n{a.Remediation}";
    }

    [Fact]
    public void Sos_DoesNotClaimDemandExceedsSupply_NorCircularDeferral()
    {
        var t = Text("SOS_SCHEDULER_YIELD");
        Assert.DoesNotContain("demand than capacity", t);
        Assert.DoesNotContain("demand exceeds supply", t);
        Assert.DoesNotContain("recommendation engine will tell you", t);
        Assert.Contains("quantum", t); // states the real cooperative-scheduling mechanism
    }

    [Fact]
    public void IoWriteLatency_DoesNotClaimEveryUpdateTouchesEveryIndex()
    {
        Assert.DoesNotContain("every UPDATE touches every nonclustered index", Text("IO_WRITE_LATENCY_MS"));
    }

    [Fact]
    public void MissingIndex_UpdateMaintenanceClaimIsPrecise()
    {
        Assert.DoesNotContain("every INSERT/UPDATE/DELETE pays", Text("MISSING_INDEX"));
    }

    [Fact]
    public void LatchEx_LastPageFix_IsOptimizeForSequentialKey_NotAddClusteredIndex()
    {
        var t = Text("LATCH_EX");
        Assert.DoesNotContain("add a clustered index", t);
        Assert.Contains("OPTIMIZE_FOR_SEQUENTIAL_KEY", t);
    }

    [Fact]
    public void Tempdb_AutogrowthIsEnabled_NotDisabled_AndNoKill()
    {
        var t = Text("TEMPDB_USAGE");
        Assert.DoesNotContain("autogrowth disabled", t);
        Assert.DoesNotContain("kill it", t);
    }

    [Fact]
    public void RangeLocks_AreSerializableOnly_NotRepeatableRead()
    {
        Assert.DoesNotContain("SERIALIZABLE or REPEATABLE READ", Text("LCK_RANGE"));
    }

    [Fact]
    public void AnomalyBlockingSpike_NoKillIt()
    {
        Assert.DoesNotContain("kill it", Text("ANOMALY_BLOCKING_SPIKE"));
    }

    [Theory]
    [InlineData("ANOMALY_READ_LATENCY")]
    [InlineData("ANOMALY_WRITE_LATENCY")]
    public void LatencyAnomalies_HeadlineNotRightNow(string key)
    {
        Assert.DoesNotContain("right now", FactAdvice.GetForFactKey(key)!.Headline);
    }
}
