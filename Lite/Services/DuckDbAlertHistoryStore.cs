/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Database;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// DuckDB-backed <see cref="IAlertHistoryStore"/> over the <c>config_alert_log</c>
/// table. Wraps the persistence that previously lived directly inside
/// <see cref="EmailAlertService"/> (LogAlertAsync / GetLastEmailSentUtcAsync /
/// GetLastAlertTimeAsync) — verbatim SQL, same DuckDbInitializer + App.DatabasePath
/// fallback. The shared <c>string serverId</c> is parsed back to the DuckDB
/// <c>INT</c> column before binding the $1 parameter (§4.1).
/// </summary>
public sealed class DuckDbAlertHistoryStore : IAlertHistoryStore
{
    private readonly DuckDbInitializer? _duckDb;

    /* Failure tracking for louder logging — moved with LogAlertAsync. */
    private int _consecutiveLogFailures;

    public DuckDbAlertHistoryStore(DuckDbInitializer? duckDb = null)
    {
        _duckDb = duckDb;
    }

    /// <summary>
    /// Logs an alert to the config_alert_log table in DuckDB.
    /// Reuses the injected DuckDbInitializer instead of creating a new one each time.
    /// </summary>
    public async Task RecordAlertAsync(AlertHistoryRecord record)
    {
        /* Resolve the DuckDB current_value/threshold_value doubles from the
           optional numerics, falling back to parsing the display text — exactly
           the ?? fallback EmailAlertService.TrySendAlertEmailAsync used today. */
        var currentValue = record.NumericCurrentValue
            ?? (double.TryParse(record.CurrentValueText.TrimEnd('%'), out var cv) ? cv : 0);
        var thresholdValue = record.NumericThresholdValue
            ?? (double.TryParse(record.ThresholdValueText.TrimEnd('%'), out var tv) ? tv : 0);
        var serverId = int.TryParse(record.ServerId, out var sid) ? sid : 0;

        try
        {
            /* Use injected initializer, fall back to creating one from App.DatabasePath */
            var duckDb = _duckDb;
            if (duckDb == null)
            {
                var dbPath = App.DatabasePath;
                if (string.IsNullOrEmpty(dbPath)) return;
                duckDb = new DuckDbInitializer(dbPath);
            }

            using var writeLock = duckDb.AcquireWriteLock();
            using var connection = duckDb.CreateConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO config_alert_log (alert_time, server_id, server_name, metric_name, current_value, threshold_value, alert_sent, notification_type, send_error, muted, detail_text, context_json)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)";

            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = DateTime.UtcNow });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = serverId });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = record.ServerName });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = record.MetricName });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = currentValue });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = thresholdValue });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = record.AlertSent });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = record.NotificationType });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = record.SendError ?? (object)DBNull.Value });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = record.Muted });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = record.DetailText ?? (object)DBNull.Value });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = record.ContextJson ?? (object)DBNull.Value });

            await command.ExecuteNonQueryAsync();

            /* Reset log failure counter on success */
            if (_consecutiveLogFailures > 0)
            {
                AppLogger.Info("EmailAlert", $"Alert logging recovered after {_consecutiveLogFailures} failure(s)");
            }
            _consecutiveLogFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveLogFailures++;
            if (_consecutiveLogFailures <= 3)
            {
                AppLogger.Error("EmailAlert", $"Failed to log alert ({_consecutiveLogFailures}x): {ex.Message}");
            }
            else if (_consecutiveLogFailures % 50 == 0)
            {
                AppLogger.Error("EmailAlert", $"Alert logging STILL broken: {_consecutiveLogFailures} failures. Last: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Returns the UTC time the most recent alert email was successfully sent
    /// for this server/metric, read from config_alert_log — or null if none.
    /// Used to seed the in-memory cooldown after an app restart (#981).
    /// </summary>
    public async Task<DateTime?> GetLastEmailSentUtcAsync(string serverId, string metricName)
    {
        var sid = int.TryParse(serverId, out var s) ? s : 0;
        try
        {
            /* Use injected initializer, fall back to creating one from App.DatabasePath */
            var duckDb = _duckDb;
            if (duckDb == null)
            {
                var dbPath = App.DatabasePath;
                if (string.IsNullOrEmpty(dbPath)) return null;
                duckDb = new DuckDbInitializer(dbPath);
            }

            using var readLock = duckDb.AcquireReadLock();
            using var connection = duckDb.CreateConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            /* A successful email send is logged with a notification_type of
               'email' / 'email+webhook' and a null send_error — that mirrors
               exactly when _cooldowns is updated after SendEmailAsync. */
            command.CommandText = @"
SELECT MAX(alert_time)
FROM config_alert_log
WHERE server_id = $1
AND   metric_name = $2
AND   notification_type IN ('email', 'email+webhook')
AND   send_error IS NULL";
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = sid });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = metricName });

            var result = await command.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value) return null;

            /* alert_time is written as DateTime.UtcNow; tag it UTC so the kind
               is explicit (the cooldown subtraction is tick math regardless). */
            return DateTime.SpecifyKind(Convert.ToDateTime(result), DateTimeKind.Utc);
        }
        catch (Exception ex)
        {
            AppLogger.Error("EmailAlert", $"Could not read persisted alert cooldown: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the UTC time of the most recent alert_log row for this
    /// (serverId, metricName), regardless of notification channel or
    /// delivery result. Used by <see cref="AnalysisNotificationService"/>
    /// to seed its per-finding cooldown across restarts — unlike the
    /// email cooldown (which filters to successful sends), the analysis
    /// cooldown is stamped unconditionally, so the persisted equivalent
    /// is the latest row for that metric_name, period.
    /// </summary>
    public async Task<DateTime?> GetLastAlertTimeAsync(string serverId, string metricName)
    {
        var sid = int.TryParse(serverId, out var s) ? s : 0;
        try
        {
            var duckDb = _duckDb;
            if (duckDb == null)
            {
                var dbPath = App.DatabasePath;
                if (string.IsNullOrEmpty(dbPath)) return null;
                duckDb = new DuckDbInitializer(dbPath);
            }

            using var readLock = duckDb.AcquireReadLock();
            using var connection = duckDb.CreateConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            /* metric_name embeds the first 8 chars of StoryPathHash via
               FindingMessageFormatter.MetricName, so a short-hash collision
               between two findings could seed one from the other's history.
               Acceptable: collision rate is ~sqrt(2^32) ≈ 65k unique patterns
               per server before a 50% chance, and the failure mode is suppress
               (not over-notify). */
            command.CommandText = @"
SELECT MAX(alert_time)
FROM config_alert_log
WHERE server_id = $1
AND   metric_name = $2";
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = sid });
            command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = metricName });

            var result = await command.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value) return null;

            return DateTime.SpecifyKind(Convert.ToDateTime(result), DateTimeKind.Utc);
        }
        catch (Exception ex)
        {
            AppLogger.Error("AnalysisNotify", $"Could not read persisted analysis cooldown: {ex.Message}");
            return null;
        }
    }
}
