using System;
using System.Collections.Generic;
using System.Linq;
using PerformanceMonitor.Common;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Pure unit tests for the shared <see cref="BlockingChainTreeBuilder"/> — the DAG -> tree logic that
/// turns the analysis engine's edge lists into renderable trees. Tested once in Common (not duplicated
/// per app). No database, no WPF.
/// </summary>
public class BlockingChainTreeBuilderTests
{
    private static DateTime Tran(int spid) => new DateTime(2026, 5, 22, 9, 0, 0).AddSeconds(spid);

    private static BlockingEdgeInput Edge(
        int level, int blocker, int blocked,
        long wait = 1000, string lockMode = "X", string db = "TestDb",
        DateTime? blockerTran = null, DateTime? blockedTran = null) => new()
        {
            Level = level,
            BlockingSpid = blocker,
            BlockingTranStarted = blockerTran ?? Tran(blocker),
            BlockedSpid = blocked,
            BlockedTranStarted = blockedTran ?? Tran(blocked),
            WaitTimeMs = wait,
            LockMode = lockMode,
            DatabaseName = db,
            BlockingSqlText = $"blocker {blocker}",
            BlockedSqlText = $"blocked {blocked}"
        };

    private static BlockingChainInput Chain(
        int apex, IEnumerable<BlockingEdgeInput> edges,
        double magnitude = 1.0, bool sleeping = false, DateTime? apexTran = null) => new()
        {
            ApexSpid = apex,
            ApexTranStarted = apexTran ?? Tran(apex),
            ApexSleeping = sleeping,
            Magnitude = magnitude,
            Edges = edges.ToList()
        };

    private static BlockingChainModel Build(params BlockingChainInput[] chains) =>
        BlockingChainTreeBuilder.Build(chains, false, false, false);

