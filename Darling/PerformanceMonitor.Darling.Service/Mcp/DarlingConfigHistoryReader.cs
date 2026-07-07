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

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Service-side reads for the config / trace-flag MCP tools (<see cref="DarlingMcpConfigHistoryTools"/>).
/// The store keeps config as APPEND-ONLY snapshots keyed by <c>capture_time</c> (the collector writes every
/// setting every run, no dedup), so a change history genuinely exists — the three <c>*_changes</c> tools
/// diff consecutive snapshots. The diff runs in C# (not SQL) so it is unit-testable without a live Postgres
/// and so trace-flag enable/disable (a flag row appearing/disappearing between snapshots) is handled
/// correctly; the reads just fetch the ordered snapshots. get_database_scoped_config reads the latest
/// snapshot directly (the viewer's <c>DatabaseScopedConfigSql</c>). All are STORED reads (no live hit).
///
/// <para>
/// COLLECTION-CADENCE + SHAPE CAVEATS (surfaced in the tool descriptions, the MCP instructions, and the
/// READMEs, never silently dropped): the config collectors run ON CONNECT only (<c>FrequencyMinutes = 0</c>),
/// so change granularity equals the service's connect/restart cadence, and a stable always-connected
/// deployment may hold a single snapshot (→ no changes yet). The change tools emit ONLY collected values —
/// the Dashboard's enrichment columns that Darling does not collect are omitted: server-config
/// <c>requires_restart</c> / <c>description</c>, database-config <c>setting_type</c> (the store is WIDE, so
/// <c>setting_name</c> is the literal column name), and the generated <c>change_description</c> narrative.
/// </para>
/// </summary>
internal static class DarlingConfigHistoryReader
{
    /* ─────────────────────────── snapshot rows ─────────────────────────── */

    public sealed record ServerConfigSnapshotRow(
        DateTime CaptureTime, string ConfigurationName, long? ValueConfigured, long? ValueInUse, bool? IsDynamic, bool? IsAdvanced);

    public sealed record DatabaseConfigSnapshotRow(
        DateTime CaptureTime, string DatabaseName, IReadOnlyList<string?> Values);

    public sealed record TraceFlagSnapshotRow(
        DateTime CaptureTime, int TraceFlag, bool? Status, bool? IsGlobal, bool? IsSession);

    public sealed record DatabaseScopedConfigReadRow(
        string DatabaseName, string ConfigurationName, string? Value, string? ValueForSecondary);

    /* ─────────────────────────── change rows ─────────────────────────── */

    public sealed record ServerConfigChange(
        DateTime ChangeTime, string ConfigurationName, long? OldValueConfigured, long? NewValueConfigured,
        long? OldValueInUse, long? NewValueInUse, bool? IsDynamic, bool? IsAdvanced);

    public sealed record DatabaseConfigChange(
        DateTime ChangeTime, string DatabaseName, string SettingName, string? OldValue, string? NewValue);

    public sealed record TraceFlagChange(
        DateTime ChangeTime, int TraceFlag, bool? PreviousStatus, bool? NewStatus, bool? IsGlobal, bool? IsSession, string ChangeType);

    /// <summary>The 27 database-config setting names, in the collector's exact column order — the WIDE store
    /// is UNPIVOTed to a change row per changed setting, with this literal column name as the setting_name.</summary>
    public static readonly IReadOnlyList<string> DatabaseConfigSettingNames = new[]
    {
        "state_desc", "compatibility_level", "collation_name", "recovery_model", "is_read_only",
        "is_auto_close_on", "is_auto_shrink_on", "is_auto_create_stats_on", "is_auto_update_stats_on",
        "is_auto_update_stats_async_on", "is_read_committed_snapshot_on", "snapshot_isolation_state",
        "is_parameterization_forced", "is_query_store_on", "is_encrypted", "is_trustworthy_on",
        "is_db_chaining_on", "is_broker_enabled", "is_cdc_enabled", "is_mixed_page_allocation_on",
        "log_reuse_wait_desc", "page_verify_option", "target_recovery_time_seconds", "delayed_durability",
        "is_accelerated_database_recovery_on", "is_memory_optimized_enabled", "is_optimized_locking_on",
    };

    /* ─────────────────────────── server config snapshots ─────────────────────────── */

