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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Configuration Changes tab's three change histories (Dashboard's <c>ConfigChangesContent</c> ported to
/// the viewer): server-config, database-config and trace-flag DRIFT over time. The Dashboard reads
/// pre-materialized <c>report.*_configuration_changes</c> / <c>report.trace_flag_changes</c> views (LAG-diff
/// over a persisted config history); Darling has no such views, so — exactly like the service's
/// <c>DarlingConfigHistoryReader</c> (PR #1421, the reference this mirrors) — the change history is computed
/// in C# by diffing the store's APPEND-ONLY config snapshots (<c>v_server_config</c> / <c>v_database_config</c>
/// / <c>v_trace_flags</c>, every setting written every capture, keyed by <c>capture_time</c>). The diff runs in
/// C# (not SQL) so it is unit-testable without a live Postgres and so trace-flag enable/disable (a flag row
/// appearing / disappearing between snapshots) is handled by set-diff.
///
/// <para>
/// WINDOWING — the read is upper-bounded at the window end (<c>capture_time &lt;= $2</c>) and the window START
/// is applied to the computed <c>change_time</c> in C# (NOT to the snapshot read). This deliberately keeps the
/// snapshot immediately BEFORE the window as the diff baseline, so a change landing on the first in-window
/// snapshot is still detected — mirroring the Dashboard, whose <c>report.*_changes</c> views LAG over the full
/// history and then filter <c>change_time &gt;= @from AND change_time &lt;= @to</c>. Strictly lower-bounding the
/// snapshot read would drop that baseline and under-report the left-edge change. Both window edges are honored:
/// the upper on the read (<c>$2</c>), the lower (and, redundantly, the upper) on <c>change_time</c> in the diff.
/// </para>
///
/// <para>
/// COLLECTION-CADENCE + SHAPE CAVEATS (surfaced honestly, never silently dropped): the config collectors run ON
/// CONNECT only (<c>FrequencyMinutes = 0</c>), so change granularity equals the service's connect/restart
/// cadence, and a fresh / stably-connected server may hold a single snapshot — then there is no change yet (the
/// diff needs &gt;= 2 snapshots). The change grids emit ONLY collected values; the Dashboard enrichment columns
/// Darling does not collect are OMITTED (not fabricated): server-config <c>description</c> (the setting's
/// human blurb) and database-config <c>setting_type</c> (the store is WIDE — <c>setting_name</c> is the literal
/// column name). <c>requires_restart</c> is NOT one of the gaps: it is derivable from collected columns
/// (<c>is_dynamic = false AND value_configured != value_in_use</c>, the Dashboard view's own definition), so it
/// is kept as a derived column. The per-row change narrative is DERIVED here from the values (allowed), matching
/// the Dashboard view's wording.
/// </para>
/// </summary>
public sealed partial class ViewerDataService
{
    /* ─────────────────────────── snapshot-read SQL (upper-bound at window end) ─────────────────────────── */

    /// <summary>Every sys.configurations snapshot for a server up to the window end, oldest-first per name —
    /// the diff walks consecutive captures per configuration_name (every config row is present in every
    /// snapshot, so a per-name walk suffices). $1 server_id, $2 window end (naive UTC).</summary>
    public const string ServerConfigChangesSnapshotsSql = """
        SELECT
            capture_time,
            configuration_name,
            value_configured,
            value_in_use,
            is_dynamic,
            is_advanced
        FROM v_server_config
        WHERE server_id = $1
        AND   capture_time <= $2
        ORDER BY configuration_name, capture_time
        """;

    /// <summary>Every sys.databases snapshot for a server up to the window end, with the 27 setting columns
    /// CAST to text for a uniform value-diff. $1 server_id, $2 window end (naive UTC). The SELECT order is
    /// <see cref="DatabaseConfigChangeSettingNames"/> (load-bearing — the diff walks the wide row positionally).</summary>
    public const string DatabaseConfigChangesSnapshotsSql = """
        SELECT
            capture_time,
            database_name,
            state_desc,
            compatibility_level::text,
            collation_name,
            recovery_model,
            is_read_only::text,
            is_auto_close_on::text,
            is_auto_shrink_on::text,
            is_auto_create_stats_on::text,
            is_auto_update_stats_on::text,
            is_auto_update_stats_async_on::text,
            is_read_committed_snapshot_on::text,
            snapshot_isolation_state,
            is_parameterization_forced::text,
            is_query_store_on::text,
            is_encrypted::text,
            is_trustworthy_on::text,
            is_db_chaining_on::text,
            is_broker_enabled::text,
            is_cdc_enabled::text,
            is_mixed_page_allocation_on::text,
            log_reuse_wait_desc,
            page_verify_option,
            target_recovery_time_seconds::text,
            delayed_durability,
            is_accelerated_database_recovery_on::text,
            is_memory_optimized_enabled::text,
            is_optimized_locking_on::text
        FROM v_database_config
        WHERE server_id = $1
        AND   capture_time <= $2
        AND   ($3::text[] IS NULL OR database_name = ANY($3))
        ORDER BY database_name, capture_time
        """;

