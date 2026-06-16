using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// E2 round-trip invariants for the Lite DuckDB stores: a recorded alert
/// produces a config_alert_log row with the same columns/values as the pre-E2
/// EmailAlertService.LogAlertAsync did (numeric resolution, notification_type,
/// send_error, UtcNow stamping), the cooldown-seed reads filter correctly, and
/// mute-rule CRUD round-trips through config_mute_rules unchanged.
/// </summary>
public class StoreRoundTripTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DuckDbInitializer _duckDb;

    public StoreRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LiteStoreTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task RecordAlert_PersistsAllColumns_WithTextNumericFallback()
    {
        await _duckDb.InitializeAsync();
        var store = new DuckDbAlertHistoryStore(_duckDb);
        var before = DateTime.UtcNow;

        /* No numerics supplied → store resolves the doubles from the display
           text (stripping the trailing %), exactly the pre-E2 ?? fallback. */
        await store.RecordAlertAsync(new AlertHistoryRecord(
            ServerId: "7", ServerName: "Srv", MetricName: "CPU",
            CurrentValueText: "92.5%", ThresholdValueText: "80%",
            NumericCurrentValue: null, NumericThresholdValue: null,
            AlertSent: true, NotificationType: "email", SendError: null,
            Muted: false, DetailText: "detail text", ContextJson: "{\"k\":1}"));

        var after = DateTime.UtcNow;
        var row = await ReadSingleAlertRowAsync();

        Assert.NotNull(row);
        Assert.InRange(row!.AlertTime, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(7, row.ServerId);
        Assert.Equal("Srv", row.ServerName);
        Assert.Equal("CPU", row.MetricName);
        Assert.Equal(92.5, row.CurrentValue);
        Assert.Equal(80.0, row.ThresholdValue);
        Assert.True(row.AlertSent);
        Assert.Equal("email", row.NotificationType);
        Assert.Null(row.SendError);
        Assert.False(row.Muted);
        Assert.Equal("detail text", row.DetailText);
        Assert.Equal("{\"k\":1}", row.ContextJson);
    }

    [Fact]
    public async Task RecordAlert_PrefersSuppliedNumerics_OverText()
    {
        await _duckDb.InitializeAsync();
        var store = new DuckDbAlertHistoryStore(_duckDb);

        await store.RecordAlertAsync(new AlertHistoryRecord(
            ServerId: "1", ServerName: "Srv", MetricName: "Analysis",
            CurrentValueText: "not-a-number", ThresholdValueText: "also-bad",
            NumericCurrentValue: 1.8, NumericThresholdValue: 1.5,
            AlertSent: false, NotificationType: "tray", SendError: null,
            Muted: false, DetailText: null, ContextJson: null));

        var row = await ReadSingleAlertRowAsync();
        Assert.NotNull(row);
        Assert.Equal(1.8, row!.CurrentValue);
        Assert.Equal(1.5, row.ThresholdValue);
        Assert.Null(row.DetailText);
        Assert.Null(row.ContextJson);
    }

    [Fact]
    public async Task GetLastEmailSentUtc_FiltersToSuccessfulEmail_GetLastAlertTime_IsUnfiltered()
    {
        await _duckDb.InitializeAsync();
        var store = new DuckDbAlertHistoryStore(_duckDb);

        /* A failed email and a webhook row must NOT seed the email cooldown,
           but they DO count for the unfiltered analysis cooldown. */
        await RecordAsync(store, "3", "CPU", "email", "smtp boom");   // failed email
        await RecordAsync(store, "3", "CPU", "webhook", null);         // webhook only
        await RecordAsync(store, "3", "CPU", "email", null);           // successful email (latest)

        var lastEmail = await store.GetLastEmailSentUtcAsync("3", "CPU");
        var lastAny = await store.GetLastAlertTimeAsync("3", "CPU");

        Assert.NotNull(lastEmail);
        Assert.NotNull(lastAny);
        /* The successful email is the last row written, so both reads land on it. */
        Assert.Equal(lastAny!.Value, lastEmail!.Value);

        /* A metric with only a failed email → no successful-email seed, but the
           unfiltered read still returns the row. */
        await RecordAsync(store, "3", "OnlyFailed", "email", "boom");
        Assert.Null(await store.GetLastEmailSentUtcAsync("3", "OnlyFailed"));
        Assert.NotNull(await store.GetLastAlertTimeAsync("3", "OnlyFailed"));

        /* Unknown metric → null both ways. */
        Assert.Null(await store.GetLastEmailSentUtcAsync("3", "Missing"));
        Assert.Null(await store.GetLastAlertTimeAsync("3", "Missing"));
    }

    [Fact]
    public async Task MuteRuleStore_InsertUpdateSetEnabledDeleteExpire_RoundTrips()
    {
        await _duckDb.InitializeAsync();
        var store = new DuckDbMuteRuleStore(_duckDb);

        var rule = new MuteRule
        {
            Id = "rule-1",
            Enabled = true,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ServerName = "Srv",
            MetricName = "CPU",
            Reason = "noisy"
        };

        await store.InsertAsync(rule);
        var loaded = await store.LoadAllAsync();
        Assert.Single(loaded);
        Assert.Equal("rule-1", loaded[0].Id);
        Assert.Equal("Srv", loaded[0].ServerName);
        Assert.Equal("CPU", loaded[0].MetricName);
        Assert.Equal("noisy", loaded[0].Reason);
        Assert.True(loaded[0].Enabled);

        rule.Reason = "still noisy";
        rule.MetricName = "Blocking";
        await store.UpdateAsync(rule);
        loaded = await store.LoadAllAsync();
        Assert.Single(loaded);
        Assert.Equal("still noisy", loaded[0].Reason);
        Assert.Equal("Blocking", loaded[0].MetricName);

        await store.SetEnabledAsync("rule-1", false);
        loaded = await store.LoadAllAsync();
        Assert.False(loaded[0].Enabled);

        await store.DeleteAsync("rule-1");
        Assert.Empty(await store.LoadAllAsync());

        /* DeleteExpired removes only the given ids. */
        var expired = new MuteRule
        {
            Id = "exp-1",
            Enabled = true,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        var live = new MuteRule { Id = "live-1", Enabled = true, CreatedAtUtc = DateTime.UtcNow };
        await store.InsertAsync(expired);
        await store.InsertAsync(live);
        await store.DeleteExpiredAsync(new List<string> { "exp-1" });
        loaded = await store.LoadAllAsync();
        Assert.Single(loaded);
        Assert.Equal("live-1", loaded[0].Id);
    }

    private static Task RecordAsync(IAlertHistoryStore store, string serverId, string metric, string type, string? error)
        => store.RecordAlertAsync(new AlertHistoryRecord(
            serverId, "Srv", metric, "90", "80", 90, 80,
            true, type, error, false, null, null));

    private sealed record AlertRow(
        DateTime AlertTime, int ServerId, string ServerName, string MetricName,
        double CurrentValue, double ThresholdValue, bool AlertSent,
        string NotificationType, string? SendError, bool Muted,
        string? DetailText, string? ContextJson);

    /// <summary>Reads the single config_alert_log row. DB I/O lives in a helper
    /// (not a [Fact] body) to match the existing test convention.</summary>
    private async Task<AlertRow?> ReadSingleAlertRowAsync()
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT alert_time, server_id, server_name, metric_name, current_value, threshold_value,
       alert_sent, notification_type, send_error, muted, detail_text, context_json
FROM config_alert_log";
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new AlertRow(
            DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetDouble(4),
            reader.GetDouble(5),
            reader.GetBoolean(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetBoolean(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }
}
