using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PerformanceMonitorLite;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Tests for Stage 2 finding-notification wiring: the FindingMessageFormatter
/// (message composition + DrillDown mapping) and the AnalysisNotificationService
/// severity filter + per-finding cooldown.
/// </summary>
public class AnalysisNotificationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DuckDbInitializer _duckDb;
    private readonly IAlertSettings _settings = new AppAlertSettings();

    public AnalysisNotificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LiteNotifyTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _duckDb = new DuckDbInitializer(Path.Combine(_tempDir, "test.duckdb"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* Best-effort cleanup */ }
    }

    /// <summary>
    /// Builds the shared AnalysisNotificationService wired to a real Lite EmailAlertService
    /// (its IFindingAlertSender) over the test DuckDB store, with Lite's serverId resolver.
    /// </summary>
    private AnalysisNotificationService MakeNotifier(
        Func<string, bool>? isServerSilenced = null,
        Action<string, string>? showTrayNotification = null)
    {
        var webhook = new WebhookAlertService(_settings, EmailAlertService.Branding, new AppLoggerAdapter<WebhookAlertService>());
        var email = new EmailAlertService(_settings, new DuckDbAlertHistoryStore(_duckDb), webhook, new AppLoggerAdapter<EmailAlertService>());
        return new AnalysisNotificationService(
            email, _settings, f => f.ServerId.ToString(), new AppLoggerAdapter<AnalysisNotificationService>(),
            isServerSilenced, showTrayNotification);
    }

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

    /* ── FindingMessageFormatter ── */

    [Fact]
    public void MetricName_AppendsShortHash_SoDistinctFindingsDoNotCollide()
    {
        // Two distinct findings sharing one coarse Category must yield distinct metric
        // names, or EmailAlertService's {serverId}:{metricName} cooldown collapses them.
        var a = FindingMessageFormatter.MetricName(MakeFinding("aaaaaaaa11111111", category: "cpu_pressure"));
        var b = FindingMessageFormatter.MetricName(MakeFinding("bbbbbbbb22222222", category: "cpu_pressure"));

        Assert.Equal("Analysis: cpu_pressure [aaaaaaaa]", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CurrentValue_NullRootValue_EmitsKeyOnly()
    {
        var value = FindingMessageFormatter.CurrentValue(MakeFinding("h", rootValue: null));

        Assert.Equal("CPU_SPIKE", value);
        Assert.DoesNotContain("()", value);
    }

    [Fact]
    public void CurrentValue_AnomalyFinding_IncludesFlattenedBaselineContext()
    {
        var finding = MakeFinding("h", metadata: new Dictionary<string, double>
        {
            ["deviation_sigma"] = 4.1,
            ["baseline_mean"] = 68.2,
            ["baseline_hour"] = 14,
            ["baseline_dow"] = 2
        });

        var value = FindingMessageFormatter.CurrentValue(finding);

        Assert.Contains("4.1σ", value);
        Assert.Contains("Tue 14:00", value);
    }

    [Fact]
    public void BuildContext_MapsAnonymousListAndObjectDrillDown()
    {
        var finding = MakeFinding("h", drillDown: new Dictionary<string, object>
        {
            ["top_cpu_queries"] = new List<object>
            {
                new { query_hash = "0x1", avg_cpu_ms = 250.0, executions = 1000L },
                new { query_hash = "0x2", avg_cpu_ms = 90.0, executions = 500L }
            },
            ["spike_peak"] = new { peak_time = "2026-05-22T10:00:00Z", cpu_percent = 99 }
        });

        var context = FindingMessageFormatter.BuildContext(finding, notifyThreshold: 1.5);

        // Details[0] = Diagnosis, [1] = Advice (CPU_SPIKE has an advice block), then the two drill-downs.
        Assert.Equal(4, context.Details.Count);
        Assert.Equal("Diagnosis", context.Details[0].Heading);
        Assert.NotNull(context.Details[1].Body);
        Assert.False(context.Details[1].IsCodeBlock);

        var queries = context.Details.Single(d => d.Heading == "Top Cpu Queries");
        Assert.Contains(queries.Fields, f => f.Label.StartsWith("#1") && f.Value == "0x1");

        var peak = context.Details.Single(d => d.Heading == "Spike Peak");
        Assert.Contains(peak.Fields, f => f.Label == "Cpu Percent" && f.Value == "99");
    }

    [Fact]
    public void BuildContext_CapsListAtThreeItems()
    {
        var finding = MakeFinding("h", drillDown: new Dictionary<string, object>
        {
            ["items"] = new List<object>
            {
                new { n = 1 }, new { n = 2 }, new { n = 3 }, new { n = 4 }, new { n = 5 }
            }
        });

        var context = FindingMessageFormatter.BuildContext(finding, notifyThreshold: 1.5);

        // Diagnosis at [0], Advice at [1], drill-down "Items" at [2]. 3 items kept x 1 property each = 3 fields; #4 and #5 dropped.
        Assert.Equal(3, context.Details.Count);
        var item = context.Details.Single(d => d.Heading == "Items");
        Assert.Equal(3, item.Fields.Count);
        Assert.DoesNotContain(item.Fields, f => f.Label.StartsWith("#4"));
    }

    [Fact]
    public void BuildContext_NoDrillDown_StillEmitsDiagnosis()
    {
        var context = FindingMessageFormatter.BuildContext(MakeFinding("h"), notifyThreshold: 1.5);
        // Diagnosis card + the CPU_SPIKE advice block, even with no DrillDown.
        Assert.Equal(2, context.Details.Count);
        Assert.Equal("Diagnosis", context.Details[0].Heading);
        Assert.NotNull(context.Details[1].Body);
    }

    [Fact]
    public void BuildContext_DiagnosisCarriesNotifyThreshold()
    {
        var context = FindingMessageFormatter.BuildContext(MakeFinding("h"), notifyThreshold: 1.7);
        var diagnosis = context.Details[0];
        Assert.Equal("Diagnosis", diagnosis.Heading);
        Assert.Contains(diagnosis.Fields, f => f.Label == "Notify threshold" && f.Value == "1.7");
        Assert.Contains(diagnosis.Fields, f => f.Label == "Story" && f.Value == "CPU_SPIKE → PLAN_REGRESSION");
    }

    [Fact]
    public void DetailText_CarriesStoryPathAndChainMetadata()
    {
        var text = FindingMessageFormatter.DetailText(MakeFinding("h"), notifyThreshold: 1.5);

        Assert.Contains("CPU_SPIKE → PLAN_REGRESSION", text);
        Assert.Contains("Severity", text);
        Assert.Contains("Confidence", text);
    }

    /* ── Advice + remediation T-SQL rendering (triage v1 PR (b)) ── */

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

    /* ── AnalysisNotificationService: severity filter + cooldown ── */
    /* TrySendAlertEmailAsync writes one config_alert_log row per call (regardless of
       whether a channel is configured), so the row count is an observable proxy for
       "did the service decide to notify". */

    [Fact]
    public async Task NotifyAsync_SameFinding_NotifiesOnceWithinCooldown()
    {
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;
        App.AnalysisNotifyCooldownMinutes = 360;

        var notifier = MakeNotifier();
        var finding = MakeFinding("samehash00000001", severity: 2.0);

        await notifier.NotifyAsync(new[] { finding });
        await notifier.NotifyAsync(new[] { finding });

        Assert.Equal(1, await CountAlertLogRowsAsync());
    }

    [Fact]
    public async Task NotifyAsync_DistinctFindings_EachNotifies()
    {
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;
        App.AnalysisNotifyCooldownMinutes = 360;

        var notifier = MakeNotifier();

        /* Use distinct first-8-char prefixes — FindingMessageFormatter.MetricName
           embeds only the first 8 chars of StoryPathHash, and the persistence
           seed (PR plan 4 PR (b)) keys on metric_name, so two findings sharing
           a shortHash would seed from each other's alert_log row. Documented
           and accepted as a collision risk in the plan; the test exercises the
           non-collision case. */
        await notifier.NotifyAsync(new[]
        {
            MakeFinding("aaaa000000000001", severity: 2.0),
            MakeFinding("bbbb000000000002", severity: 2.0)
        });

        Assert.Equal(2, await CountAlertLogRowsAsync());
    }

    [Fact]
    public async Task NotifyAsync_BelowSeverityThreshold_DoesNotNotify()
    {
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;

        var notifier = MakeNotifier();
        await notifier.NotifyAsync(new[] { MakeFinding("lowsev0000000001", severity: 1.0) });

        Assert.Equal(0, await CountAlertLogRowsAsync());
    }

    [Fact]
    public async Task NotifyAsync_SilencedServer_DoesNotNotify()
    {
        // Issue #1035: a server silenced via "Silence All Alerts" must not produce
        // analysis-finding emails. The predicate is keyed by resolved serverId ("1").
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;
        App.AnalysisNotifyCooldownMinutes = 360;

        var notifier = MakeNotifier(serverId => serverId == "1");
        await notifier.NotifyAsync(new[] { MakeFinding("silenced00000001", severity: 2.0, serverId: 1) });

        Assert.Equal(0, await CountAlertLogRowsAsync());
    }

    [Fact]
    public async Task NotifyAsync_SilencePredicate_OnlySuppressesMatchingServer()
    {
        // The predicate is per-server: a finding for an unsilenced server still notifies
        // even while another server is silenced, and a silenced server consuming no
        // cooldown means unsilencing resumes immediately on the next cycle.
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;
        App.AnalysisNotifyCooldownMinutes = 360;

        var notifier = MakeNotifier(serverId => serverId == "1");
        await notifier.NotifyAsync(new[]
        {
            MakeFinding("aaaa000000000001", severity: 2.0, serverId: 1), // silenced — suppressed
            MakeFinding("bbbb000000000002", severity: 2.0, serverId: 2)  // not silenced — notifies
        });

        Assert.Equal(1, await CountAlertLogRowsAsync());
    }

    /* ── WS2: always raise a tray balloon for a notify-worthy finding ── */

    [Fact]
    public async Task NotifyAsync_NotifyWorthyFinding_RaisesTrayBalloon()
    {
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;
        App.AnalysisNotifyCooldownMinutes = 360;

        var balloons = new List<(string Title, string Message)>();
        var notifier = MakeNotifier(showTrayNotification: (t, m) => balloons.Add((t, m)));

        await notifier.NotifyAsync(new[] { MakeFinding("tray000000000001", severity: 2.0, category: "cpu_pressure") });

        var balloon = Assert.Single(balloons);
        Assert.Contains("cpu_pressure", balloon.Title);
        Assert.Contains("TestServer", balloon.Title);
        Assert.False(string.IsNullOrWhiteSpace(balloon.Message));
    }

    [Fact]
    public async Task NotifyAsync_BelowThreshold_RaisesNoTrayBalloon()
    {
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;

        var balloons = new List<(string, string)>();
        var notifier = MakeNotifier(showTrayNotification: (t, m) => balloons.Add((t, m)));

        await notifier.NotifyAsync(new[] { MakeFinding("traylow000000001", severity: 1.0) });

        Assert.Empty(balloons);   // sub-threshold finding -> no email, no tray
    }

    [Fact]
    public async Task NotifyAsync_SilencedServer_RaisesNoTrayBalloon()
    {
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;
        App.AnalysisNotifyCooldownMinutes = 360;

        var balloons = new List<(string, string)>();
        var notifier = MakeNotifier(serverId => serverId == "1", showTrayNotification: (t, m) => balloons.Add((t, m)));

        await notifier.NotifyAsync(new[] { MakeFinding("traysilenced0001", severity: 2.0, serverId: 1) });

        Assert.Empty(balloons);   // silenced server -> no tray either
    }

    [Fact]
    public void BalloonText_NamesCategoryAndServer_AndCarriesHeadline()
    {
        var finding = MakeFinding("balloon000000001", severity: 2.0, category: "blocking",
            rootFactKey: "BLOCKING_CHAIN", rootValue: 5);

        var (title, message) = FindingMessageFormatter.BalloonText(finding);

        Assert.Contains("blocking", title);
        Assert.Contains("TestServer", title);
        Assert.Contains("BLOCKING_CHAIN", message);
    }

    private async Task<long> CountAlertLogRowsAsync()
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM config_alert_log";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
