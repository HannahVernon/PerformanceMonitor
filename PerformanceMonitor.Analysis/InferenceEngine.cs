using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// Greedy traversal engine that builds analysis stories from scored facts
/// and the relationship graph.
///
/// Algorithm:
/// 1. Start at the highest-severity fact as entry point
/// 2. Evaluate all edge predicates from current node
/// 3. Follow edge to highest-severity destination (that hasn't been visited)
/// 4. Repeat until leaf (no active edges or all destinations visited)
/// 5. The path IS the story
/// 6. Mark traversed facts as consumed, repeat from next highest-severity
/// 7. Stop when remaining facts are below 0.5 severity
/// </summary>
public class InferenceEngine
{
    private const double MinimumSeverityThreshold = 0.5;
    private const int MaxPathDepth = 10; // Safety limit

    /// <summary>
    /// Config-advisory fact keys that root a finding at ANY positive severity, bypassing the
    /// MinimumSeverityThreshold. A standing misconfiguration (RCSI off, auto-shrink on, MAXDOP at
    /// a silly default) is an advisory the operator should see regardless of current load — unlike
    /// an incident fact, which must clear 0.5 to be worth surfacing. The existing severity-ordered
    /// <c>consumed</c> traversal still suppresses duplicates: a higher-severity incident story that
    /// consumes the config fact (e.g. CXPACKET → CONFIG_MAXDOP, or LCK_M_S → DB_CONFIG) wins, and
    /// only an UN-consumed config fact roots a standalone recommendation.
    /// </summary>
    private static readonly HashSet<string> ConfigAdvisoryRootKeys = new(StringComparer.Ordinal)
    {
        "DB_CONFIG",
        "SERVER_CONFIG",
        "FILE_AUTOGROWTH_PERCENT",
        // WS3: bad server-level config. Each per-setting CONFIG_* fact scores 0.4 ONLY when
        // bad (FactScorer) and roots its own standalone advisory card here, bypassing the 0.5
        // incident threshold — a standing misconfig should surface on a quiet, healthy server.
        "CONFIG_MAXDOP",
        "CONFIG_CTFP",
        "CONFIG_MAX_MEMORY_MB",
        "CONFIG_MIN_MAX_MEMORY_NARROW",
        // WS5: server-health advisories (advise-only). Each scores its 0.4 advisory base only when
        // bad (FactScorer) and roots its own standalone card here, bypassing the 0.5 incident
        // threshold — a standing server-health gap should surface on a quiet, healthy server.
        "CONFIG_IFI_DISABLED",
        "CONFIG_LPIM_DISABLED",
        "SERVER_MEMORY_DUMPS",
        // WS4: plan-XML advisories (advise-only) parsed from the top collected query plans —
        // missing indexes and actionable plan warnings each root their own standalone advisory card.
        "MISSING_INDEX",
        "PLAN_WARNING",
    };

    private readonly RelationshipGraph _graph;

    public InferenceEngine(RelationshipGraph graph)
    {
        _graph = graph;
    }

    /// <summary>
    /// Builds analysis stories by traversing the relationship graph
    /// starting from the highest-severity facts.
    /// </summary>
    public List<AnalysisStory> BuildStories(List<Fact> facts)
    {
        var stories = new List<AnalysisStory>();
        var factsByKey = facts
            .Where(f => f.Severity > 0)
            .ToFactLookup();
        var consumed = new HashSet<string>();

        // Process facts in severity order. Incident facts must clear the 0.5 threshold to root;
        // config-advisory facts (DB_CONFIG/SERVER_CONFIG) root at any positive severity so a
        // standing misconfig surfaces on a quiet, healthy server (it would otherwise never reach
        // 0.5 without contention — e.g. RCSI-off is base 0.3). Severity ordering + `consumed`
        // (below) keep an incident story from being shadowed by, or duplicating, its config leaf.
        var entryPoints = facts
            .Where(f => f.Severity >= MinimumSeverityThreshold
                     || (ConfigAdvisoryRootKeys.Contains(f.Key) && f.Severity > 0))
            .OrderByDescending(f => f.Severity)
            .ToList();

        foreach (var entryFact in entryPoints)
        {
            if (consumed.Contains(entryFact.Key))
                continue;

            var path = Traverse(entryFact.Key, factsByKey, consumed);

            // Mark all facts in this path as consumed
            foreach (var node in path)
                consumed.Add(node);

            var story = BuildStory(path, factsByKey);
            stories.Add(story);
        }

        // Check for absolution — if no stories were generated at all
        if (stories.Count == 0 && facts.Count > 0)
        {
            stories.Add(new AnalysisStory
            {
                RootFactKey = "server_health",
                RootFactValue = 0,
                Severity = 0,
                Confidence = 1.0,
                Category = "absolution",
                Path = ["server_health"],
                StoryPath = "server_health",
                StoryPathHash = ComputeHash("server_health"),
                StoryText = string.Empty,
                IsAbsolution = true
            });
        }

        return stories;
    }

    /// <summary>
    /// Greedy traversal from an entry point through the relationship graph.
    /// Returns the path as a list of fact keys.
    /// </summary>
    private List<string> Traverse(string startKey,
        Dictionary<string, Fact> factsByKey,
        HashSet<string> consumed)
    {
        var path = new List<string> { startKey };
        var visited = new HashSet<string> { startKey };
        var current = startKey;

        for (var depth = 0; depth < MaxPathDepth; depth++)
        {
            var activeEdges = _graph.GetActiveEdges(current, factsByKey);

            // Filter to destinations not already in this path and not consumed by prior stories
            var candidates = activeEdges
                .Where(e => !visited.Contains(e.Destination) && !consumed.Contains(e.Destination))
                .Where(e => factsByKey.ContainsKey(e.Destination))
                .OrderByDescending(e => factsByKey[e.Destination].Severity)
                .ToList();

            if (candidates.Count == 0)
                break; // Leaf node — no more edges to follow

            var best = candidates[0];
            path.Add(best.Destination);
            visited.Add(best.Destination);
            current = best.Destination;
        }

        return path;
    }

    /// <summary>
    /// Builds an AnalysisStory from a traversal path.
    /// </summary>
    private static AnalysisStory BuildStory(List<string> path, Dictionary<string, Fact> factsByKey)
    {
        var rootFact = factsByKey.GetValueOrDefault(path[0]);
        var leafKey = path.Count > 1 ? path[^1] : null;
        var leafFact = leafKey != null ? factsByKey.GetValueOrDefault(leafKey) : null;

        var storyPath = string.Join(" → ", path);
        var category = rootFact?.Source ?? "unknown";

        // Confidence = what fraction of edge destinations had matching facts
        // For single-node paths, confidence is 1.0 (we found the symptom, just no deeper cause)
        var confidence = path.Count == 1 ? 1.0 : (path.Count - 1.0) / path.Count;

        return new AnalysisStory
        {
            RootFactKey = path[0],
            RootFactValue = rootFact?.Severity ?? 0,
            Severity = rootFact?.Severity ?? 0,
            Confidence = confidence,
            Category = category,
            Path = path,
            StoryPath = storyPath,
            StoryPathHash = ComputeHash(storyPath),
            StoryText = string.Empty,
            LeafFactKey = leafKey,
            LeafFactValue = leafFact?.Severity,
            FactCount = path.Count,
            IsAbsolution = false,
            RootFactMetadata = rootFact?.Metadata
        };
    }

    /// <summary>
    /// Stable hash for story path deduplication and muting.
    /// </summary>
    private static string ComputeHash(string storyPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(storyPath));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }
}