    /// <summary>Every trace-flag snapshot for a server up to the window end (a row exists only while a flag is
    /// enabled), oldest-first — the diff set-diffs consecutive captures. $1 server_id, $2 window end (naive UTC).</summary>
    public const string TraceFlagChangesSnapshotsSql = """
        SELECT
            capture_time,
            trace_flag,
            status,
            is_global,
            is_session
        FROM v_trace_flags
        WHERE server_id = $1
        AND   capture_time <= $2
        ORDER BY capture_time, trace_flag
        """;

    /// <summary>The 27 database-config setting names, in the collector's exact column order — the WIDE store
    /// row is UNPIVOTed to a change row per changed setting, with this literal column name as the setting_name.
    /// Byte-identical to the service reader's list (DarlingConfigHistoryReader.DatabaseConfigSettingNames) and
    /// to the Configuration tab's 28-column read (minus its leading database_name).</summary>
    public static readonly IReadOnlyList<string> DatabaseConfigChangeSettingNames = new[]
    {
        "state_desc", "compatibility_level", "collation_name", "recovery_model", "is_read_only",
        "is_auto_close_on", "is_auto_shrink_on", "is_auto_create_stats_on", "is_auto_update_stats_on",
        "is_auto_update_stats_async_on", "is_read_committed_snapshot_on", "snapshot_isolation_state",
        "is_parameterization_forced", "is_query_store_on", "is_encrypted", "is_trustworthy_on",
        "is_db_chaining_on", "is_broker_enabled", "is_cdc_enabled", "is_mixed_page_allocation_on",
        "log_reuse_wait_desc", "page_verify_option", "target_recovery_time_seconds", "delayed_durability",
        "is_accelerated_database_recovery_on", "is_memory_optimized_enabled", "is_optimized_locking_on",
    };

    /* ─────────────────────────── snapshot row records (internal — the diff's raw input) ─────────────────────────── */

    internal sealed record ServerConfigSnapshot(
        DateTime CaptureTime, string ConfigurationName, long? ValueConfigured, long? ValueInUse, bool? IsDynamic, bool? IsAdvanced);

    internal sealed record DatabaseConfigSnapshot(
        DateTime CaptureTime, string DatabaseName, IReadOnlyList<string?> Values);

    internal sealed record TraceFlagSnapshot(
        DateTime CaptureTime, int TraceFlag, bool? Status, bool? IsGlobal, bool? IsSession);

    /* ─────────────────────────── public read + diff (composes the two) ─────────────────────────── */

    /// <summary>Server-configuration changes in the window [startUtc, endUtc]. Reads every snapshot up to
    /// endUtc (so the pre-window baseline is kept) then diffs per configuration_name in C#.</summary>
    public async Task<List<ServerConfigChangeRow>> GetServerConfigChangesAsync(
        int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        var snapshots = await ReadServerConfigSnapshotsAsync(serverId, endUtc, cancellationToken);
        return DiffServerConfigChanges(snapshots, startUtc, endUtc);
    }

    /// <summary>Database-configuration changes in the window [startUtc, endUtc]. Reads every snapshot up to
    /// endUtc then UNPIVOTs the wide row per (database, setting) whose value moved.</summary>
    public async Task<List<DatabaseConfigChangeRow>> GetDatabaseConfigChangesAsync(
        int serverId, DateTime startUtc, DateTime endUtc, IReadOnlyList<string>? databaseNames = null, CancellationToken cancellationToken = default)
    {
        var snapshots = await ReadDatabaseConfigSnapshotsAsync(serverId, endUtc, databaseNames, cancellationToken);
        return DiffDatabaseConfigChanges(snapshots, startUtc, endUtc);
    }

    /// <summary>Trace-flag changes in the window [startUtc, endUtc]. Reads every snapshot up to endUtc then
    /// set-diffs consecutive captures (flag appears = enabled, disappears = disabled, status/scope moves =
    /// modified).</summary>
    public async Task<List<TraceFlagChangeRow>> GetTraceFlagChangesAsync(
        int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        var snapshots = await ReadTraceFlagSnapshotsAsync(serverId, endUtc, cancellationToken);
        return DiffTraceFlagChanges(snapshots, startUtc, endUtc);
    }

