/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's control-plane writes to <c>config.config_alert_settings</c> — the single-row (id=1) desired
/// state for the shared alert engine's toggles/thresholds plus the scheduled-analysis cadence knobs the
/// Stage-1 <c>StoreConfigProvider</c> reads and hot-swaps into the running service's
/// <c>DarlingAlertSettings</c>/<c>AnalysisConfig</c> on every reload beacon. This is the Stage-3b rewire that
/// replaces the severed <c>viewer-settings.json</c> alert block: saving the Settings window's Notifications +
/// Automated-Analysis sections now drives the running service.
///
/// <para>Mirrors <see cref="MonitoredServerUpsertSql"/>/<see cref="GetMuteRulesAsync"/> discipline exactly:
/// public-const SQL (so Darling.Tests pin the dialect + column parity with the service's
/// <c>StoreConfigProvider.ReadAlertSettingsAsync</c> without a live Postgres), every value bound as a
/// <c>$N</c> parameter, the write routed through <see cref="ExecuteWriteAsync"/> so a read-only <c>viewer</c>
/// seat degrades to <see cref="ViewerReadOnlyException"/> (42501). The single global row is pinned to
/// <c>id = 1</c>; <c>modified_at</c> is server-side naive-UTC. The bare table name resolves through the
/// <c>collect,config,public</c> search_path to <c>config.config_alert_settings</c>.</para>
///
/// <para><b>cpu_mode.</b> The store carries the SERVICE's vocabulary — <c>"sql"</c> (SQL-process CPU) vs
/// <c>"total"</c> (all non-idle CPU) — which the service compares case-insensitively against <c>"sql"</c>.
/// The viewer's <see cref="AlertSettingsRow.CpuMode"/> holds that same store value; the Settings window maps
/// its "Total"/"SqlOnly" combo to/from it (see <see cref="MapCpuModeToStore"/>).</para>
/// </summary>
public sealed partial class ViewerDataService
{
    /* The 27 AlertsConfig + AnalysisConfig columns in the SAME order the service reads them
       (StoreConfigProvider.ReadAlertSettingsAsync), so the parity test pins one list against both ends. */
    private const string AlertSettingsColumns =
        "enabled, cpu_enabled, cpu_threshold_percent, cpu_mode, blocking_enabled, blocking_count_threshold, " +
        "deadlock_enabled, deadlock_count_threshold, poison_wait_enabled, poison_wait_threshold_ms, " +
        "long_running_query_enabled, long_running_query_threshold_minutes, tempdb_space_enabled, " +
        "tempdb_space_threshold_percent, low_disk_enabled, low_disk_threshold_percent, low_disk_threshold_gb, " +
        "long_running_job_enabled, long_running_job_multiplier, failed_job_enabled, failed_job_lookback_minutes, " +
        "cooldown_minutes, excluded_databases, analysis_enabled, analysis_interval_minutes, " +
        "analysis_notifications_enabled, analysis_notify_severity";

    /// <summary>The single global alert-settings row (id=1), for the Settings window prefill + the migrate-in
    /// defaults check. Column order matches <see cref="AlertSettingsColumns"/>.</summary>
    public const string AlertSettingsSelectSql =
        "SELECT " + AlertSettingsColumns + " FROM config_alert_settings WHERE id = 1";

