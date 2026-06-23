/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor analysis engine.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// One blocked/blocker pair from blocked_process_reports — the raw input to reconstruction.
/// </summary>
internal sealed class BlockingPairRow
{
    public DateTime EventTime { get; init; }
    public string DatabaseName { get; init; } = string.Empty;
    public int BlockedSpid { get; init; }
    public DateTime? BlockedTranStarted { get; init; }
    public int BlockingSpid { get; init; }
    public DateTime? BlockingTranStarted { get; init; }
    public long WaitTimeMs { get; init; }
    public string LockMode { get; init; } = string.Empty;
    public string BlockingStatus { get; init; } = string.Empty;
    public string BlockedSqlText { get; init; } = string.Empty;
    public string BlockingSqlText { get; init; } = string.Empty;
    // Session identity for each side (login / host / client app). Carried so the viewer can show WHO a
    // session is, not just a SPID. Threaded the same way as the SQL text.
    public string BlockedLoginName { get; init; } = string.Empty;
    public string BlockedHostName { get; init; } = string.Empty;
    public string BlockedClientApp { get; init; } = string.Empty;
    // Blocker-side identity is settable (not init) so the Dashboard viewer can enrich it after Read from
    // the correlated activity='blocking' row — its table denormalizes only the blocked side (see Dashboard
    // BlockingPairRowQuery.ReadWithBlockerIdentity). Lite sets all six in its Read object initializer.
    public string BlockingLoginName { get; set; } = string.Empty;
    public string BlockingHostName { get; set; } = string.Empty;
    public string BlockingClientApp { get; set; } = string.Empty;
}

/// <summary>
/// Stable session identity. A SPID is reused across the analysis window, so the bare
/// integer is not a session identity — the transaction start time disambiguates two
/// sessions that reused one SPID.
/// </summary>
internal readonly record struct SessionKey(int Spid, DateTime? TranStarted);

/// <summary>One level (one blocked/blocker edge) of a reconstructed chain, for drill-down.</summary>
internal sealed class ChainLevel
{
    public int Level { get; init; }
    public int BlockingSpid { get; init; }
    /// <summary>Transaction start of the blocking side — the identity disambiguator a faithful
    /// tree rebuild needs (a SPID reused by two sessions in the window is two real nodes).</summary>
    public DateTime? BlockingTranStarted { get; init; }
    public int BlockedSpid { get; init; }
    public DateTime? BlockedTranStarted { get; init; }
    public string LockMode { get; init; } = string.Empty;
    public long WaitTimeMs { get; init; }
    public string DatabaseName { get; init; } = string.Empty;
    public string BlockingSqlText { get; init; } = string.Empty;
    public string BlockedSqlText { get; init; } = string.Empty;
    public string BlockedLoginName { get; init; } = string.Empty;
    public string BlockedHostName { get; init; } = string.Empty;
    public string BlockedClientApp { get; init; } = string.Empty;
    public string BlockingLoginName { get; init; } = string.Empty;
    public string BlockingHostName { get; init; } = string.Empty;
    public string BlockingClientApp { get; init; } = string.Empty;
}

/// <summary>A single reconstructed blocking chain, rooted at an apex head blocker.</summary>
internal sealed class ReconstructedChain
{
    public int ApexSpid { get; init; }
    /// <summary>Transaction start of the apex — pairs with <see cref="ApexSpid"/> as its session identity.</summary>
    public DateTime? ApexTranStarted { get; init; }
    public bool ApexSleeping { get; init; }
    public int Depth { get; init; }
    public int VictimCount { get; init; }
    public long MaxWaitMs { get; init; }
    public double Magnitude { get; init; }
    public IReadOnlyList<ChainLevel> Levels { get; init; } = Array.Empty<ChainLevel>();
}

/// <summary>Result of a reconstruction pass — chains ranked worst-first, plus cap flags.</summary>
internal sealed class BlockingReconstruction
{
    public IReadOnlyList<ReconstructedChain> Chains { get; init; } = Array.Empty<ReconstructedChain>();
    public bool DepthCapped { get; init; }
    public bool TraversalTruncated { get; init; }
    public bool CycleDetected { get; init; }
}