    private async Task<List<ServerConfigSnapshot>> ReadServerConfigSnapshotsAsync(
        int serverId, DateTime endUtc, CancellationToken cancellationToken)
    {
        var rows = new List<ServerConfigSnapshot>();
        await using var command = _dataSource.CreateCommand(ServerConfigChangesSnapshotsSql);
        AddServerIdAndWindowEnd(command, serverId, endUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ServerConfigSnapshot(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetBoolean(5)));
        }

        return rows;
    }

    private async Task<List<DatabaseConfigSnapshot>> ReadDatabaseConfigSnapshotsAsync(
        int serverId, DateTime endUtc, IReadOnlyList<string>? databaseNames, CancellationToken cancellationToken)
    {
        var rows = new List<DatabaseConfigSnapshot>();
        await using var command = _dataSource.CreateCommand(DatabaseConfigChangesSnapshotsSql);
        AddServerIdAndWindowEnd(command, serverId, endUtc);
        command.Parameters.Add(DatabaseFilterParameter(databaseNames));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new string?[DatabaseConfigChangeSettingNames.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var ordinal = i + 2; /* capture_time, database_name precede the settings */
                values[i] = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            }

            rows.Add(new DatabaseConfigSnapshot(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                values));
        }

        return rows;
    }

    private async Task<List<TraceFlagSnapshot>> ReadTraceFlagSnapshotsAsync(
        int serverId, DateTime endUtc, CancellationToken cancellationToken)
    {
        var rows = new List<TraceFlagSnapshot>();
        await using var command = _dataSource.CreateCommand(TraceFlagChangesSnapshotsSql);
        AddServerIdAndWindowEnd(command, serverId, endUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TraceFlagSnapshot(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetBoolean(4)));
        }

        return rows;
    }

    /// <summary>$1 server_id, $2 window end re-stamped naive-UTC (the store is naive-UTC, matching the other
    /// windowed viewer reads — see ViewerDataService.SystemEvents.cs).</summary>
    private static void AddServerIdAndWindowEnd(NpgsqlCommand command, int serverId, DateTime endUtc)
    {
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified) });
    }

    /* ─────────────────────────── C# snapshot diffs (pure, unit-testable, no live PG) ─────────────────────────── */

    /// <summary>
    /// Diffs the sys.configurations snapshots into a change per (configuration_name) whose configured or in-use
    /// value moved between two consecutive captures (every config row is present in every snapshot, so a
    /// per-name walk is the whole story). Emits changes whose newer capture falls in [<paramref name="startUtc"/>,
    /// <paramref name="endUtc"/>], newest first. Mirrors DarlingConfigHistoryReader.ComputeServerConfigChanges
    /// plus the window's upper bound.
    /// </summary>
    internal static List<ServerConfigChangeRow> DiffServerConfigChanges(
        IReadOnlyList<ServerConfigSnapshot> snapshots, DateTime startUtc, DateTime endUtc)
    {
        var changes = new List<ServerConfigChangeRow>();
        foreach (var group in snapshots.GroupBy(s => s.ConfigurationName))
        {
            ServerConfigSnapshot? prev = null;
            foreach (var cur in group.OrderBy(s => s.CaptureTime))
            {
                if (prev is not null
                    && (prev.ValueConfigured != cur.ValueConfigured || prev.ValueInUse != cur.ValueInUse)
                    && cur.CaptureTime >= startUtc && cur.CaptureTime <= endUtc)
                {
                    changes.Add(new ServerConfigChangeRow
                    {
                        ChangeTime = cur.CaptureTime,
                        ConfigurationName = cur.ConfigurationName,
                        OldValueConfigured = prev.ValueConfigured,
                        NewValueConfigured = cur.ValueConfigured,
                        OldValueInUse = prev.ValueInUse,
                        NewValueInUse = cur.ValueInUse,
                        IsDynamic = cur.IsDynamic,
                        IsAdvanced = cur.IsAdvanced,
                    });
                }

                prev = cur;
            }
        }

        return changes.OrderByDescending(c => c.ChangeTime).ThenBy(c => c.ConfigurationName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Diffs the sys.databases snapshots into a change per (database, setting) whose value moved between two
    /// consecutive captures — the WIDE row is UNPIVOTed against <see cref="DatabaseConfigChangeSettingNames"/> so
    /// the setting_name is the literal column name. Emits changes in [<paramref name="startUtc"/>,
    /// <paramref name="endUtc"/>]. Mirrors DarlingConfigHistoryReader.ComputeDatabaseConfigChanges plus the upper bound.
    /// </summary>
    internal static List<DatabaseConfigChangeRow> DiffDatabaseConfigChanges(
        IReadOnlyList<DatabaseConfigSnapshot> snapshots, DateTime startUtc, DateTime endUtc)
    {
        var changes = new List<DatabaseConfigChangeRow>();
        foreach (var dbGroup in snapshots.GroupBy(s => s.DatabaseName))
        {
            DatabaseConfigSnapshot? prev = null;
            foreach (var cur in dbGroup.OrderBy(s => s.CaptureTime))
            {
                if (prev is not null && cur.CaptureTime >= startUtc && cur.CaptureTime <= endUtc)
                {
                    for (var i = 0; i < DatabaseConfigChangeSettingNames.Count; i++)
                    {
                        var oldVal = i < prev.Values.Count ? prev.Values[i] : null;
                        var newVal = i < cur.Values.Count ? cur.Values[i] : null;
                        if (!string.Equals(oldVal, newVal, StringComparison.Ordinal))
                        {
                            changes.Add(new DatabaseConfigChangeRow
                            {
                                ChangeTime = cur.CaptureTime,
                                DatabaseName = cur.DatabaseName,
                                SettingName = DatabaseConfigChangeSettingNames[i],
                                OldValue = oldVal,
                                NewValue = newVal,
                            });
                        }
                    }
                }

                prev = cur;
            }
        }

        return changes
            .OrderByDescending(c => c.ChangeTime)
            .ThenBy(c => c.DatabaseName, StringComparer.Ordinal)
            .ThenBy(c => c.SettingName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Set-diffs consecutive full trace-flag snapshots: a flag present in the newer capture but not the older =
    /// <c>enabled</c>; present in the older but not the newer = <c>disabled</c>; present in both with a changed
    /// status/scope = <c>modified</c>. Emits changes in [<paramref name="startUtc"/>, <paramref name="endUtc"/>].
    /// Mirrors DarlingConfigHistoryReader.ComputeTraceFlagChanges plus the upper bound.
    /// </summary>
    internal static List<TraceFlagChangeRow> DiffTraceFlagChanges(
        IReadOnlyList<TraceFlagSnapshot> snapshots, DateTime startUtc, DateTime endUtc)
    {
        var changes = new List<TraceFlagChangeRow>();
        var byCapture = snapshots.GroupBy(s => s.CaptureTime).OrderBy(g => g.Key).ToList();

        for (var i = 1; i < byCapture.Count; i++)
        {
            var changeTime = byCapture[i].Key;
            if (changeTime < startUtc || changeTime > endUtc)
            {
                continue;
            }

            var prevSnap = byCapture[i - 1].GroupBy(s => s.TraceFlag).ToDictionary(g => g.Key, g => g.First());
            var curSnap = byCapture[i].GroupBy(s => s.TraceFlag).ToDictionary(g => g.Key, g => g.First());

            foreach (var (flag, c) in curSnap)
            {
                if (!prevSnap.TryGetValue(flag, out var p))
                {
                    changes.Add(new TraceFlagChangeRow
                    {
                        ChangeTime = changeTime, TraceFlag = flag, PreviousStatus = null, NewStatus = c.Status,
                        IsGlobal = c.IsGlobal, IsSession = c.IsSession, ChangeType = "enabled",
                    });
                }
                else if (p.Status != c.Status || p.IsGlobal != c.IsGlobal || p.IsSession != c.IsSession)
                {
                    changes.Add(new TraceFlagChangeRow
                    {
                        ChangeTime = changeTime, TraceFlag = flag, PreviousStatus = p.Status, NewStatus = c.Status,
                        IsGlobal = c.IsGlobal, IsSession = c.IsSession, ChangeType = "modified",
                    });
                }
            }

            foreach (var (flag, p) in prevSnap)
            {
                if (!curSnap.ContainsKey(flag))
                {
                    changes.Add(new TraceFlagChangeRow
                    {
                        ChangeTime = changeTime, TraceFlag = flag, PreviousStatus = p.Status, NewStatus = null,
                        IsGlobal = p.IsGlobal, IsSession = p.IsSession, ChangeType = "disabled",
                    });
                }
            }
        }

        return changes.OrderByDescending(c => c.ChangeTime).ThenBy(c => c.TraceFlag).ToList();
    }
}

/* ─────────────────────────── grid row view-models (display props are pure C#, evaluated at bind time on the
   UI thread AFTER ViewerTimeHelper's offset is applied — same pattern as the System Events rows) ─────────────────────────── */

/// <summary>One server-configuration change (Server Config Changes grid). <see cref="RequiresRestart"/> and
/// <see cref="ChangeDescription"/> are DERIVED from the collected values, matching the Dashboard's
/// <c>report.server_configuration_changes</c> view definitions.</summary>
public sealed class ServerConfigChangeRow
{
    public DateTime ChangeTime { get; set; }
    public string ConfigurationName { get; set; } = "";
    public long? OldValueConfigured { get; set; }
    public long? NewValueConfigured { get; set; }
    public long? OldValueInUse { get; set; }
    public long? NewValueInUse { get; set; }
    public bool? IsDynamic { get; set; }
    public bool? IsAdvanced { get; set; }

    public string ChangeTimeDisplay => ViewerTimeHelper.ForDisplay(ChangeTime).ToString("yyyy-MM-dd HH:mm:ss");
    public string DynamicDisplay => IsDynamic == true ? "Yes" : "No";
    public string AdvancedDisplay => IsAdvanced == true ? "Yes" : "No";

    /* requires_restart = a non-dynamic setting whose configured value differs from the in-use value (the
       Dashboard view's exact definition: is_dynamic = 0 AND value_configured != value_in_use). Derivable from
       collected columns, so it is kept — NOT a collection gap. */
    public bool RequiresRestart => IsDynamic == false && NewValueConfigured != NewValueInUse;
    public string RequiresRestartDisplay => RequiresRestart ? "Yes" : "No";

    /* Derived narrative matching report.server_configuration_changes.change_description wording. */
    public string ChangeDescription =>
        OldValueConfigured != NewValueConfigured
            ? $"Configured value changed from {OldValueConfigured} to {NewValueConfigured}"
            : OldValueInUse != NewValueInUse
                ? $"In-use value changed from {OldValueInUse} to {NewValueInUse}"
                : "Value unchanged";
}

/// <summary>One database-configuration change (Database Config Changes grid). The Dashboard's
/// <c>setting_type</c> column is OMITTED (Darling's WIDE store has no setting_type — <see cref="SettingName"/>
/// is the literal column name); <see cref="ChangeDescription"/> is DERIVED, matching the Dashboard view.</summary>
public sealed class DatabaseConfigChangeRow
{
    public DateTime ChangeTime { get; set; }
    public string DatabaseName { get; set; } = "";
    public string SettingName { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    public string ChangeTimeDisplay => ViewerTimeHelper.ForDisplay(ChangeTime).ToString("yyyy-MM-dd HH:mm:ss");

    /* Derived narrative matching report.database_configuration_changes.change_description wording. */
    public string ChangeDescription =>
        OldValue is null && NewValue is not null ? $"Set to: {NewValue}"
        : OldValue is not null && NewValue is null ? $"Cleared (was: {OldValue})"
        : $"Changed from {OldValue} to {NewValue}";
}

/// <summary>One trace-flag change (Trace Flag Changes grid). <see cref="Scope"/> and
/// <see cref="ChangeDescription"/> are DERIVED, matching the Dashboard's <c>report.trace_flag_changes</c> view.</summary>
public sealed class TraceFlagChangeRow
{
    public DateTime ChangeTime { get; set; }
    public int TraceFlag { get; set; }
    public bool? PreviousStatus { get; set; }
    public bool? NewStatus { get; set; }
    public bool? IsGlobal { get; set; }
    public bool? IsSession { get; set; }

    /// <summary>enabled / disabled / modified (the set-diff outcome).</summary>
    public string ChangeType { get; set; } = "";

    public string ChangeTimeDisplay => ViewerTimeHelper.ForDisplay(ChangeTime).ToString("yyyy-MM-dd HH:mm:ss");
    public string PreviousStatusDisplay => StatusText(PreviousStatus);
    public string NewStatusDisplay => StatusText(NewStatus);
    public string GlobalDisplay => IsGlobal == true ? "Yes" : "No";
    public string SessionDisplay => IsSession == true ? "Yes" : "No";

    /* Matches report.trace_flag_changes.scope (GLOBAL / SESSION / UNKNOWN). */
    public string Scope => IsGlobal == true ? "GLOBAL" : IsSession == true ? "SESSION" : "UNKNOWN";

    /* Derived narrative matching report.trace_flag_changes.change_description wording. */
    public string ChangeDescription => ChangeType switch
    {
        "enabled" => $"Trace flag {TraceFlag} ENABLED",
        "disabled" => $"Trace flag {TraceFlag} DISABLED",
        "modified" => $"Trace flag {TraceFlag} scope changed",
        _ => "Status unchanged",
    };

    private static string StatusText(bool? status) => status is null ? "" : status.Value ? "ON" : "OFF";
}