    /// <summary>Upserts the single global alert-settings row (Settings window Save). ON CONFLICT rewrites every
    /// column and bumps <c>modified_at</c> (and, via the V17 statement trigger, <c>config_version</c> — the
    /// service reloads on its next sweep). $1..$27 bind the columns in <see cref="AlertSettingsColumns"/> order.</summary>
    public const string AlertSettingsUpsertSql = @"
INSERT INTO config_alert_settings (id, " + AlertSettingsColumns + @", modified_at)
VALUES (1, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22,
        $23, $24, $25, $26, $27, (now() AT TIME ZONE 'UTC'))
ON CONFLICT (id) DO UPDATE SET
    enabled = EXCLUDED.enabled,
    cpu_enabled = EXCLUDED.cpu_enabled,
    cpu_threshold_percent = EXCLUDED.cpu_threshold_percent,
    cpu_mode = EXCLUDED.cpu_mode,
    blocking_enabled = EXCLUDED.blocking_enabled,
    blocking_count_threshold = EXCLUDED.blocking_count_threshold,
    deadlock_enabled = EXCLUDED.deadlock_enabled,
    deadlock_count_threshold = EXCLUDED.deadlock_count_threshold,
    poison_wait_enabled = EXCLUDED.poison_wait_enabled,
    poison_wait_threshold_ms = EXCLUDED.poison_wait_threshold_ms,
    long_running_query_enabled = EXCLUDED.long_running_query_enabled,
    long_running_query_threshold_minutes = EXCLUDED.long_running_query_threshold_minutes,
    tempdb_space_enabled = EXCLUDED.tempdb_space_enabled,
    tempdb_space_threshold_percent = EXCLUDED.tempdb_space_threshold_percent,
    low_disk_enabled = EXCLUDED.low_disk_enabled,
    low_disk_threshold_percent = EXCLUDED.low_disk_threshold_percent,
    low_disk_threshold_gb = EXCLUDED.low_disk_threshold_gb,
    long_running_job_enabled = EXCLUDED.long_running_job_enabled,
    long_running_job_multiplier = EXCLUDED.long_running_job_multiplier,
    failed_job_enabled = EXCLUDED.failed_job_enabled,
    failed_job_lookback_minutes = EXCLUDED.failed_job_lookback_minutes,
    cooldown_minutes = EXCLUDED.cooldown_minutes,
    excluded_databases = EXCLUDED.excluded_databases,
    analysis_enabled = EXCLUDED.analysis_enabled,
    analysis_interval_minutes = EXCLUDED.analysis_interval_minutes,
    analysis_notifications_enabled = EXCLUDED.analysis_notifications_enabled,
    analysis_notify_severity = EXCLUDED.analysis_notify_severity,
    modified_at = (now() AT TIME ZONE 'UTC')";

    /// <summary>The two <c>cpu_mode</c> values the service honors (it compares case-insensitively against
    /// <see cref="CpuModeSql"/>; anything else is total).</summary>
    public const string CpuModeSql = "sql";
    public const string CpuModeTotal = "total";

    /// <summary>Reads the single global alert-settings row, or null when the store has not seeded it yet
    /// (a pre-Stage-1 store, or the service has not started) — the caller then shows viewer defaults.</summary>
    public async Task<AlertSettingsRow?> GetAlertSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(AlertSettingsSelectSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAlertSettingsRow(reader) : null;
    }

    /// <summary>Upserts the single global alert-settings row (Settings window Save). Read-only seats throw
    /// <see cref="ViewerReadOnlyException"/> via <see cref="ExecuteWriteAsync"/>.</summary>
    public async Task UpsertAlertSettingsAsync(AlertSettingsRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        await using var command = _dataSource.CreateCommand(AlertSettingsUpsertSql);
        BindAlertSettings(command, row);
        await ExecuteWriteAsync(command, cancellationToken);
    }

