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

    private readonly NpgsqlDataSource _dataSource;

    public ViewerDataService(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
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

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
