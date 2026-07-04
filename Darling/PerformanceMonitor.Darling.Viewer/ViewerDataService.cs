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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>One row of the servers table, as the viewer's server list shows it.</summary>
public sealed record DarlingServer(
    int ServerId,
    string ServerName,
    string DisplayName,
    bool IsEnabled,
    int? SqlMajorVersion)
{
    /// <summary>"SQL Server 2022"-style label for the server list; empty when the version is unknown.</summary>
    public string VersionLabel => ViewerDataService.SqlVersionLabel(SqlMajorVersion);
}

/// <summary>
/// The viewer's reads of the Darling Postgres store — the server list, plus the surfaces in the
/// partials (the Overview lanes' total-wait + memory trends and per-lane baselines in
/// <c>ViewerDataService.OverviewLanes.cs</c>, the per-tab reads in <c>.Cpu.cs</c>, <c>.Waits.cs</c>,
/// <c>.BlockingTrends.cs</c>, <c>.FileIo.cs</c>, <c>.TempDb.cs</c>, <c>.Config.cs</c>, and
/// <c>.RunningJobs.cs</c>, the Daily Summary + Collection Health reads in <c>.DailySummary.cs</c> /
/// <c>.CollectionHealth.cs</c>, and the wave-2/3 reads in <c>.QueryStats.cs</c>, <c>.Blocking.cs</c>,
/// <c>.Findings.cs</c>, <c>.AlertHistory.cs</c>, and <c>.MuteRules.cs</c>).
/// Connections come from a pooled <see cref="NpgsqlDataSource"/>, so the window can run its
/// per-tab queries concurrently. The SQL lives in public constants so tests can pin the
/// load-bearing clauses without a live Postgres.
/// All timestamps in the store are naive UTC (`timestamp without time zone`), so DateTime
/// parameters are sent with DateTimeKind.Unspecified — since Npgsql 6.0 a Kind=Utc DateTime
/// maps strictly to timestamptz and throws against naive columns.
/// </summary>
public sealed partial class ViewerDataService : IAsyncDisposable
{
    public const string ServersSql =
        "SELECT server_id, server_name, display_name, is_enabled, sql_major_version FROM servers ORDER BY display_name";

    /// <summary>
    /// The authoritative read-only probe (V8 security hardening): does the connected role hold INSERT
    /// on a <c>config</c> table? True → the admin role (or an owner) — the mute / alert-dismiss /
    /// analysis-mute writes are available. False → the read-only viewer role — those surfaces degrade.
    /// This is the source of truth over <c>connectAs</c> (which only picks a credential and doesn't
    /// apply in BYO mode), because it reflects the connection's ACTUAL privileges. The bare table name
    /// resolves through search_path to <c>config.config_mute_rules</c>.
    /// </summary>
    public const string ReadOnlyProbeSql = "SELECT has_table_privilege('config_mute_rules', 'INSERT')";

    private readonly NpgsqlDataSource _dataSource;

    public ViewerDataService(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>
    /// True when the connected role cannot write the operator-config tables (the read-only
    /// <c>viewer</c> role, or any connection lacking config INSERT). Set by
    /// <see cref="DetectReadOnlyAsync"/>; the write surfaces gate on it. Defaults false (writable) until
    /// probed, then fails safe to true if the probe cannot run.
    /// </summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>
    /// Runs the <see cref="ReadOnlyProbeSql"/> capability probe and records <see cref="IsReadOnly"/>.
    /// Called once after the service connects, before the write affordances are shown. A probe that
    /// throws (table missing on a mis-provisioned store, permission quirk, transient error) fails safe
    /// to read-only, so the UI hides writes rather than dead-clicking into a permission error; the
    /// reactive 42501 catch in the write paths is the backstop if the probe and reality ever disagree.
    /// </summary>
    public async Task<bool> DetectReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = _dataSource.CreateCommand(ReadOnlyProbeSql);
            var canInsert = await command.ExecuteScalarAsync(cancellationToken);
            IsReadOnly = canInsert is not true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsReadOnly = true;
        }

        return IsReadOnly;
    }

    /// <summary>All registered servers, ordered as the server list displays them.</summary>
    public async Task<List<DarlingServer>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        var servers = new List<DarlingServer>();

        await using var command = _dataSource.CreateCommand(ServersSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var serverName = reader.GetString(1);
            servers.Add(new DarlingServer(
                reader.GetInt32(0),
                serverName,
                reader.IsDBNull(2) ? serverName : reader.GetString(2),
                !reader.IsDBNull(3) && reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        return servers;
    }

    /// <summary>
    /// Product-name label for a sql_major_version (2016+ is what the product supports; older or
    /// unknown majors fall back to a bare version tag, null to empty).
    /// </summary>
    public static string SqlVersionLabel(int? sqlMajorVersion) => sqlMajorVersion switch
    {
        null => "",
        11 => "SQL Server 2012",
        12 => "SQL Server 2014",
        13 => "SQL Server 2016",
        14 => "SQL Server 2017",
        15 => "SQL Server 2019",
        16 => "SQL Server 2022",
        17 => "SQL Server 2025",
        _ => $"SQL Server v{sqlMajorVersion}",
    };

    /// <summary>The store's timestamps are naive UTC; re-stamp as UTC and convert for display.</summary>
    public static DateTime ToLocalTime(DateTime naiveUtc)
        => DateTime.SpecifyKind(naiveUtc, DateTimeKind.Utc).ToLocalTime();

    /// <summary>Postgres SQLSTATE 42501 (insufficient_privilege) — a write refused on a read-only connection.</summary>
    internal const string InsufficientPrivilegeSqlState = "42501";

    /// <summary>
    /// Executes a write command, translating a permission-denied failure — a write attempted on the
    /// read-only viewer role, or after grants changed under a running app — into a
    /// <see cref="ViewerReadOnlyException"/> the UI shows as a clear "read-only connection" message
    /// instead of a raw Postgres error. The reactive backstop to the proactive hide/disable; any other
    /// failure propagates unchanged.
    /// </summary>
    internal async Task<int> ExecuteWriteAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilegeSqlState)
        {
            throw new ViewerReadOnlyException(ex);
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

/// <summary>
/// A write was attempted on a read-only Darling connection (the least-privilege <c>viewer</c> role,
/// or any connection lacking <c>config</c> INSERT). Thrown by the write paths when Postgres returns
/// 42501 so the UI shows a clear, actionable message rather than a raw permission error. The proactive
/// <see cref="ViewerDataService.IsReadOnly"/> gating normally hides the affordances first; this covers
/// the race where the probe and the live grants disagree.
/// </summary>
public sealed class ViewerReadOnlyException : Exception
{
    public ViewerReadOnlyException(Exception innerException)
        : base(
            "This viewer is connected with a read-only role, so it can't change mute rules, dismiss alerts, " +
            "or mute findings. Set postgres.connectAs to \"admin\" in darling.json and restart the viewer to " +
            "enable these actions.",
            innerException)
    {
    }
}
