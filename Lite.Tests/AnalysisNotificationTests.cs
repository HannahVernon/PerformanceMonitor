using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PerformanceMonitorLite;
using PerformanceMonitorLite.Analysis;
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

    private static AnalysisFinding MakeFinding(
        string hash,
        double severity = 1.8,
        string category = "cpu_pressure",
        int serverId = 1,
        double? rootValue = 92.5,
        Dictionary<string, double>? metadata = null,
        Dictionary<string, object>? drillDown = null)
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
            RootFactKey = "CPU_SPIKE",
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

        var context = FindingMessageFormatter.BuildContext(finding);

        Assert.Equal(2, context.Details.Count);

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

        var context = FindingMessageFormatter.BuildContext(finding);

        // 3 items kept x 1 property each = 3 fields; #4 and #5 dropped.
        var item = Assert.Single(context.Details);
        Assert.Equal(3, item.Fields.Count);
        Assert.DoesNotContain(item.Fields, f => f.Label.StartsWith("#4"));
    }

    [Fact]
    public void BuildContext_NoDrillDown_ReturnsEmptyContext()
    {
        var context = FindingMessageFormatter.BuildContext(MakeFinding("h"));
        Assert.Empty(context.Details);
    }

    [Fact]
    public void DetailText_CarriesStoryPathAndChainMetadata()
    {
        var text = FindingMessageFormatter.DetailText(MakeFinding("h"));

        Assert.Contains("CPU_SPIKE → PLAN_REGRESSION", text);
        Assert.Contains("Severity", text);
        Assert.Contains("Confidence", text);
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

        var notifier = new AnalysisNotificationService(new EmailAlertService(_duckDb));
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

        var notifier = new AnalysisNotificationService(new EmailAlertService(_duckDb));

        await notifier.NotifyAsync(new[]
        {
            MakeFinding("hash000000000001", severity: 2.0),
            MakeFinding("hash000000000002", severity: 2.0)
        });

        Assert.Equal(2, await CountAlertLogRowsAsync());
    }

    [Fact]
    public async Task NotifyAsync_BelowSeverityThreshold_DoesNotNotify()
    {
        await _duckDb.InitializeAsync();
        App.AnalysisNotifySeverity = 1.5;

        var notifier = new AnalysisNotificationService(new EmailAlertService(_duckDb));
        await notifier.NotifyAsync(new[] { MakeFinding("lowsev0000000001", severity: 1.0) });

        Assert.Equal(0, await CountAlertLogRowsAsync());
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
