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

/// <summary>
/// One RAW CPU ring-buffer sample: a sample time plus the SQL Server and other-process CPU percentages.
/// This raw-per-sample read (up to ~60 samples per collection) feeds BOTH the CPU tab's scatter chart
/// (SQL Server vs other processes) and — since W1d — the Overview's CPU lane (SQL vs SQL+other Total),
/// mirroring Lite's CPU tab (<c>ServerTab.Charts.cs</c> <c>UpdateCpuChart</c>) and Lite's Overview lanes,
/// both over <c>LocalDataService.GetCpuUtilizationAsync</c>. Both CPU columns are <c>integer</c> in the
/// store, read as int and cast to double at plot time, byte-for-byte with Lite's chart body.
/// </summary>
public sealed record CpuUtilizationSample(DateTime SampleTime, int SqlServerCpu, int OtherProcessCpu);

public sealed partial class ViewerDataService
{
    /// <summary>
    /// The raw per-sample CPU read: every ring-buffer sample since <paramref name="sinceUtc"/> (not an
    /// average-per-collection roll-up), feeding both the CPU tab's scatter chart and — since W1d — the
    /// Overview's CPU lane. Deliberate choices: the window filters on <c>collection_time</c> (the
    /// naive-UTC collection prefix — the reliable clock every Darling read windows on), while
    /// <c>sample_time</c> (server-LOCAL wall clock from SYSDATETIME on the monitored server) is selected
    /// only for the axis; a NULL <c>other_process_cpu_utilization</c> (SQL on Linux, #1048) reads as 0.
    /// Reads the base <c>cpu_utilization_stats</c> table.
    /// $1 server_id, $2 window start (naive UTC).
    /// </summary>
    public const string CpuUtilizationSql = """
        SELECT sample_time, sqlserver_cpu_utilization, other_process_cpu_utilization
        FROM cpu_utilization_stats
        WHERE server_id = $1
        AND   collection_time >= $2
        ORDER BY sample_time
        """;

    /// <summary>
    /// Raw CPU samples for one server since <paramref name="sinceUtc"/>, time-ordered — one point per
    /// ring-buffer sample, for the CPU tab's scatter chart.
    /// </summary>
    public async Task<List<CpuUtilizationSample>> GetCpuUtilizationAsync(int serverId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        var samples = new List<CpuUtilizationSample>();

        await using var command = _dataSource.CreateCommand(CpuUtilizationSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Unspecified),
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new CpuUtilizationSample(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                /* NULL other-process CPU (SQL on Linux) reads as 0, like Lite's CPU chart. */
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2)));
        }

        return samples;
    }
}