    private static void BindAlertSettings(NpgsqlCommand command, AlertSettingsRow r)
    {
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.Enabled });                          // $1
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.CpuEnabled });                       // $2
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.CpuThresholdPercent });               // $3
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = r.CpuMode });                        // $4
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.BlockingEnabled });                  // $5
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.BlockingCountThreshold });            // $6
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.DeadlockEnabled });                  // $7
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.DeadlockCountThreshold });            // $8
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.PoisonWaitEnabled });                // $9
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.PoisonWaitThresholdMs });             // $10
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.LongRunningQueryEnabled });          // $11
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.LongRunningQueryThresholdMinutes });  // $12
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.TempDbSpaceEnabled });               // $13
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.TempDbSpaceThresholdPercent });       // $14
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.LowDiskEnabled });                   // $15
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.LowDiskThresholdPercent });           // $16
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.LowDiskThresholdGb });                // $17
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.LongRunningJobEnabled });            // $18
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.LongRunningJobMultiplier });          // $19
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.FailedJobEnabled });                 // $20
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.FailedJobLookbackMinutes });          // $21
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.CooldownMinutes });                   // $22
        AddTextArray(command, r.ExcludedDatabases);                                                             // $23
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.AnalysisEnabled });                  // $24
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = r.AnalysisIntervalMinutes });           // $25
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = r.AnalysisNotificationsEnabled });     // $26
        command.Parameters.Add(new NpgsqlParameter<double> { TypedValue = r.AnalysisNotifySeverity });         // $27
    }

    private static AlertSettingsRow ReadAlertSettingsRow(NpgsqlDataReader reader) => new()
    {
        Enabled = reader.GetBoolean(0),
        CpuEnabled = reader.GetBoolean(1),
        CpuThresholdPercent = reader.GetInt32(2),
        CpuMode = reader.GetString(3),
        BlockingEnabled = reader.GetBoolean(4),
        BlockingCountThreshold = reader.GetInt32(5),
        DeadlockEnabled = reader.GetBoolean(6),
        DeadlockCountThreshold = reader.GetInt32(7),
        PoisonWaitEnabled = reader.GetBoolean(8),
        PoisonWaitThresholdMs = reader.GetInt32(9),
        LongRunningQueryEnabled = reader.GetBoolean(10),
        LongRunningQueryThresholdMinutes = reader.GetInt32(11),
        TempDbSpaceEnabled = reader.GetBoolean(12),
        TempDbSpaceThresholdPercent = reader.GetInt32(13),
        LowDiskEnabled = reader.GetBoolean(14),
        LowDiskThresholdPercent = reader.GetInt32(15),
        LowDiskThresholdGb = reader.GetInt32(16),
        LongRunningJobEnabled = reader.GetBoolean(17),
        LongRunningJobMultiplier = reader.GetInt32(18),
        FailedJobEnabled = reader.GetBoolean(19),
        FailedJobLookbackMinutes = reader.GetInt32(20),
        CooldownMinutes = reader.GetInt32(21),
        ExcludedDatabases = reader.IsDBNull(22) ? new List<string>() : reader.GetFieldValue<string[]>(22).ToList(),
        AnalysisEnabled = reader.GetBoolean(23),
        AnalysisIntervalMinutes = reader.GetInt32(24),
        AnalysisNotificationsEnabled = reader.GetBoolean(25),
        AnalysisNotifySeverity = reader.GetDouble(26),
    };

    /// <summary>Maps the Settings window's CPU-mode combo tag ("Total"/"SqlOnly") to the store value.</summary>
    public static string MapCpuModeToStore(string viewerMode) =>
        string.Equals(viewerMode, "SqlOnly", StringComparison.OrdinalIgnoreCase) ? CpuModeSql : CpuModeTotal;

    /// <summary>Maps the store value back to the Settings window's combo tag.</summary>
    public static string MapCpuModeFromStore(string? storeMode) =>
        string.Equals(storeMode, CpuModeSql, StringComparison.OrdinalIgnoreCase) ? "SqlOnly" : "Total";
}

/// <summary>
/// A <c>config.config_alert_settings</c> row (the single id=1 global row) as the viewer authors + reads it —
/// the desired-state twin of the service's <c>AlertsConfig</c> + <c>AnalysisConfig</c> the store hot-swaps in.
/// Carries ONLY the columns the store (and hence the service) has: the viewer-only alert fields the Settings
/// window also edits (tray minimize, connection-change notify, LRQ max-results + the five noise filters,
/// Summary/Per-event delivery, mute-rule default expiration, dismissal logging, analysis re-notify cooldown)
/// are NOT service-honored config and stay viewer-local in <see cref="ViewerAppSettings"/>. Defaults mirror
/// the V17 DDL (and Lite's <c>App.*</c>) member-for-member so <see cref="Defaults"/> equals a freshly-seeded row.
/// </summary>
public sealed class AlertSettingsRow
{
    public bool Enabled { get; set; } = true;
    public bool CpuEnabled { get; set; } = true;
    public int CpuThresholdPercent { get; set; } = 80;

    /// <summary><see cref="ViewerDataService.CpuModeSql"/> or <see cref="ViewerDataService.CpuModeTotal"/>.</summary>
    public string CpuMode { get; set; } = ViewerDataService.CpuModeTotal;

