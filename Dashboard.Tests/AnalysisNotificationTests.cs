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

    [Fact]
    public void BuildContext_PlanRegression_AttachesStructuredRemediationToCodeBlock()
    {
        var finding = MakeFinding("remstruct0000001", rootFactKey: "PLAN_REGRESSION",
            drillDown: RegressedQueriesDrillDown());

        var context = FindingMessageFormatter.BuildContext(finding, notifyThreshold: 1.5);

        var tsql = context.Details.Single(d => d.IsCodeBlock);
        Assert.NotNull(tsql.Remediation);
        Assert.Equal("PLAN_REGRESSION", tsql.Remediation!.FactKey);
        Assert.Equal("force", tsql.Remediation.Action);
        var target = Assert.Single(tsql.Remediation.Targets);
        Assert.Equal("AdventureWorks", target.Database);
        Assert.Equal(4242, target.QueryId);
        Assert.Equal(17, target.PlanId);
    }

    [Fact]
    public void AlertContext_RemediationAction_SurvivesRoundTrip()
    {
        var finding = MakeFinding("remround00000001", rootFactKey: "PLAN_REGRESSION",
            drillDown: RegressedQueriesDrillDown());
        var context = FindingMessageFormatter.BuildContext(finding, notifyThreshold: 1.5);

        var json = AlertContextSerializer.Serialize(context);
        Assert.True(AlertContextSerializer.TryDeserialize(json, out var restored));

        var rem = restored.Details.Single(d => d.IsCodeBlock).Remediation;
        Assert.NotNull(rem);
        Assert.Equal("PLAN_REGRESSION", rem!.FactKey);
        Assert.Equal("force", rem.Action);
        var t = Assert.Single(rem.Targets);
        Assert.Equal("AdventureWorks", t.Database);
        Assert.Equal(4242, t.QueryId);
        Assert.Equal(17, t.PlanId);
        Assert.Equal("0xBEEF", t.BestPlanHash);
        Assert.Equal(7.5, t.RegressionFactor);
    }

    [Fact]
    public void AlertContext_DbConfigAction_DbConfigTargetsSurviveRoundTrip()
    {
        // m-A: a DB_CONFIG action must come back with its DbConfigTargets intact after
        // a full serialize -> deserialize. A 3-arg FromDto ctor call would silently
        // drop them (action survives but un-applyable); this pins they survive.
        var action = new RemediationAction("DB_CONFIG", "set",
            Array.Empty<ForcePlanTarget>(),
            new List<DbConfigTarget>
            {
                new("Foo", DbConfigSetting.AutoShrinkOff, "ON"),
                new("Foo", DbConfigSetting.PageVerifyChecksum, "TORN_PAGE_DETECTION")
            });

        var context = new AlertContext();
        context.Details.Add(new AlertDetailItem
        {
            Heading = "Remediation T-SQL",
            IsCodeBlock = true,
            Body = "ALTER DATABASE [Foo] SET AUTO_SHRINK OFF;",
            Remediation = action
        });

        var json = AlertContextSerializer.Serialize(context);
        Assert.True(AlertContextSerializer.TryDeserialize(json, out var restored));

        var rem = restored.Details.Single(d => d.IsCodeBlock).Remediation;
        Assert.NotNull(rem);
        Assert.Equal("DB_CONFIG", rem!.FactKey);
        Assert.Equal("set", rem.Action);
        Assert.Empty(rem.Targets);                       // force-plan list empty
        Assert.NotNull(rem.DbConfigTargets);
        Assert.Equal(2, rem.DbConfigTargets!.Count);
        Assert.Equal("Foo", rem.DbConfigTargets[0].Database);
        Assert.Equal(DbConfigSetting.AutoShrinkOff, rem.DbConfigTargets[0].Setting);
        Assert.Equal("ON", rem.DbConfigTargets[0].CurrentValue);
        Assert.Equal(DbConfigSetting.PageVerifyChecksum, rem.DbConfigTargets[1].Setting);
        Assert.Equal("TORN_PAGE_DETECTION", rem.DbConfigTargets[1].CurrentValue);
    }

    [Fact]
    public void AlertContext_ForcePlanAction_DbConfigTargetsNull_AfterRoundTrip()
    {
        // A force-plan action has no DbConfigTargets; it must round-trip to null
        // (not an empty list that would imply a DB_CONFIG action).
        var finding = MakeFinding("remround00000002", rootFactKey: "PLAN_REGRESSION",
            drillDown: RegressedQueriesDrillDown());
        var context = FindingMessageFormatter.BuildContext(finding, notifyThreshold: 1.5);

        var json = AlertContextSerializer.Serialize(context);
        Assert.True(AlertContextSerializer.TryDeserialize(json, out var restored));

        var rem = restored.Details.Single(d => d.IsCodeBlock).Remediation;
        Assert.NotNull(rem);
        Assert.Null(rem!.DbConfigTargets);
    }

    // ── B3 Phase 3 (PR-B): second "Enable RCSI (advanced)" detail item + cross-surface ──

    private static AnalysisFinding DbConfigRcsiFinding(
        bool rcsiOff = true, bool enrichment = true, bool alsoAutoShrink = false,
        int blocking = 12, int deadlocks = 3, int? rwPct = 80, string hash = "dbconfig00000099")
    {
        var row = new Dictionary<string, object?>
        {
            ["database"] = "Foo",
            ["recovery_model"] = "FULL",
            ["rcsi"] = !rcsiOff,                  // rcsi == true means RCSI is ON
            ["query_store"] = true,
            ["issues"] = rcsiOff ? new[] { "RCSI OFF" } : System.Array.Empty<string>(),
            ["auto_shrink"] = alsoAutoShrink,
            ["auto_close"] = false,
            ["page_verify"] = "CHECKSUM",
        };
        if (enrichment)
        {
            row["rcsi_blocking_events"] = blocking;
            row["rcsi_deadlocks"] = deadlocks;
            row["rcsi_reader_writer_pct"] = rwPct;
        }
        return MakeFinding(hash, rootFactKey: "DB_CONFIG", category: "config_issues",
            drillDown: new Dictionary<string, object> { ["config_issues"] = new List<object> { row } });
    }

    [Fact]
    public void BuildContext_RcsiOffWithEnrichment_EmitsSecondEnableRcsiItem()
    {
        var context = FindingMessageFormatter.BuildContext(DbConfigRcsiFinding(), notifyThreshold: 1.5);

        var rcsiItem = Assert.Single(context.Details, d => d.Heading == "Enable RCSI (advanced)");
        Assert.True(rcsiItem.IsCodeBlock);
        Assert.NotNull(rcsiItem.Remediation);
        Assert.Equal("RCSI", rcsiItem.Remediation!.FactKey);
        Assert.Contains("SET READ_COMMITTED_SNAPSHOT ON", rcsiItem.Body);
        // The figures rode the action (captured while the finding was in hand).
        Assert.NotNull(rcsiItem.Remediation.RcsiFigures);
        Assert.Equal(12, rcsiItem.Remediation.RcsiFigures!.BlockingEvents);

        // The cross-surface disclosure item is also present (read-only, both email bodies + webhook).
        var disclosure = Assert.Single(context.Details, d => d.Heading == "RCSI — risks of changing / not changing");
        Assert.False(disclosure.IsCodeBlock);
        Assert.Contains("Risks of CHANGING", disclosure.Body);
        Assert.Contains("Risks of NOT changing", disclosure.Body);
        Assert.Contains("RCSI eliminates", disclosure.Body);   // 80% reader/writer
    }

    [Fact]
    public void BuildContext_RcsiOn_EmitsNoEnableRcsiItem()
    {
        // RCSI already ON -> the affordance must NOT appear (M2-1 polarity).
        var context = FindingMessageFormatter.BuildContext(DbConfigRcsiFinding(rcsiOff: false), notifyThreshold: 1.5);
        Assert.DoesNotContain(context.Details, d => d.Heading == "Enable RCSI (advanced)");
        Assert.DoesNotContain(context.Details, d => d.Heading == "RCSI — risks of changing / not changing");
    }

    [Fact]
    public void BuildContext_RcsiOffNoEnrichment_EmitsNoEnableRcsiItem()
    {
        // Legacy alert: RCSI off but no §3.3 enrichment -> no affordance.
        var context = FindingMessageFormatter.BuildContext(DbConfigRcsiFinding(enrichment: false), notifyThreshold: 1.5);
        Assert.DoesNotContain(context.Details, d => d.Heading == "Enable RCSI (advanced)");
    }

    [Fact]
    public void BuildContext_RcsiItem_NotGatedBehindAlwaysSafeRemediationTsql()
    {
        // The residual round-2 note: emit the RCSI item on ANY config_issues-bearing
        // finding, NOT only when the always-safe block emitted a Remediation T-SQL item.
        // Here ONLY RCSI is wrong (no auto_shrink/auto_close/page_verify issue), so the
        // always-safe BuildAction yields null and NO "Remediation T-SQL" item is emitted —
        // yet the RCSI item must still appear.
        var context = FindingMessageFormatter.BuildContext(DbConfigRcsiFinding(alsoAutoShrink: false), notifyThreshold: 1.5);

        Assert.DoesNotContain(context.Details, d => d.Heading == "Remediation T-SQL");
        Assert.Contains(context.Details, d => d.Heading == "Enable RCSI (advanced)");
    }

    [Fact]
    public void BuildContext_BothItems_WhenAlsoAlwaysSafeIssue()
    {
        // RCSI off + an always-safe issue (auto_shrink ON): BOTH the always-safe
        // "Remediation T-SQL" item AND the separate "Enable RCSI (advanced)" item appear,
        // each with its own singular Remediation. The always-safe one never carries RCSI.
        var context = FindingMessageFormatter.BuildContext(DbConfigRcsiFinding(alsoAutoShrink: true), notifyThreshold: 1.5);

        var safe = Assert.Single(context.Details, d => d.Heading == "Remediation T-SQL");
        Assert.Equal("DB_CONFIG", safe.Remediation!.FactKey);
        Assert.All(safe.Remediation.DbConfigTargets!, t => Assert.NotEqual(DbConfigSetting.ReadCommittedSnapshotOn, t.Setting));

        var rcsi = Assert.Single(context.Details, d => d.Heading == "Enable RCSI (advanced)");
        Assert.Equal("RCSI", rcsi.Remediation!.FactKey);
    }

    [Fact]
    public void BuildContext_RcsiItem_RemediationActionAndFigures_SurviveRoundTrip()
    {
        var context = FindingMessageFormatter.BuildContext(DbConfigRcsiFinding(), notifyThreshold: 1.5);
        var json = AlertContextSerializer.Serialize(context);
        Assert.True(AlertContextSerializer.TryDeserialize(json, out var restored));

        var rcsi = Assert.Single(restored.Details, d => d.Heading == "Enable RCSI (advanced)");
        Assert.NotNull(rcsi.Remediation);
        Assert.Equal("RCSI", rcsi.Remediation!.FactKey);
        var t = Assert.Single(rcsi.Remediation.DbConfigTargets!);
        Assert.Equal(DbConfigSetting.ReadCommittedSnapshotOn, t.Setting);
        // The real figures survive the persistence round-trip.
        Assert.NotNull(rcsi.Remediation.RcsiFigures);
        Assert.Equal(12, rcsi.Remediation.RcsiFigures!.BlockingEvents);
        Assert.Equal(3, rcsi.Remediation.RcsiFigures.Deadlocks);
        Assert.Equal(80, rcsi.Remediation.RcsiFigures.ReaderWriterPct);
    }

    [Fact]
    public void CrossSurface_RiskDisclosure_RendersInBothEmailBodiesAndWebhook()
    {
        var context = FindingMessageFormatter.BuildContext(DbConfigRcsiFinding(), notifyThreshold: 1.5);
        var branding = EmailAlertService.Branding;

        // Both email bodies flow from context.Details — the disclosure must render in EACH
        // (feedback_emailtemplatebuilder_two_bodies: HTML + plain-text are separate bodies).
        var (html, plain) = EmailTemplateBuilder.BuildAlertEmail(
            "High CPU", "TestServer", "n/a", "n/a", 15, branding, context);

        foreach (var body in new[] { html, plain })
        {
            Assert.Contains("RCSI — risks of changing / not changing", body);
            Assert.Contains("Risks of CHANGING", body);
            Assert.Contains("Risks of NOT changing", body);
            Assert.Contains("RCSI eliminates", body);
        }

        // Webhook payloads (Teams + Slack) also flow from context.Details.
        var teams = WebhookAlertService.BuildTeamsPayload(
            "High CPU", "TestServer", "n/a", "n/a", branding, context: context);
        var slack = WebhookAlertService.BuildSlackPayload(
            "High CPU", "TestServer", "n/a", "n/a", branding, context: context);

        foreach (var payload in new[] { teams, slack })
        {
            Assert.Contains("Risks of NOT changing", payload);
            Assert.Contains("RCSI eliminates", payload);
        }
    }

    [Fact]
    public void AlertContext_LegacyJsonWithoutRemediation_DeserializesToNull()
    {
        // A persisted context written before the Remediation field existed: a code
        // block with no "Remediation" property. It must deserialize cleanly with
        // Remediation == null (no Apply button, no crash) — backward compatibility.
        const string legacyJson =
            "{\"Details\":[{\"Heading\":\"Remediation T-SQL\",\"Fields\":[]," +
            "\"Body\":\"EXEC sys.sp_query_store_force_plan @query_id = 1, @plan_id = 2;\",\"IsCodeBlock\":true}]}";

        Assert.True(AlertContextSerializer.TryDeserialize(legacyJson, out var restored));
        var item = Assert.Single(restored.Details);
        Assert.True(item.IsCodeBlock);
        Assert.Null(item.Remediation);
        Assert.Contains("sp_query_store_force_plan", item.Body);
    }
}
