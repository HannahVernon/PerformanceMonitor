using System;
using System.Collections.Generic;
using System.Linq;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Notifications;
using PerformanceMonitorDashboard.Services;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Dashboard's first notification-formatter tests. Mirrors the Lite suite's
/// FindingMessageFormatter.BuildContext ordering/omission cases plus the
/// AlertContext serialization round-trip (triage v1 PR (b)). Dashboard threads
/// the notify threshold as a method parameter, so BuildContext takes (finding, threshold).
/// </summary>
public class AnalysisNotificationTests
{
    private static AnalysisFinding MakeFinding(
        string hash,
        double severity = 1.8,
        string category = "cpu_pressure",
        int serverId = 1,
        double? rootValue = 92.5,
        Dictionary<string, double>? metadata = null,
        Dictionary<string, object>? drillDown = null,
        string rootFactKey = "CPU_SPIKE")
    {
        return new AnalysisFinding
        {
            ServerId = serverId,
            ServerName = "TestServer",
            Category = category,
            StoryPath = "CPU_SPIKE → PLAN_REGRESSION",
            StoryPathHash = hash,
            Severity = severity,
            Confidence = 0.67,
            FactCount = 2,
            RootFactKey = rootFactKey,
            RootFactValue = rootValue,
            TimeRangeStart = DateTime.UtcNow.AddHours(-4),
            TimeRangeEnd = DateTime.UtcNow,
            RootFactMetadata = metadata,
            DrillDown = drillDown
        };
    }

    private static Dictionary<string, object> RegressedQueriesDrillDown() => new()
    {
        ["regressed_queries"] = new List<object>
        {
            new
            {
                database = "AdventureWorks",
                query_id = 4242L,
                best_plan_id = 17L,
                latest_plan_hash = "0xDEAD",
                best_plan_hash = "0xBEEF",
                latest_cpu_per_exec_us = 9000.0,
                best_cpu_per_exec_us = 1200.0,
                regression_factor = 7.5
            }
        }
    };

    [Fact]
    public void BuildContext_RenderingOrder_DiagnosisAdviceTsqlDrillDown()
    {
        var finding = MakeFinding("planreg000000001", rootFactKey: "PLAN_REGRESSION",
            drillDown: RegressedQueriesDrillDown());

        var context = FindingMessageFormatter.BuildContext(finding, notifyThreshold: 1.5);

        // [0] Diagnosis, [1] Advice prose, [2] Remediation T-SQL, [3] regressed_queries drill-down.
        Assert.Equal(4, context.Details.Count);
        Assert.Equal("Diagnosis", context.Details[0].Heading);

        Assert.NotNull(context.Details[1].Body);
        Assert.False(context.Details[1].IsCodeBlock);
        Assert.Contains("Investigation:", context.Details[1].Body);
        Assert.Contains("Remediation:", context.Details[1].Body);

        Assert.Equal("Remediation T-SQL", context.Details[2].Heading);
        Assert.True(context.Details[2].IsCodeBlock);
        Assert.NotNull(context.Details[2].Body);
        Assert.Contains("sp_query_store_force_plan", context.Details[2].Body);

        Assert.Equal("Regressed Queries", context.Details[3].Heading);
    }

    [Fact]
    public void BuildContext_UnknownFactKey_OmitsAdviceAndTsql()
    {
        var finding = MakeFinding("unknown000000001", rootFactKey: "ZZZ_TEST");

        var context = FindingMessageFormatter.BuildContext(finding, notifyThreshold: 1.5);

        // Diagnosis only — no advice block for an unknown fact key, and no T-SQL.
        var only = Assert.Single(context.Details);
        Assert.Equal("Diagnosis", only.Heading);
        Assert.DoesNotContain(context.Details, d => d.Heading == "Remediation T-SQL");
    }

    [Fact]
    public void BuildContext_NonPlanRegression_OmitsTsqlOnly()
    {
        // CPU_SPIKE has advice but is not PLAN_REGRESSION, so advice renders but no T-SQL.
        var context = FindingMessageFormatter.BuildContext(MakeFinding("cpuonly000000001"), notifyThreshold: 1.5);

        Assert.Contains(context.Details, d => d.Body is not null && !d.IsCodeBlock);
        Assert.DoesNotContain(context.Details, d => d.IsCodeBlock);
        Assert.DoesNotContain(context.Details, d => d.Heading == "Remediation T-SQL");
    }

    [Fact]
    public void AlertContext_SerializesAndDeserializes_PreservingFieldsBodyAndCodeBlock()
    {
        // MINOR-3: round-trip the real BuildContext output for a PLAN_REGRESSION finding (generated
        // T-SQL with newlines/brackets/semicolons/-- comments + drill-down Fields), not a hand-built
        // context, through the production AlertContextSerializer / DTO.
        var finding = MakeFinding("roundtrip0000001", rootFactKey: "PLAN_REGRESSION",
            drillDown: RegressedQueriesDrillDown());
        var context = FindingMessageFormatter.BuildContext(finding, notifyThreshold: 1.5);

        var json = AlertContextSerializer.Serialize(context);
        Assert.True(AlertContextSerializer.TryDeserialize(json, out var restored));

        Assert.Equal(context.Details.Count, restored.Details.Count);
        for (int i = 0; i < context.Details.Count; i++)
        {
            var expected = context.Details[i];
            var actual = restored.Details[i];

            Assert.Equal(expected.Heading, actual.Heading);
            Assert.Equal(expected.Body, actual.Body);
            Assert.Equal(expected.IsCodeBlock, actual.IsCodeBlock);
            Assert.Equal(expected.Fields.Count, actual.Fields.Count);
            for (int j = 0; j < expected.Fields.Count; j++)
            {
                Assert.Equal(expected.Fields[j].Label, actual.Fields[j].Label);
                Assert.Equal(expected.Fields[j].Value, actual.Fields[j].Value);
            }
        }

        // The T-SQL code block in particular must survive intact.
        var tsql = restored.Details.Single(d => d.IsCodeBlock);
        Assert.Contains("sp_query_store_force_plan", tsql.Body);
    }
}