    /// <summary>Every sys.configurations snapshot for a server, oldest-first — the change tool diffs
    /// consecutive captures per configuration_name (rows are always all-present, so a per-name walk suffices).
    /// $1 server_id.</summary>
    public const string ServerConfigSnapshotsSql = """
        SELECT
            capture_time,
            configuration_name,
            value_configured,
            value_in_use,
            is_dynamic,
            is_advanced
        FROM v_server_config
        WHERE server_id = $1
        ORDER BY configuration_name, capture_time
        """;

    public static async Task<List<ServerConfigSnapshotRow>> GetServerConfigSnapshotsAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken = default)
    {
        var rows = new List<ServerConfigSnapshotRow>();
        await using var command = postgres.CreateCommand(ServerConfigSnapshotsSql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ServerConfigSnapshotRow(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetBoolean(5)));
        }

        return rows;
    }

    /* ─────────────────────────── database config snapshots (WIDE) ─────────────────────────── */

    /// <summary>Every sys.databases snapshot for a server, with the 27 setting columns CAST to text for a
    /// uniform value-diff. $1 server_id. The SELECT order is <see cref="DatabaseConfigSettingNames"/>.</summary>
    public const string DatabaseConfigSnapshotsSql = """
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
        ORDER BY database_name, capture_time
        """;

    public static async Task<List<DatabaseConfigSnapshotRow>> GetDatabaseConfigSnapshotsAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken = default)
    {
        var rows = new List<DatabaseConfigSnapshotRow>();
        await using var command = postgres.CreateCommand(DatabaseConfigSnapshotsSql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new string?[DatabaseConfigSettingNames.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var ordinal = i + 2; /* capture_time, database_name precede the settings */
                values[i] = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            }

            rows.Add(new DatabaseConfigSnapshotRow(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                values));
        }

        return rows;
    }

    /* ─────────────────────────── trace flag snapshots ─────────────────────────── */

    /// <summary>Every trace-flag snapshot for a server (a row exists only while a flag is enabled), oldest
    /// first — the change tool set-diffs consecutive captures so a flag appearing = enabled, disappearing =
    /// disabled. $1 server_id.</summary>
    public const string TraceFlagSnapshotsSql = """
        SELECT
            capture_time,
            trace_flag,
            status,
            is_global,
            is_session
        FROM v_trace_flags
        WHERE server_id = $1
        ORDER BY capture_time, trace_flag
        """;

    public static async Task<List<TraceFlagSnapshotRow>> GetTraceFlagSnapshotsAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken = default)
    {
        var rows = new List<TraceFlagSnapshotRow>();
        await using var command = postgres.CreateCommand(TraceFlagSnapshotsSql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TraceFlagSnapshotRow(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetBoolean(4)));
        }

        return rows;
    }

    /* ─────────────────────────── database scoped config (latest snapshot) ─────────────────────────── */

    /// <summary>The latest sys.database_scoped_configurations snapshot — the viewer's
    /// <c>DatabaseScopedConfigSql</c>. $1 server_id.</summary>
    public const string DatabaseScopedConfigSql = """
        SELECT database_name, configuration_name, value, value_for_secondary
        FROM v_database_scoped_config
        WHERE server_id = $1
        AND   capture_time = (SELECT MAX(capture_time) FROM v_database_scoped_config WHERE server_id = $1)
        ORDER BY database_name, configuration_name
        """;

    public static async Task<List<DatabaseScopedConfigReadRow>> GetLatestDatabaseScopedConfigAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken = default)
    {
        var rows = new List<DatabaseScopedConfigReadRow>();
        await using var command = postgres.CreateCommand(DatabaseScopedConfigSql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DatabaseScopedConfigReadRow(
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /* ─────────────────────────── C# snapshot diffs (unit-testable, no live PG) ─────────────────────────── */

    /// <summary>
    /// Diffs the sys.configurations snapshots into a change per (configuration_name) whose configured or
    /// in-use value moved between two consecutive captures — every config row is present in every snapshot,
    /// so a per-name walk over captures is the whole story. Emits changes whose newer capture is at/after
    /// <paramref name="windowStartUtc"/>, newest first.
    /// </summary>
    public static List<ServerConfigChange> ComputeServerConfigChanges(
        IReadOnlyList<ServerConfigSnapshotRow> snapshots, DateTime windowStartUtc)
    {
        var changes = new List<ServerConfigChange>();
        foreach (var group in snapshots.GroupBy(s => s.ConfigurationName))
        {
            ServerConfigSnapshotRow? prev = null;
            foreach (var cur in group.OrderBy(s => s.CaptureTime))
            {
                if (prev is not null
                    && (prev.ValueConfigured != cur.ValueConfigured || prev.ValueInUse != cur.ValueInUse)
                    && cur.CaptureTime >= windowStartUtc)
                {
                    changes.Add(new ServerConfigChange(
                        cur.CaptureTime, cur.ConfigurationName, prev.ValueConfigured, cur.ValueConfigured,
                        prev.ValueInUse, cur.ValueInUse, cur.IsDynamic, cur.IsAdvanced));
                }

                prev = cur;
            }
        }

        return changes.OrderByDescending(c => c.ChangeTime).ThenBy(c => c.ConfigurationName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Diffs the sys.databases snapshots into a change per (database, setting) whose value moved between two
    /// consecutive captures — the WIDE row is UNPIVOTed against <see cref="DatabaseConfigSettingNames"/> so
    /// the setting_name is the literal column name. Emits changes at/after <paramref name="windowStartUtc"/>.
    /// </summary>
    public static List<DatabaseConfigChange> ComputeDatabaseConfigChanges(
        IReadOnlyList<DatabaseConfigSnapshotRow> snapshots, DateTime windowStartUtc)
    {
        var changes = new List<DatabaseConfigChange>();
        foreach (var dbGroup in snapshots.GroupBy(s => s.DatabaseName))
        {
            DatabaseConfigSnapshotRow? prev = null;
            foreach (var cur in dbGroup.OrderBy(s => s.CaptureTime))
            {
                if (prev is not null && cur.CaptureTime >= windowStartUtc)
                {
                    for (var i = 0; i < DatabaseConfigSettingNames.Count; i++)
                    {
                        var oldVal = i < prev.Values.Count ? prev.Values[i] : null;
                        var newVal = i < cur.Values.Count ? cur.Values[i] : null;
                        if (!string.Equals(oldVal, newVal, StringComparison.Ordinal))
                        {
                            changes.Add(new DatabaseConfigChange(
                                cur.CaptureTime, cur.DatabaseName, DatabaseConfigSettingNames[i], oldVal, newVal));
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
    /// Set-diffs consecutive full trace-flag snapshots: a flag present in the newer capture but not the older
    /// = <c>enabled</c>; present in the older but not the newer = <c>disabled</c>; present in both with a
    /// changed status/scope = <c>modified</c>. Emits changes at/after <paramref name="windowStartUtc"/>.
    /// </summary>
    public static List<TraceFlagChange> ComputeTraceFlagChanges(
        IReadOnlyList<TraceFlagSnapshotRow> snapshots, DateTime windowStartUtc)
    {
        var changes = new List<TraceFlagChange>();
        var byCapture = snapshots.GroupBy(s => s.CaptureTime).OrderBy(g => g.Key).ToList();

        for (var i = 1; i < byCapture.Count; i++)
        {
            var changeTime = byCapture[i].Key;
            if (changeTime < windowStartUtc)
            {
                continue;
            }

            var prevSnap = byCapture[i - 1].GroupBy(s => s.TraceFlag).ToDictionary(g => g.Key, g => g.First());
            var curSnap = byCapture[i].GroupBy(s => s.TraceFlag).ToDictionary(g => g.Key, g => g.First());

            foreach (var (flag, c) in curSnap)
            {
                if (!prevSnap.TryGetValue(flag, out var p))
                {
                    changes.Add(new TraceFlagChange(changeTime, flag, null, c.Status, c.IsGlobal, c.IsSession, "enabled"));
                }
                else if (p.Status != c.Status || p.IsGlobal != c.IsGlobal || p.IsSession != c.IsSession)
                {
                    changes.Add(new TraceFlagChange(changeTime, flag, p.Status, c.Status, c.IsGlobal, c.IsSession, "modified"));
                }
            }

            foreach (var (flag, p) in prevSnap)
            {
                if (!curSnap.ContainsKey(flag))
                {
                    changes.Add(new TraceFlagChange(changeTime, flag, p.Status, null, p.IsGlobal, p.IsSession, "disabled"));
                }
            }
        }

        return changes.OrderByDescending(c => c.ChangeTime).ThenBy(c => c.TraceFlag).ToList();
    }
}