/// <summary>
/// Reconstructs blocking chains (apex head blocker, depth, victim count) from the per-pair
/// blocked_process_reports rows. Pure — no DB dependency — so the collector and the
/// drill-down collector share one implementation and it is directly unit-testable.
/// </summary>
internal static class BlockingChainReconstructor
{
    /// <summary>
    /// SQL Server's blocked-process-report emits lasttranstarted="1900-01-01T00:00:00"
    /// (a real, parseable value — not NULL) for a session with no open transaction.
    /// A transaction start at or before this floor is treated as "no transaction".
    /// </summary>
    private static readonly DateTime SentinelFloor = new(1900, 1, 2);

    // Holds the deduped winning row for an edge, so every per-pair field (wait, lock, SQL, identity) is
    // available when building the ChainLevel without re-listing each one here.
    private sealed record EdgeInfo(BlockingPairRow Row);

    /// <summary>Builds a stable session key, normalizing the 1900-01-01 sentinel to NULL.</summary>
    public static SessionKey MakeKey(int spid, DateTime? tranStarted)
    {
        var normalized = tranStarted.HasValue && tranStarted.Value > SentinelFloor ? tranStarted : null;
        return new SessionKey(spid, normalized);
    }

    /// <summary>
    /// Finds the single reconstructed chain that contains the given session (matched by SessionKey — the
    /// session may be the apex, a mid-level blocker, or a leaf victim). Returns the chain rooted at its
    /// apex, or null if no chain contains the session. Lets the viewer scope to the one chain the clicked
    /// row belongs to.
    /// </summary>
    public static ReconstructedChain? FindChainForSession(
        BlockingReconstruction reconstruction, int spid, DateTime? tranStarted)
    {
        var key = MakeKey(spid, tranStarted);
        foreach (var chain in reconstruction.Chains)
            if (ChainContains(chain, key))
                return chain;
        return null;
    }

    /// <summary>
    /// Edge-precise overload: when the clicked row carries its blocker too, prefer the chain that contains
    /// the exact (blocker -> blocked) edge. This disambiguates a victim that was blocked by two different
    /// lead blockers at different times in the window (each is a separate chain, both containing the victim).
    /// Falls back to the any-level match on the blocked session when no chain holds that exact edge (e.g. the
    /// clicked row has no recorded blocker).
    /// </summary>
    public static ReconstructedChain? FindChainForSession(
        BlockingReconstruction reconstruction,
        int blockedSpid, DateTime? blockedTran,
        int blockingSpid, DateTime? blockingTran)
    {
        var blockedKey = MakeKey(blockedSpid, blockedTran);
        var blockerKey = MakeKey(blockingSpid, blockingTran);

        foreach (var chain in reconstruction.Chains)
            if (ChainContainsEdge(chain, blockerKey, blockedKey))
                return chain;

        foreach (var chain in reconstruction.Chains)
            if (ChainContains(chain, blockedKey))
                return chain;

        return null;
    }

    /// <summary>True if the chain has the exact blocker -> blocked edge.</summary>
    private static bool ChainContainsEdge(ReconstructedChain chain, SessionKey blockerKey, SessionKey blockedKey)
    {
        foreach (var l in chain.Levels)
            if (MakeKey(l.BlockingSpid, l.BlockingTranStarted).Equals(blockerKey) &&
                MakeKey(l.BlockedSpid, l.BlockedTranStarted).Equals(blockedKey))
                return true;
        return false;
    }

    /// <summary>True if the session appears anywhere in the chain (apex, a blocker, or a victim).</summary>
    private static bool ChainContains(ReconstructedChain chain, SessionKey key)
    {
        if (MakeKey(chain.ApexSpid, chain.ApexTranStarted).Equals(key))
            return true;

        foreach (var l in chain.Levels)
        {
            if (MakeKey(l.BlockingSpid, l.BlockingTranStarted).Equals(key)) return true;
            if (MakeKey(l.BlockedSpid, l.BlockedTranStarted).Equals(key)) return true;
        }

        return false;
    }

