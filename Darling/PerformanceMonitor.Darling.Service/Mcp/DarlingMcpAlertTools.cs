/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Npgsql;
using PerformanceMonitor.Common;
using PerformanceMonitor.Notifications;

#pragma warning disable CA1707 // MCP tools use snake_case naming convention

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// The alerts MCP tools — get_alert_history, get_alert_settings, get_mute_rules — served over Darling's
/// Postgres store, the same names Lite (and the Dashboard) expose. This is the FLEET edition's biggest MCP
/// win: an agent triaging N servers can now see what fired, whether delivery failed, the current thresholds,
/// and which servers are suppressed vs healthy-quiet — none of which the ~60 existing tools surfaced. All
/// STORED reads (no live monitored-server hit): get_alert_history / get_alert_settings read
/// <c>config_alert_log</c> / <c>config_alert_settings</c> through <see cref="DarlingAlertReader"/>, and
/// get_mute_rules reads through the SAME service-side <see cref="PgMuteRuleStore"/> the delivery paths honor.
///
/// <para>get_alert_history adds a fleet dimension Lite's single-store tool lacks: an optional
/// <c>server_name</c> — omit it for the whole fleet (the viewer's all-servers Alert History default, with each
/// row carrying its server), or name a server to scope to it. get_alert_settings reports the single global
/// alert-settings row the service hot-swaps in (the viewer's Settings-window desired state); SMTP/webhook
/// delivery credentials are managed separately and are not exposed here (the least-privilege mcp role cannot
/// read the secret columns anyway).</para>
/// </summary>
[McpServerToolType]
public sealed class DarlingMcpAlertTools
{
    [McpServerTool(Name = "get_alert_history"), Description("Gets recent alert history from the alert log: what alerts fired, when, for which server, the current vs threshold value, whether email/webhook delivery succeeded, and whether the alert was muted. Omit server_name to see the whole fleet (each row names its server); pass one to scope to a single server.")]
    public static async Task<string> GetAlertHistory(
        NpgsqlDataSource postgres,
        [Description("Server name or display name. Omit to return alerts across all servers (the fleet default).")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Maximum rows. Default 50.")] int limit = 50)
    {
        var hoursError = McpHelpers.ValidateHoursBack(hours_back);
        if (hoursError != null) return hoursError;
        var limitError = McpHelpers.ValidateTop(limit);
        if (limitError != null) return limitError;

        /* Optional server scope: a named server resolves + scopes; omitted = the whole fleet (the viewer's
           all-servers Alert History default). Unlike the other tools this does NOT force a single server. */
        int? serverId = null;
        var scope = "(all servers)";
        if (!string.IsNullOrWhiteSpace(server_name))
        {
            var (resolved, error) = await DarlingServerResolver.ResolveOrErrorAsync(postgres, server_name);
            if (error != null) return error;
            serverId = resolved.Value.ServerId;
            scope = resolved.Value.ServerName;
        }

        try
        {
            var since = DateTime.UtcNow.AddHours(-hours_back);
            var rows = await DarlingAlertReader.GetAlertHistoryAsync(postgres, since, serverId, limit);
            if (rows.Count == 0)
                return McpHelpers.Status("empty", "No alerts found in the specified time range.");

            var alerts = rows.Select(r => new
            {
                alert_time = r.AlertTime.ToString("o"),
                server_id = r.ServerId,
                server_name = r.ServerName,
                metric_name = r.MetricName,
                current_value = r.CurrentValue,
                threshold_value = r.ThresholdValue,
                alert_sent = r.AlertSent,
                notification_type = r.NotificationType,
                send_error = r.SendError,
                muted = r.Muted,
                detail_text = r.DetailText
            });

            return JsonSerializer.Serialize(new
            {
                server = scope,
                hours_back,
                total_alerts = rows.Count,
                alerts
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_alert_history", ex);
        }
    }

    [McpServerTool(Name = "get_alert_settings"), Description("Gets the current alert configuration the service is using: which alerts are enabled and their thresholds (CPU, blocking, deadlocks, poison waits, long-running queries/jobs, tempdb, low disk, failed jobs), the cooldown, excluded databases, the deadlock/blocking delivery mode, and the scheduled-analysis cadence. This is the single global settings row the service hot-swaps in. SMTP/webhook delivery credentials are managed separately and are not reported here.")]
    public static async Task<string> GetAlertSettings(
        NpgsqlDataSource postgres)
    {
        try
        {
            var s = await DarlingAlertReader.GetAlertSettingsAsync(postgres);
            if (s is null)
                return McpHelpers.Status(
                    "unavailable",
                    "No alert-settings row is present in the store yet. The service seeds it on startup (or the Viewer's Settings window writes it); until then the service runs on its darling.json defaults.");

            return JsonSerializer.Serialize(new
            {
                alerts_enabled = s.Enabled,
                notify_connection_changes = s.NotifyConnectionChanges,
                cpu = new { enabled = s.CpuEnabled, threshold_percent = s.CpuThresholdPercent, mode = s.CpuMode },
                blocking = new { enabled = s.BlockingEnabled, count_threshold = s.BlockingCountThreshold },
                deadlocks = new { enabled = s.DeadlockEnabled, count_threshold = s.DeadlockCountThreshold },
                poison_wait = new { enabled = s.PoisonWaitEnabled, threshold_ms = s.PoisonWaitThresholdMs },
                long_running_query = new
                {
                    enabled = s.LongRunningQueryEnabled,
                    threshold_minutes = s.LongRunningQueryThresholdMinutes,
                    max_results = s.LongRunningQueryMaxResults,
                    exclude_sp_server_diagnostics = s.LongRunningQueryExcludeSpServerDiagnostics,
                    exclude_wait_for = s.LongRunningQueryExcludeWaitFor,
                    exclude_backups = s.LongRunningQueryExcludeBackups,
                    exclude_misc_waits = s.LongRunningQueryExcludeMiscWaits,
                    exclude_cdc = s.LongRunningQueryExcludeCdc
                },
                tempdb_space = new { enabled = s.TempDbSpaceEnabled, threshold_percent = s.TempDbSpaceThresholdPercent },
                low_disk = new { enabled = s.LowDiskEnabled, threshold_percent = s.LowDiskThresholdPercent, threshold_gb = s.LowDiskThresholdGb },
                long_running_job = new { enabled = s.LongRunningJobEnabled, multiplier = s.LongRunningJobMultiplier },
                failed_job = new { enabled = s.FailedJobEnabled, lookback_minutes = s.FailedJobLookbackMinutes },
                cooldown_minutes = s.CooldownMinutes,
                excluded_databases = s.ExcludedDatabases,
                delivery = new { mode = s.DeliveryMode, per_event_max = s.PerEventMax },
                analysis = new
                {
                    enabled = s.AnalysisEnabled,
                    interval_minutes = s.AnalysisIntervalMinutes,
                    notifications_enabled = s.AnalysisNotificationsEnabled,
                    notify_severity = s.AnalysisNotifySeverity
                }
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_alert_settings", ex);
        }
    }

    [McpServerTool(Name = "get_mute_rules"), Description("Gets the configured alert mute rules. Mute rules suppress specific recurring alerts (by server, metric, database, query text, wait type, or job name) while still logging them — so an agent can tell a genuinely healthy-quiet server from one whose alerts are being suppressed.")]
    public static async Task<string> GetMuteRules(
        NpgsqlDataSource postgres,
        [Description("Include only enabled, non-expired rules. Default true.")] bool enabled_only = true)
    {
        try
        {
            var rules = (await new PgMuteRuleStore(postgres).LoadAllAsync()).AsEnumerable();
            if (enabled_only)
                rules = rules.Where(r => r.Enabled && (r.ExpiresAtUtc == null || r.ExpiresAtUtc > DateTime.UtcNow));

            var list = rules.ToList();
            return JsonSerializer.Serialize(new
            {
                total_count = list.Count,
                mute_rules = list.Select(r => new
                {
                    id = r.Id,
                    enabled = r.Enabled,
                    created_at_utc = r.CreatedAtUtc.ToString("o"),
                    expires_at_utc = r.ExpiresAtUtc?.ToString("o"),
                    reason = r.Reason,
                    server_name = r.ServerName,
                    metric_name = r.MetricName,
                    database_pattern = r.DatabasePattern,
                    query_text_pattern = r.QueryTextPattern,
                    wait_type_pattern = r.WaitTypePattern,
                    job_name_pattern = r.JobNamePattern,
                    summary = r.Summary
                })
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_mute_rules", ex);
        }
    }
}
