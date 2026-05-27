using System;
using System.Collections.Generic;
using System.Linq;
using PerformanceMonitor.Analysis;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Pure unit tests for BlockingChainReconstructor — apex/depth/victim reconstruction,
/// the composite session identity that defeats SPID reuse, the 1900-01-01 sentinel,
/// cycle handling, and the traversal caps. No database.
/// </summary>
public class BlockingChainReconstructorTests
{
    private const int MaxDepth = 50;
    private const int MaxPairs = 5000;
    private const int StepBudget = 100_000;

    private static DateTime TranFor(int spid) => new DateTime(2026, 5, 22, 9, 0, 0).AddSeconds(spid);

    private static BlockingPairRow Pair(
        int blockedSpid, int blockingSpid,
        DateTime? blockedTran = null, DateTime? blockingTran = null,
        long waitMs = 1000, string blockingStatus = "running")
    {
        return new BlockingPairRow
        {
            EventTime = new DateTime(2026, 5, 22, 10, 0, 0),
            DatabaseName = "TestDb",
            BlockedSpid = blockedSpid,
            BlockedTranStarted = blockedTran ?? TranFor(blockedSpid),
            BlockingSpid = blockingSpid,
            BlockingTranStarted = blockingTran ?? TranFor(blockingSpid),
            WaitTimeMs = waitMs,
            LockMode = "X",
            BlockingStatus = blockingStatus,
            BlockedSqlText = "blocked sql",
            BlockingSqlText = "blocking sql"
        };
    }

    private static BlockingReconstruction Run(IEnumerable<BlockingPairRow> rows) =>
        BlockingChainReconstructor.Reconstruct(rows, MaxDepth, MaxPairs, StepBudget);

    [Fact]
    public void Empty_ProducesNoChains()
    {
        var result = Run(Array.Empty<BlockingPairRow>());
        Assert.Empty(result.Chains);
    }

    [Fact]
    public void DepthFourLine_ReportsApexDepthAndVictims()
    {
        // 200 → 201 → 202 → 203 → 204
        var result = Run(new[]
        {
            Pair(201, 200), Pair(202, 201), Pair(203, 202), Pair(204, 203)
        });

        var chain = Assert.Single(result.Chains);
        Assert.Equal(200, chain.ApexSpid);
        Assert.Equal(4, chain.Depth);
        Assert.Equal(4, chain.VictimCount);
        Assert.False(result.CycleDetected);
    }

    [Fact]
    public void FanOut_IsDepthOneWithAllVictims()
    {
        // 300 blocks 301..305 directly
        var result = Run(Enumerable.Range(301, 5).Select(v => Pair(v, 300)));

        var chain = Assert.Single(result.Chains);
        Assert.Equal(300, chain.ApexSpid);
        Assert.Equal(1, chain.Depth);
        Assert.Equal(5, chain.VictimCount);
    }

    [Fact]
    public void SpidReuse_DifferentTransactionStart_DoesNotSplice()
    {
        // Real chain 200 → 201 → 202, plus SPID 201 reused (different tran) blocking 203.
        var reusedTran = TranFor(201).AddHours(3);
        var result = Run(new[]
        {
            Pair(201, 200),
            Pair(202, 201),
            Pair(203, 201, blockingTran: reusedTran) // reused 201 — a distinct session
        });

        // Two distinct chains: apex 200 depth 2, and the reused-201 apex depth 1.
        Assert.Equal(2, result.Chains.Count);
        Assert.Contains(result.Chains, c => c.ApexSpid == 200 && c.Depth == 2);
        Assert.Contains(result.Chains, c => c.ApexSpid == 201 && c.Depth == 1);
    }

    [Fact]
    public void Sentinel_TransactionStart_NormalizesToNull()
    {
        // SQL Server's 1900-01-01 "no transaction" sentinel must key the same as NULL.
        Assert.Equal(
            BlockingChainReconstructor.MakeKey(100, null),
            BlockingChainReconstructor.MakeKey(100, new DateTime(1900, 1, 1)));

        // And a real transaction start must NOT collapse to the sentinel key.
        Assert.NotEqual(
            BlockingChainReconstructor.MakeKey(100, null),
            BlockingChainReconstructor.MakeKey(100, TranFor(100)));
    }

    [Fact]
    public void PureCycle_IsDetectedAndGetsFallbackRoot()
    {
        // A blocks B, B blocks A — every node is blocked, so there is no apex.
        var result = Run(new[]
        {
            Pair(blockedSpid: 401, blockingSpid: 400),
            Pair(blockedSpid: 400, blockingSpid: 401)
        });

        Assert.True(result.CycleDetected);
        Assert.NotEmpty(result.Chains); // fallback root — the cycle is not silently dropped
    }

    [Fact]
    public void DepthCap_IsFlaggedAndBounded()
    {
        // A 12-edge line, reconstructed with a maxDepth of 4.
        var rows = Enumerable.Range(0, 12).Select(i => Pair(501 + i, 500 + i)).ToList();
        var result = BlockingChainReconstructor.Reconstruct(rows, maxDepth: 4, MaxPairs, StepBudget);

        Assert.True(result.DepthCapped);
        var chain = Assert.Single(result.Chains);
        Assert.True(chain.Depth <= 4, $"depth {chain.Depth} should be capped at 4");
    }

    [Fact]
    public void EdgeDedup_KeepsTheLargestWaitTime()
    {
        // Same blocked/blocker pair re-fires with a growing wait time.
        var result = Run(new[]
        {
            Pair(601, 600, waitMs: 1_000),
            Pair(601, 600, waitMs: 9_000),
            Pair(601, 600, waitMs: 4_000)
        });

        var chain = Assert.Single(result.Chains);
        Assert.Equal(9_000, chain.MaxWaitMs);
    }

    [Fact]
    public void SleepingApex_IsReported()
    {
        var result = Run(new[]
        {
            Pair(701, 700, blockingStatus: "sleeping"),
            Pair(702, 701)
        });

        var chain = Assert.Single(result.Chains);
        Assert.Equal(700, chain.ApexSpid);
        Assert.True(chain.ApexSleeping);
    }

    [Fact]
    public void Ranking_PutsTheHigherMagnitudeChainFirst()
    {
        // Chain A: depth 1, 8 victims (wide). Chain B: depth 2, 2 victims (shallow).
        var rows = new List<BlockingPairRow>();
        rows.AddRange(Enumerable.Range(801, 8).Select(v => Pair(v, 800)));   // wide
        rows.Add(Pair(901, 900));                                            // narrow
        rows.Add(Pair(902, 901));

        var result = Run(rows);

        Assert.Equal(2, result.Chains.Count);
        // The 8-victim fan-out out-scores the depth-2 / 2-victim chain.
        Assert.Equal(800, result.Chains[0].ApexSpid);
    }
}