    public static BlockingReconstruction Reconstruct(
        IEnumerable<BlockingPairRow> rows, int maxDepth, int maxPairs, int stepBudget)
    {
        var pairs = rows.Take(maxPairs).ToList();
        if (pairs.Count == 0)
            return new BlockingReconstruction();

        // Directed graph: blocker -> blocked. Edges deduped by max wait time (a pair
        // re-fires every few seconds with a growing wait), keeping the worst row's detail.
        var adjacency = new Dictionary<SessionKey, Dictionary<SessionKey, EdgeInfo>>();
        var allNodes = new HashSet<SessionKey>();
        var blockedNodes = new HashSet<SessionKey>();
        var sleepingBlockers = new HashSet<SessionKey>();

        foreach (var row in pairs)
        {
            var blocker = MakeKey(row.BlockingSpid, row.BlockingTranStarted);
            var blocked = MakeKey(row.BlockedSpid, row.BlockedTranStarted);

            allNodes.Add(blocker);
            allNodes.Add(blocked);
            blockedNodes.Add(blocked);

            if (string.Equals(row.BlockingStatus, "sleeping", StringComparison.OrdinalIgnoreCase))
                sleepingBlockers.Add(blocker);

            if (blocker.Equals(blocked))
                continue; // a session cannot block itself — guard against degenerate data

            if (!adjacency.TryGetValue(blocker, out var dests))
                adjacency[blocker] = dests = new Dictionary<SessionKey, EdgeInfo>();

            if (!dests.TryGetValue(blocked, out var existing) || row.WaitTimeMs > existing.Row.WaitTimeMs)
            {
                dests[blocked] = new EdgeInfo(row);
            }
        }

        var cycleDetected = HasCycle(allNodes, adjacency);

        // Roots: apexes (blockers that are never blocked). Subgraphs that are pure cycles
        // have no apex — give each a fallback root so the chain is not silently dropped.
        var roots = allNodes.Where(n => adjacency.ContainsKey(n) && !blockedNodes.Contains(n)).ToList();
        AddFallbackRoots(roots, allNodes, blockedNodes, adjacency);

        var steps = stepBudget;
        var depthCapped = false;
        var truncated = false;
        var depthMemo = new Dictionary<SessionKey, int>();

        var chains = new List<ReconstructedChain>(roots.Count);
        foreach (var root in roots)
        {
            var depth = LongestDepth(root, adjacency, maxDepth, !cycleDetected, depthMemo,
                new HashSet<SessionKey>(), ref steps, ref depthCapped, ref truncated);
            var (victimCount, maxWait, levels) = WalkChain(root, adjacency, ref steps, ref truncated);

            var magnitude = Math.Max(
                FactScorer.ApplyThresholdFormula(depth, 3, 8),
                FactScorer.ApplyThresholdFormula(victimCount, 5, 25));

            chains.Add(new ReconstructedChain
            {
                ApexSpid = root.Spid,
                ApexTranStarted = root.TranStarted,
                ApexSleeping = sleepingBlockers.Contains(root),
                Depth = depth,
                VictimCount = victimCount,
                MaxWaitMs = maxWait,
                Magnitude = magnitude,
                Levels = levels
            });
        }

        return new BlockingReconstruction
        {
            Chains = chains.OrderByDescending(c => c.Magnitude)
                           .ThenByDescending(c => c.Depth)
                           .ToList(),
            DepthCapped = depthCapped,
            TraversalTruncated = truncated,
            CycleDetected = cycleDetected
        };
    }

    /// <summary>Kahn's algorithm — true if the graph is not a DAG.</summary>
    private static bool HasCycle(
        HashSet<SessionKey> allNodes,
        Dictionary<SessionKey, Dictionary<SessionKey, EdgeInfo>> adjacency)
    {
        var inDegree = allNodes.ToDictionary(n => n, _ => 0);
        foreach (var dests in adjacency.Values)
            foreach (var dest in dests.Keys)
                inDegree[dest]++;

        var queue = new Queue<SessionKey>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var removed = 0;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            removed++;
            if (adjacency.TryGetValue(node, out var dests))
                foreach (var dest in dests.Keys)
                    if (--inDegree[dest] == 0)
                        queue.Enqueue(dest);
        }

