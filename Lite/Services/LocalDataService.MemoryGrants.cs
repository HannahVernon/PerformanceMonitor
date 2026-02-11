/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DuckDB.NET.Data;

namespace PerformanceMonitorLite.Services;

public partial class LocalDataService
{
    /// <summary>
    /// Gets the most recent memory grant snapshot for a server.
    /// </summary>
    public async Task<List<MemoryGrantStatsRow>> GetMemoryGrantStatsAsync(int serverId, int hoursBack = 1)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    collection_time,
    session_id,
    database_name,
    query_text,
    requested_memory_mb,
    granted_memory_mb,
    used_memory_mb,
    max_used_memory_mb,
    ideal_memory_mb,
    required_memory_mb,
    wait_time_ms,
    is_small_grant,
    dop,
    query_cost
FROM memory_grant_stats
WHERE server_id = $1
AND   collection_time >= $2
ORDER BY collection_time DESC, granted_memory_mb DESC";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-hoursBack) });

        var items = new List<MemoryGrantStatsRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new MemoryGrantStatsRow
            {
                CollectionTime = reader.GetDateTime(0),
                SessionId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                DatabaseName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                QueryText = reader.IsDBNull(3) ? "" : reader.GetString(3),
                RequestedMemoryMb = reader.IsDBNull(4) ? 0 : ToDouble(reader.GetValue(4)),
                GrantedMemoryMb = reader.IsDBNull(5) ? 0 : ToDouble(reader.GetValue(5)),
                UsedMemoryMb = reader.IsDBNull(6) ? 0 : ToDouble(reader.GetValue(6)),
                MaxUsedMemoryMb = reader.IsDBNull(7) ? 0 : ToDouble(reader.GetValue(7)),
                IdealMemoryMb = reader.IsDBNull(8) ? 0 : ToDouble(reader.GetValue(8)),
                RequiredMemoryMb = reader.IsDBNull(9) ? 0 : ToDouble(reader.GetValue(9)),
                WaitTimeMs = reader.IsDBNull(10) ? 0 : ToInt64(reader.GetValue(10)),
                IsSmallGrant = !reader.IsDBNull(11) && reader.GetBoolean(11),
                Dop = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                QueryCost = reader.IsDBNull(13) ? 0 : ToDouble(reader.GetValue(13))
            });
        }

        return items;
    }

    /// <summary>
    /// Gets memory grant trend — total granted MB per collection snapshot for charting.
    /// </summary>
    public async Task<List<MemoryTrendPoint>> GetMemoryGrantTrendAsync(int serverId, int hoursBack = 4, DateTime? fromDate = null, DateTime? toDate = null)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();

        var (startTime, endTime) = GetTimeRange(hoursBack, fromDate, toDate);

        command.CommandText = @"
SELECT
    collection_time,
    0 AS total_server_memory_mb,
    0 AS target_server_memory_mb,
    0 AS buffer_pool_mb,
    SUM(granted_memory_mb) AS total_granted_mb
FROM memory_grant_stats
WHERE server_id = $1
AND   collection_time >= $2
AND   collection_time <= $3
GROUP BY collection_time
ORDER BY collection_time";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = startTime });
        command.Parameters.Add(new DuckDBParameter { Value = endTime });

        var items = new List<MemoryTrendPoint>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new MemoryTrendPoint
            {
                CollectionTime = reader.GetDateTime(0),
                TotalGrantedMb = reader.IsDBNull(4) ? 0 : ToDouble(reader.GetValue(4))
            });
        }
        return items;
    }
}

public class MemoryGrantStatsRow
{
    public DateTime CollectionTime { get; set; }
    public int SessionId { get; set; }
    public string DatabaseName { get; set; } = "";
    public string QueryText { get; set; } = "";
    public double RequestedMemoryMb { get; set; }
    public double GrantedMemoryMb { get; set; }
    public double UsedMemoryMb { get; set; }
    public double MaxUsedMemoryMb { get; set; }
    public double IdealMemoryMb { get; set; }
    public double RequiredMemoryMb { get; set; }
    public long WaitTimeMs { get; set; }
    public bool IsSmallGrant { get; set; }
    public int Dop { get; set; }
    public double QueryCost { get; set; }

    public string GrantEfficiency => GrantedMemoryMb > 0
        ? $"{MaxUsedMemoryMb / GrantedMemoryMb * 100:F0}%"
        : "N/A";
}
