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
/// FinOps Server Inventory reads. Lite queries each target LIVE (<c>GetServerPropertiesLiveAsync</c>) — the
/// headless viewer can't reach targets, so the inventory rows come from the COLLECTED
/// <c>server_properties</c> table (Darling collects it; <c>PgFactCollector.Config.ServerPropertiesSql</c>
/// reads it the same way) joined to the <c>servers</c> registry, with the collected metrics
/// (<see cref="ServerMetricsSql"/>) overlaid per server by the loader. Column parity vs Lite's live query:
/// the collected table carries edition/version/level/CPU/memory/sockets/cores/HADR/clustered, so those
/// surface; it does NOT carry sqlserver_start_time / host OS / AG replica role, so those Lite columns are
/// omitted (nothing stubbed). SQL kept in <c>public const</c> so tests pin it.
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>Collected 24h-CPU / storage / idle-DB / provisioning metrics for one server. $1 server_id, $2 cpu cutoff (24h), $3 idle cutoff (7d).</summary>
    public const string ServerMetricsSql = @"
WITH cpu_24h AS (
    SELECT
        AVG(CAST(sqlserver_cpu_utilization AS DECIMAL(5,2))) AS avg_cpu_pct,
        MAX(sqlserver_cpu_utilization) AS max_cpu_pct,
        PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY sqlserver_cpu_utilization) AS p95_cpu_pct
    FROM v_cpu_utilization_stats
    WHERE server_id = $1
    AND   collection_time >= $2
),
mem_latest AS (
    SELECT
        CAST(total_server_memory_mb AS DECIMAL(10,2)) / NULLIF(target_server_memory_mb, 0) AS memory_ratio
    FROM v_memory_stats
    WHERE server_id = $1
    AND   (server_id, collection_time) IN (
        SELECT server_id, MAX(collection_time)
        FROM v_memory_stats
        WHERE server_id = $1
        GROUP BY server_id
    )
),
storage_totals AS (
    SELECT
        SUM(total_size_mb) / 1024.0 AS total_storage_gb
    FROM v_database_size_stats
    WHERE server_id = $1
    AND   (server_id, collection_time) IN (
        SELECT server_id, MAX(collection_time)
        FROM v_database_size_stats
        WHERE server_id = $1
        GROUP BY server_id
    )
),
idle_dbs AS (
    SELECT
        COUNT(DISTINCT database_name) AS idle_db_count
    FROM (
        SELECT database_name
        FROM v_database_size_stats
        WHERE server_id = $1
        AND   (server_id, collection_time) IN (
            SELECT server_id, MAX(collection_time)
            FROM v_database_size_stats
            WHERE server_id = $1
            GROUP BY server_id
        )
        AND database_name NOT IN ('master', 'model', 'msdb', 'tempdb', 'PerformanceMonitor')
        EXCEPT
        SELECT DISTINCT database_name
        FROM v_query_stats
        WHERE server_id = $1
        AND   collection_time >= $3
        AND   delta_execution_count > 0
    ) AS idle
)
SELECT
    c.avg_cpu_pct,
    st.total_storage_gb,
    id.idle_db_count,
    CASE
        WHEN c.avg_cpu_pct < 15 AND c.max_cpu_pct < 40 AND COALESCE(m.memory_ratio, 0) < 0.5
        THEN 'OVER_PROVISIONED'
        WHEN c.p95_cpu_pct > 85 OR COALESCE(m.memory_ratio, 0) > 0.95
        THEN 'UNDER_PROVISIONED'
        ELSE 'RIGHT_SIZED'
    END AS provisioning_status
FROM (SELECT 1) AS anchor
LEFT JOIN cpu_24h c ON true
LEFT JOIN mem_latest m ON true
LEFT JOIN storage_totals st ON true
LEFT JOIN idle_dbs id ON true";

    public async Task<(decimal? AvgCpuPct, decimal? StorageTotalGb, int? IdleDbCount, string? ProvisioningStatus)> GetServerMetricsAsync(int serverId, CancellationToken cancellationToken = default)
    {
        var cpuCutoff = DateTime.UtcNow.AddHours(-24);
        var idleCutoff = DateTime.UtcNow.AddDays(-7);

        await using var command = _dataSource.CreateCommand(ServerMetricsSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(cpuCutoff, DateTimeKind.Unspecified) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(idleCutoff, DateTimeKind.Unspecified) });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (
                reader.IsDBNull(0) ? null : Convert.ToDecimal(reader.GetValue(0)),
                reader.IsDBNull(1) ? null : Convert.ToDecimal(reader.GetValue(1)),
                reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)
            );
        }

        return (null, null, null, null);
    }

    /// <summary>
    /// Latest collected properties per server joined to the registry — the Server Inventory base rows (metrics
    /// overlaid per row by the loader). DISTINCT ON keeps each server's newest server_properties row.
    /// </summary>
    public const string ServerInventorySql = @"
SELECT
    sp.server_id,
    COALESCE(s.display_name, s.server_name) AS server_name,
    sp.edition,
    sp.product_version,
    sp.product_level,
    sp.product_update_level,
    sp.engine_edition,
    sp.cpu_count,
    sp.physical_memory_mb,
    sp.socket_count,
    sp.cores_per_socket,
    sp.is_hadr_enabled,
    sp.is_clustered,
    sp.collection_time
FROM (
    SELECT DISTINCT ON (server_id)
        server_id, edition, product_version, product_level, product_update_level,
        engine_edition, cpu_count, physical_memory_mb, socket_count, cores_per_socket,
        is_hadr_enabled, is_clustered, collection_time
    FROM server_properties
    ORDER BY server_id, collection_time DESC
) sp
JOIN servers s ON s.server_id = sp.server_id
ORDER BY server_name";

    public async Task<List<ServerPropertyRow>> GetServerInventoryAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(ServerInventorySql);

        var items = new List<ServerPropertyRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var level = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var updateLevel = reader.IsDBNull(5) ? null : reader.GetString(5);
            var versionDisplay = !string.IsNullOrEmpty(updateLevel)
                ? $"{version} - {updateLevel}"
                : $"{version} - {level}";

            items.Add(new ServerPropertyRow
            {
                ServerId = reader.GetInt32(0),
                ServerName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Edition = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ProductVersion = versionDisplay,
                EngineEdition = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6)),
                CpuCount = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7)),
                PhysicalMemoryMb = reader.IsDBNull(8) ? 0L : Convert.ToInt64(reader.GetValue(8)),
                SocketCount = reader.IsDBNull(9) ? null : Convert.ToInt32(reader.GetValue(9)),
                CoresPerSocket = reader.IsDBNull(10) ? null : Convert.ToInt32(reader.GetValue(10)),
                IsHadrEnabled = reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                IsClustered = reader.IsDBNull(12) ? null : reader.GetBoolean(12),
                LastUpdated = reader.IsDBNull(13) ? null : ToLocalTime(reader.GetDateTime(13))
            });
        }
        return items;
    }
}