        return removed != allNodes.Count;
    }

    /// <summary>
    /// For any subgraph with no apex (a pure cycle), adds the highest-wait node as a
    /// fallback root so the chain is reconstructed rather than silently dropped.
    /// </summary>
    private static void AddFallbackRoots(
        List<SessionKey> roots,
        HashSet<SessionKey> allNodes,
        HashSet<SessionKey> blockedNodes,
        Dictionary<SessionKey, Dictionary<SessionKey, EdgeInfo>> adjacency)
    {
        var reached = new HashSet<SessionKey>();
        foreach (var root in roots)
            MarkReachable(root, adjacency, reached);

        var orphans = allNodes.Where(n => adjacency.ContainsKey(n) && !reached.Contains(n)).ToList();
        while (orphans.Count > 0)
        {
            // Pick the orphan with the largest outgoing wait time as the fallback root.
            var fallback = orphans
                .OrderByDescending(n => adjacency[n].Values.Max(e => e.Row.WaitTimeMs))
                .First();
            roots.Add(fallback);
            MarkReachable(fallback, adjacency, reached);
            orphans = orphans.Where(n => !reached.Contains(n)).ToList();
        }
    }

    private static void MarkReachable(
        SessionKey start,
        Dictionary<SessionKey, Dictionary<SessionKey, EdgeInfo>> adjacency,
        HashSet<SessionKey> reached)
    {
        var stack = new Stack<SessionKey>();
        if (reached.Add(start))
            stack.Push(start);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (adjacency.TryGetValue(node, out var dests))
                foreach (var dest in dests.Keys)
                    if (reached.Add(dest))
                        stack.Push(dest);
        }
    }

    /// <summary>
    /// Longest downward path (in edges) from a node. Memoized when the graph is a DAG
    /// (memo is path-independent there); on a cyclic graph memo is disabled and the
    /// per-path visited set plus the global step budget bound the traversal.
    /// </summary>
    private static int LongestDepth(
        SessionKey node,
        Dictionary<SessionKey, Dictionary<SessionKey, EdgeInfo>> adjacency,
        int maxDepth,
        bool useMemo,
        Dictionary<SessionKey, int> memo,
        HashSet<SessionKey> path,
        ref int steps,
        ref bool depthCapped,
        ref bool truncated)
    {
        if (useMemo && memo.TryGetValue(node, out var cached))
            return cached;

        if (steps-- <= 0)
        {
            truncated = true;
            return 0;
        }

        if (path.Count >= maxDepth)
        {
            depthCapped = true;
            return 0;
        }

        var best = 0;
        if (adjacency.TryGetValue(node, out var dests))
        {
            path.Add(node);
            foreach (var child in dests.Keys)
            {
                if (path.Contains(child))
                    continue; // cycle guard

                var childDepth = LongestDepth(child, adjacency, maxDepth, useMemo, memo, path,
                    ref steps, ref depthCapped, ref truncated);
                if (1 + childDepth > best)
                    best = 1 + childDepth;
            }
            path.Remove(node);
        }

        if (useMemo)
            memo[node] = best;
        return best;
    }

    /// <summary>
    /// Walks the subtree under a root: distinct transitive victim count, the worst edge
    /// wait time, and a BFS-ordered level list for drill-down.
    /// </summary>
    private static (int VictimCount, long MaxWaitMs, List<ChainLevel> Levels) WalkChain(
        SessionKey root,
        Dictionary<SessionKey, Dictionary<SessionKey, EdgeInfo>> adjacency,
        ref int steps,
        ref bool truncated)
    {
        var victims = new HashSet<SessionKey>();
        var levels = new List<ChainLevel>();
        long maxWait = 0;

        var queue = new Queue<(SessionKey Node, int Level)>();
        var enqueued = new HashSet<SessionKey> { root };
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            if (steps-- <= 0)
            {
                truncated = true;
                break;
            }

            var (node, level) = queue.Dequeue();
            if (!adjacency.TryGetValue(node, out var dests))
                continue;

            foreach (var (child, edge) in dests)
            {
                victims.Add(child);
                var row = edge.Row;
                if (row.WaitTimeMs > maxWait)
                    maxWait = row.WaitTimeMs;

                levels.Add(new ChainLevel
                {
                    Level = level + 1,
                    BlockingSpid = node.Spid,
                    BlockingTranStarted = node.TranStarted,
                    BlockedSpid = child.Spid,
                    BlockedTranStarted = child.TranStarted,
                    LockMode = row.LockMode ?? string.Empty,
                    WaitTimeMs = row.WaitTimeMs,
                    DatabaseName = row.DatabaseName ?? string.Empty,
                    BlockingSqlText = row.BlockingSqlText ?? string.Empty,
                    BlockedSqlText = row.BlockedSqlText ?? string.Empty,
                    BlockedLoginName = row.BlockedLoginName ?? string.Empty,
                    BlockedHostName = row.BlockedHostName ?? string.Empty,
                    BlockedClientApp = row.BlockedClientApp ?? string.Empty,
                    BlockingLoginName = row.BlockingLoginName ?? string.Empty,
                    BlockingHostName = row.BlockingHostName ?? string.Empty,
                    BlockingClientApp = row.BlockingClientApp ?? string.Empty
                });

                if (enqueued.Add(child))
                    queue.Enqueue((child, level + 1));
            }
        }

        return (victims.Count, maxWait, levels);
    }
}
