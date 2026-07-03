/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// One Alerts-tab row over <c>config_alert_log</c>, mirroring Lite's <c>AlertHistoryRow</c>
/// (Lite/Services/LocalDataService.AlertHistory.cs): the same metric-keyed value formatting
/// (#1134), the same email-vs-tray <see cref="StatusDisplay"/>, and the shared
/// <see cref="AlertMetricClassifier"/> for critical/warning/resolved row emphasis. Read-only —
/// the viewer's alert history is a read surface (the write-back surfaces are finding mute and the
/// mute-rule CRUD); there is no dismiss here (Lite's dismiss only sets a durable hide flag, so no
/// equivalent is invented for Darling per the wave brief).
/// </summary>
public sealed class ViewerAlertRow
{
    /// <summary>Stored naive-UTC alert time.</summary>
    public required DateTime AlertTime { get; init; }

    public required string MetricName { get; init; }

    public required double CurrentValue { get; init; }

    public required double ThresholdValue { get; init; }

    public required bool AlertSent { get; init; }

    public required string NotificationType { get; init; }

    public string? SendError { get; init; }

    public required bool Muted { get; init; }

    public string? DetailText { get; init; }

    public string? ContextJson { get; init; }

    /// <summary>Stored naive-UTC; shown in the viewer machine's local time (the viewer convention).</summary>
    public string TimeLocal => ViewerDataService.ToLocalTime(AlertTime).ToString("yyyy-MM-dd HH:mm:ss");

    public string CurrentValueDisplay => FormatValue(MetricName, CurrentValue);

    public string ThresholdValueDisplay => FormatValue(MetricName, ThresholdValue);

    /// <summary>Email rows show send outcome; tray/other rows show shown-vs-delivered (Lite's mapping).</summary>
    public string StatusDisplay
    {
        get
        {
            if (NotificationType == "email")
            {
                return AlertSent ? "Sent" : (!string.IsNullOrEmpty(SendError) ? "Failed" : "Not sent");
            }

            return AlertSent ? "Delivered" : "Shown";
        }
    }

    public bool IsResolved => AlertMetricClassifier.IsResolution(MetricName);

    public bool IsCritical => AlertMetricClassifier.IsCritical(MetricName);

    public bool IsWarning => AlertMetricClassifier.IsWarning(MetricName);

    /* #1134 — render the stored value with the unit/precision matching each metric the alert engine
       emits, keyed on the exact metric_name strings. Copied from Lite's AlertHistoryRow.FormatValue
       so both grids read identically; the :F2 fallback (never :G) keeps an unmapped metric — e.g. an
       "Analysis: <category> [<hash>]" finding severity — from rendering as a raw full-precision float. */
    private static string FormatValue(string metricName, double value) => metricName switch
    {
        "High CPU" or "tempdb Space" or "Volume Free Space" or "Long-Running Job" => $"{value:F1}%",
        "Poison Wait" => $"{value:F0} ms",
        "Long-Running Query" => $"{value:F0} m",
        "Blocking Detected" or "Deadlocks Detected" or "Failed Agent Job" => $"{value:F0}",
        _ => $"{value:F2}",
    };
}

public sealed partial class ViewerDataService
{
    /// <summary>
    /// The alert-history read — Lite's <c>GetAlertHistoryAsync</c> query adapted to Darling's
    /// <c>config_alert_log</c> (no <c>source</c> column and no archive tier, so no
    /// <c>v_config_alert_log</c> union; Darling rows are always "live"). Same
    /// <c>dismissed = FALSE</c> filter, newest first, windowed on <c>alert_time</c>, per-server.
    /// $1 window start, $2 server_id, $3 limit (naive UTC / int / int).
    /// </summary>
    public const string AlertHistorySql = @"
SELECT
    alert_time,
    metric_name,
    current_value,
    threshold_value,
    alert_sent,
    notification_type,
    send_error,
    muted,
    detail_text,
    context_json
FROM config_alert_log
WHERE alert_time >= $1
AND   server_id = $2
AND   dismissed = FALSE
ORDER BY alert_time DESC
LIMIT $3";

    /// <summary>
    /// Recent alerts for one server, newest first, excluding dismissed rows — the Alerts tab's read.
    /// </summary>
    public async Task<List<ViewerAlertRow>> GetAlertHistoryAsync(
        int serverId, DateTime sinceUtc, int limit = 500, CancellationToken cancellationToken = default)
    {
        var rows = new List<ViewerAlertRow>();

        await using var command = _dataSource.CreateCommand(AlertHistorySql);
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Unspecified),
        });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ViewerAlertRow
            {
                AlertTime = reader.GetDateTime(0),
                MetricName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                CurrentValue = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                ThresholdValue = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                AlertSent = !reader.IsDBNull(4) && reader.GetBoolean(4),
                NotificationType = reader.IsDBNull(5) ? "" : reader.GetString(5),
                SendError = reader.IsDBNull(6) ? null : reader.GetString(6),
                Muted = !reader.IsDBNull(7) && reader.GetBoolean(7),
                DetailText = reader.IsDBNull(8) ? null : reader.GetString(8),
                ContextJson = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        return rows;
    }

    /// <summary>
    /// The Alerts detail pane's text for one alert: a header (type, local time, value/threshold,
    /// channel/status, muted, any send error), the stored <c>detail_text</c>, then the raw
    /// <c>context_json</c> pretty-printed when it parses (it carries the #1140 dedup fingerprint /
    /// #1154 cooldown key humans care about). Pure over the row — unit-testable without a store.
    /// </summary>
    public static string ComposeAlertDetailText(ViewerAlertRow row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(row.MetricName);
        sb.AppendLine($"Time (local): {row.TimeLocal}");
        sb.AppendLine($"Value: {row.CurrentValueDisplay}    Threshold: {row.ThresholdValueDisplay}");
        sb.AppendLine($"Channel: {row.NotificationType}    Status: {row.StatusDisplay}");
        if (row.Muted)
        {
            sb.AppendLine("Muted: yes");
        }
        if (!string.IsNullOrEmpty(row.SendError))
        {
            sb.AppendLine($"Send error: {row.SendError}");
        }

        if (!string.IsNullOrWhiteSpace(row.DetailText))
        {
            sb.AppendLine();
            sb.AppendLine(row.DetailText.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(row.ContextJson))
        {
            sb.AppendLine();
            sb.AppendLine("Context (dedup fingerprint / cooldown key):");
            sb.AppendLine(PrettyPrintJsonOrRaw(row.ContextJson));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Indents JSON for reading; returns the input unchanged when it doesn't parse.</summary>
    private static string PrettyPrintJsonOrRaw(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(document.RootElement, s_indentedJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }
}
