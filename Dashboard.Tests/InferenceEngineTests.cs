using System.Collections.Generic;
using PerformanceMonitor.Analysis;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Tests the InferenceEngine and RelationshipGraph against seeded scenarios.
/// Validates that stories are built with correct paths and severity ordering.
/// </summary>
public class InferenceEngineTests
{
    /* ── Unit tests: graph edge evaluation ── */

    [Fact]
    public void Graph_NoEdgesForUnknownFact()
    {
        var graph = new RelationshipGraph();
        var facts = new Dictionary<string, Fact>();

        var edges = graph.GetActiveEdges("UNKNOWN_THING", facts);
        Assert.Empty(edges);
    }

    [Fact]
    public void Graph_CxPacketEdgeFires_WhenSosIsHigh()
    {
        var graph = new RelationshipGraph();
        var facts = new Dictionary<string, Fact>
        {
            ["SOS_SCHEDULER_YIELD"] = new() { Key = "SOS_SCHEDULER_YIELD", Value = 0.50, Severity = 0.67 }
        };

        var edges = graph.GetActiveEdges("CXPACKET", facts);
        Assert.Contains(edges, e => e.Destination == "SOS_SCHEDULER_YIELD");
    }

    [Fact]
    public void Graph_CxPacketEdgeDoesNotFire_WhenSosIsLow()
    {
        var graph = new RelationshipGraph();
        var facts = new Dictionary<string, Fact>
        {
            ["SOS_SCHEDULER_YIELD"] = new() { Key = "SOS_SCHEDULER_YIELD", Value = 0.10, Severity = 0.13 }
        };

        var edges = graph.GetActiveEdges("CXPACKET", facts);
        Assert.DoesNotContain(edges, e => e.Destination == "SOS_SCHEDULER_YIELD");
    }

    // WS3: a config-advisory fact (DB_CONFIG/SERVER_CONFIG) roots a standalone recommendation
    // at its base severity (e.g. RCSI-off = 0.3), below the 0.5 incident threshold — so a
    // standing misconfig surfaces on a quiet, healthy server. An incident fact at the same
    // severity does NOT root.
    [Fact]
    public void ConfigFact_RootsStandalone_BelowMinimumSeverity()
    {
        var engine = new InferenceEngine(new RelationshipGraph());
        var facts = new List<Fact>
        {
            new() { Key = "DB_CONFIG", Source = "config", Value = 1, Severity = 0.3,
                    Metadata = new Dictionary<string, double> { ["rcsi_off_count"] = 9 } }
        };

        var stories = engine.BuildStories(facts);

        Assert.Contains(stories, s => s.RootFactKey == "DB_CONFIG");
    }

    [Fact]
    public void IncidentFact_BelowMinimumSeverity_DoesNotRoot()
    {
        var engine = new InferenceEngine(new RelationshipGraph());
        var facts = new List<Fact>
        {
            new() { Key = "CPU_SQL_PERCENT", Source = "cpu", Value = 60, Severity = 0.3 }
        };

        var stories = engine.BuildStories(facts);

        Assert.DoesNotContain(stories, s => s.RootFactKey == "CPU_SQL_PERCENT");
    }

    // WS3: a FILE_AUTOGROWTH_PERCENT fact at its 0.3 advisory base roots a standalone
    // recommendation, below the 0.5 incident threshold — because it is a config-advisory root
    // key. Mirrors ConfigFact_RootsStandalone_BelowMinimumSeverity for the new key.
    [Fact]
    public void FileAutogrowthPercentFact_RootsStandalone_BelowMinimumSeverity()
    {
        var engine = new InferenceEngine(new RelationshipGraph());
        var facts = new List<Fact>
        {
            new() { Key = "FILE_AUTOGROWTH_PERCENT", Source = "config", Value = 2, Severity = 0.3,
                    Metadata = new Dictionary<string, double> { ["file_count"] = 2 } }
        };

        var stories = engine.BuildStories(facts);

        Assert.Contains(stories, s => s.RootFactKey == "FILE_AUTOGROWTH_PERCENT");
    }
}