    public bool BlockingEnabled { get; set; } = true;
    public int BlockingCountThreshold { get; set; } = 1;
    public bool DeadlockEnabled { get; set; } = true;
    public int DeadlockCountThreshold { get; set; } = 1;
    public bool PoisonWaitEnabled { get; set; } = true;
    public int PoisonWaitThresholdMs { get; set; } = 500;
    public bool LongRunningQueryEnabled { get; set; } = true;
    public int LongRunningQueryThresholdMinutes { get; set; } = 30;
    public bool TempDbSpaceEnabled { get; set; } = true;
    public int TempDbSpaceThresholdPercent { get; set; } = 80;
    public bool LowDiskEnabled { get; set; } = true;
    public int LowDiskThresholdPercent { get; set; } = 10;
    public int LowDiskThresholdGb { get; set; } = 5;
    public bool LongRunningJobEnabled { get; set; } = true;
    public int LongRunningJobMultiplier { get; set; } = 3;
    public bool FailedJobEnabled { get; set; } = true;
    public int FailedJobLookbackMinutes { get; set; } = 60;
    public int CooldownMinutes { get; set; } = 5;
    public List<string> ExcludedDatabases { get; set; } = new();
    public bool AnalysisEnabled { get; set; } = true;
    public int AnalysisIntervalMinutes { get; set; } = 30;

    /// <summary>Darling's shipped default is TRUE (Lite's App default is false) — matches the V17 DDL.</summary>
    public bool AnalysisNotificationsEnabled { get; set; } = true;
    public double AnalysisNotifySeverity { get; set; } = 1.5;

    /// <summary>A row equal to the V17 seed defaults — the migrate-in "is the service section still at defaults?" baseline.</summary>
    public static AlertSettingsRow Defaults() => new();

    /// <summary>Value-equality against another row (sequence-comparing the excluded-databases list) — the
    /// migrate-in uses it to detect an untouched (default) store section and a genuine viewer customization.</summary>
    public bool ValueEquals(AlertSettingsRow other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Enabled == other.Enabled
            && CpuEnabled == other.CpuEnabled
            && CpuThresholdPercent == other.CpuThresholdPercent
            && string.Equals(CpuMode, other.CpuMode, StringComparison.OrdinalIgnoreCase)
            && BlockingEnabled == other.BlockingEnabled
            && BlockingCountThreshold == other.BlockingCountThreshold
            && DeadlockEnabled == other.DeadlockEnabled
            && DeadlockCountThreshold == other.DeadlockCountThreshold
            && PoisonWaitEnabled == other.PoisonWaitEnabled
            && PoisonWaitThresholdMs == other.PoisonWaitThresholdMs
            && LongRunningQueryEnabled == other.LongRunningQueryEnabled
            && LongRunningQueryThresholdMinutes == other.LongRunningQueryThresholdMinutes
            && TempDbSpaceEnabled == other.TempDbSpaceEnabled
            && TempDbSpaceThresholdPercent == other.TempDbSpaceThresholdPercent
            && LowDiskEnabled == other.LowDiskEnabled
            && LowDiskThresholdPercent == other.LowDiskThresholdPercent
            && LowDiskThresholdGb == other.LowDiskThresholdGb
            && LongRunningJobEnabled == other.LongRunningJobEnabled
            && LongRunningJobMultiplier == other.LongRunningJobMultiplier
            && FailedJobEnabled == other.FailedJobEnabled
            && FailedJobLookbackMinutes == other.FailedJobLookbackMinutes
            && CooldownMinutes == other.CooldownMinutes
            && (ExcludedDatabases ?? new List<string>()).SequenceEqual(other.ExcludedDatabases ?? new List<string>())
            && AnalysisEnabled == other.AnalysisEnabled
            && AnalysisIntervalMinutes == other.AnalysisIntervalMinutes
            && AnalysisNotificationsEnabled == other.AnalysisNotificationsEnabled
            && Math.Abs(AnalysisNotifySeverity - other.AnalysisNotifySeverity) < 0.0001;
    }
}