    private static IEnumerable<BlockingChainNode> Flatten(BlockingChainNode n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var d in Flatten(c))
                yield return d;
    }

    [Fact]
    public void SingleChain_BuildsLinearTree_WithSourcedFields()
    {
        // 200 -> 201 -> 202 -> 203
        var model = Build(Chain(200, new[]
        {
            Edge(1, 200, 201), Edge(2, 201, 202), Edge(3, 202, 203)
        }));

        var root = Assert.Single(model.Roots);
        Assert.Equal(200, root.Spid);
        Assert.True(root.IsApex);
        Assert.Equal(0, root.WaitTimeMs);          // apex waits on no one
        Assert.Equal(string.Empty, root.LockMode);
        Assert.Equal("blocker 200", root.SqlText); // apex SQL sourced from its outgoing edge
        Assert.Equal("TestDb", root.DatabaseName);

        var n201 = Assert.Single(root.Children);
        Assert.Equal(201, n201.Spid);
        Assert.False(n201.IsApex);
        Assert.Equal(1000, n201.WaitTimeMs);
        Assert.Equal("X", n201.LockMode);
        Assert.Equal("blocked 201", n201.SqlText); // victim SQL sourced from its incoming edge

        var n202 = Assert.Single(n201.Children);
        var n203 = Assert.Single(n202.Children);
        Assert.Equal(203, n203.Spid);
        Assert.Empty(n203.Children);
        Assert.Equal(4, Flatten(root).Count());
    }

    [Fact]
    public void BranchingBlocker_AttachesAllVictimsToApex_OrderedBySpid()
    {
        var model = Build(Chain(300, new[]
        {
            Edge(1, 300, 303), Edge(1, 300, 301), Edge(1, 300, 302)
        }));

        var root = Assert.Single(model.Roots);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal(new[] { 301, 302, 303 }, root.Children.Select(c => c.Spid).ToArray());
        Assert.All(root.Children, c => Assert.Empty(c.Children));
    }

    [Fact]
    public void SameSpidDifferentTransaction_AreKeptAsSeparateNodes()
    {
        // Apex 200 blocks SPID 201 twice — but two DIFFERENT sessions (distinct tran starts). The
        // builder must NOT collapse them into one node.
        var tranA = Tran(201);
        var tranB = Tran(201).AddHours(3);
        var model = Build(Chain(200, new[]
        {
            Edge(1, 200, 201, blockedTran: tranA),
            Edge(1, 200, 201, blockedTran: tranB)
        }));

        var root = Assert.Single(model.Roots);
        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, c => Assert.Equal(201, c.Spid));
        Assert.Equal(new[] { tranA, tranB }.OrderBy(t => t),
                     root.Children.Select(c => c.TranStarted!.Value).OrderBy(t => t));
    }

    [Fact]
    public void Diamond_AttachesVictimToLowestParentSpid_DroppingExtraInEdge()
    {
        // 400 -> 401, 400 -> 402, and BOTH 401 and 402 block 403 at the same level. Tie on level breaks
        // to the lowest parent SPID (401); the 402 -> 403 in-edge is dropped, and 403 appears once.
        var model = Build(Chain(400, new[]
        {
            Edge(1, 400, 401), Edge(1, 400, 402),
            Edge(2, 401, 403), Edge(2, 402, 403)
        }));

        var root = Assert.Single(model.Roots);
        var n401 = root.Children.Single(c => c.Spid == 401);
        var n402 = root.Children.Single(c => c.Spid == 402);

        Assert.Single(n401.Children);
        Assert.Equal(403, n401.Children[0].Spid);
        Assert.Empty(n402.Children);
        Assert.Equal(4, Flatten(root).Count());        // 403 is not duplicated
        Assert.Single(Flatten(root).Where(n => n.Spid == 403));
    }

    [Fact]
    public void Flags_ArePropagatedToModel()
    {
        var model = BlockingChainTreeBuilder.Build(
            new[] { Chain(200, new[] { Edge(1, 200, 201) }) },
            cycleDetected: true, depthCapped: true, traversalTruncated: true);

        Assert.True(model.CycleDetected);
        Assert.True(model.DepthCapped);
        Assert.True(model.TraversalTruncated);
    }

    [Fact]
    public void SleepingApex_IsFlaggedOnRoot()
    {
        var model = Build(Chain(200, new[] { Edge(1, 200, 201) }, sleeping: true));
        var root = Assert.Single(model.Roots);
        Assert.True(root.IsApex);
        Assert.True(root.IsApexSleeping);
    }

    [Fact]
    public void Roots_AreRankedByMagnitudeDescending()
    {
        var weak = Chain(200, new[] { Edge(1, 200, 201) }, magnitude: 0.2);
        var strong = Chain(300, new[] { Edge(1, 300, 301) }, magnitude: 0.9);

        var model = BlockingChainTreeBuilder.Build(new[] { weak, strong }, false, false, false);

        Assert.Equal(2, model.Roots.Count);
        Assert.Equal(300, model.Roots[0].Spid);   // higher magnitude first
        Assert.Equal(0.9, model.Roots[0].Magnitude);
        Assert.Equal(200, model.Roots[1].Spid);
    }

    [Fact]
    public void Cycle_DoesNotInfiniteLoop_AndPlacesEachNodeOnce()
    {
        // 500 -> 501 -> 502, plus a back-edge 502 -> 500. The visited set must stop the cycle and the
        // apex must never be re-parented; each session appears once.
        var model = BlockingChainTreeBuilder.Build(
            new[]
            {
                Chain(500, new[]
                {
                    Edge(1, 500, 501), Edge(2, 501, 502), Edge(3, 502, 500)
                })
            },
            cycleDetected: true, depthCapped: false, traversalTruncated: false);

        var root = Assert.Single(model.Roots);
        Assert.Equal(500, root.Spid);
        Assert.Equal(3, Flatten(root).Count());                 // 500, 501, 502 — no duplicate 500
        Assert.Single(Flatten(root).Where(n => n.Spid == 500));
    }
}
