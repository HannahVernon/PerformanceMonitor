/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Overview server-cards read (W2a viewer copy-parity), copied from Lite's
/// <c>LocalDataService.Overview.cs</c> (GetServerSummaryAsync + ServerSummaryItem) and rewired to the
/// Darling Postgres store. The five per-server reads mirror Lite's exactly — latest CPU (SQL + other),
/// latest total server memory, blocking count in the last hour (XE blocked-process reports, falling
/// back to the always-on DMV snapshot when the XE count is zero), deadlock count in the last hour, and
/// the last collection time — over the same <c>v_*</c> passthrough views the other viewer tabs read.
/// The SQL lives in public constants so tests can pin the load-bearing clauses without a live Postgres.
///
/// <para><b>The one semantic change (#1262 headless plan).</b> Lite's <c>IsOnline</c> comes from a live
/// per-server connection ping; the viewer has no live connection to the monitored servers, so it derives
/// the card's status from COLLECTION FRESHNESS instead — how old the newest <c>v_collection_log</c> row
/// is (see <see cref="ServerSummaryItem.ClassifyFreshness"/>): fresh → Online (green), stale (older than
/// twice the fastest collector's cadence) → Warning (amber), and no collection / long-dead → Offline
/// (the red overlay). The freshness result is mapped onto Lite's own (IsOnline, HasCollectorErrors)
/// inputs so the card view-model is otherwise a verbatim copy of Lite's.</para>
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>Latest SQL + other-process CPU for one server (newest ring-buffer sample). $1 server_id.</summary>
    public const string ServerSummaryCpuSql = @"
SELECT sqlserver_cpu_utilization, other_process_cpu_utilization
FROM v_cpu_utilization_stats
WHERE server_id = $1
ORDER BY sample_time DESC
LIMIT 1";

    /// <summary>Latest total server memory (MB) for one server. $1 server_id.</summary>
    public const string ServerSummaryMemorySql = @"
SELECT CAST(total_server_memory_mb AS double precision)
FROM v_memory_stats
WHERE server_id = $1
ORDER BY collection_time DESC
LIMIT 1";

    /// <summary>
    /// Blocking events in the window: XE blocked-process reports, falling back to the always-on DMV
    /// blocking snapshot count when the XE count is zero (AWS RDS has no XE) — Lite's
    /// <c>COALESCE(NULLIF(...))</c> shape. $1 server_id, $2 window start (naive UTC).
    /// </summary>
    public const string ServerSummaryBlockingSql = @"
SELECT COALESCE(NULLIF(
    (SELECT COUNT(*) FROM v_blocked_process_reports WHERE server_id = $1 AND event_time >= $2), 0),
    (SELECT COUNT(*) FROM v_dmv_blocking_snapshots WHERE server_id = $1 AND event_time >= $2))";

    /// <summary>Deadlock count in the window. $1 server_id, $2 window start (naive UTC).</summary>
    public const string ServerSummaryDeadlockSql = @"
SELECT COUNT(*)
FROM v_deadlocks
WHERE server_id = $1
AND   deadlock_time >= $2";

    /// <summary>Newest collection time across all collectors for one server. $1 server_id.</summary>
    public const string ServerSummaryLastCollectionSql = @"
SELECT MAX(collection_time)
FROM v_collection_log
WHERE server_id = $1";

    /// <summary>
    /// One server's Overview-card summary — Lite's <c>GetServerSummaryAsync</c> ported to Postgres. The
    /// caller sets <see cref="ServerSummaryItem.ServerName"/> and applies the freshness-derived status
    /// (<see cref="ServerSummaryItem.ApplyFreshness"/>) after the read, exactly where Lite set IsOnline
    /// from the live ping. Blocking / deadlock counts use a one-hour window (Lite's window).
    /// </summary>
    public async Task<ServerSummaryItem> GetServerSummaryAsync(int serverId, string displayName, CancellationToken cancellationToken = default)
    {
        var windowStart = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-1), DateTimeKind.Unspecified);

        double? cpuPercent = null;
        double? otherProcessCpuPercent = null;
        double? memoryMb = null;
        var blockingCount = 0;
        var deadlockCount = 0;
        DateTime? lastCollection = null;

        /* Latest CPU — SQL and other-process, so the card can show total non-idle CPU with the SQL-only
           number alongside (Lite's headline). */
        await using (var command = _dataSource.CreateCommand(ServerSummaryCpuSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                cpuPercent = reader.IsDBNull(0) ? null : Convert.ToDouble(reader.GetValue(0));
                otherProcessCpuPercent = reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1));
            }
        }

        /* Latest total server memory. */
        await using (var command = _dataSource.CreateCommand(ServerSummaryMemorySql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null && result != DBNull.Value)
            {
                memoryMb = Convert.ToDouble(result);
            }
        }

        /* Blocking count in the last hour (XE, DMV fallback). */
        await using (var command = _dataSource.CreateCommand(ServerSummaryBlockingSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = windowStart });
            var result = await command.ExecuteScalarAsync(cancellationToken);
            blockingCount = result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        /* Deadlock count in the last hour. */
        await using (var command = _dataSource.CreateCommand(ServerSummaryDeadlockSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = windowStart });
            var result = await command.ExecuteScalarAsync(cancellationToken);
            deadlockCount = result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        /* Newest collection time across all collectors — drives the freshness status. */
        await using (var command = _dataSource.CreateCommand(ServerSummaryLastCollectionSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null && result != DBNull.Value)
            {
                lastCollection = Convert.ToDateTime(result);
            }
        }

        return new ServerSummaryItem
        {
            DisplayName = displayName,
            ServerId = serverId,
            CpuPercent = cpuPercent,
            OtherProcessCpuPercent = otherProcessCpuPercent,
            MemoryMb = memoryMb,
            BlockingCount = blockingCount,
            DeadlockCount = deadlockCount,
            LastCollectionTime = lastCollection,
        };
    }
}

/// <summary>The three collection-freshness bands the viewer derives a card's status from.</summary>
public enum ServerFreshness
{
    /// <summary>The newest collection is within twice the fastest collector's cadence — Online (green).</summary>
    Fresh,

    /// <summary>Collection has lagged past twice the cadence but the server isn't long-dead — Warning (amber).</summary>
    Stale,

    /// <summary>No collection at all, or the newest is long-dead — the Offline overlay (red).</summary>
    Offline,
}

/// <summary>
/// One Overview server card's view-model — copied from Lite's <c>ServerSummaryItem</c>
/// (Lite/Services/LocalDataService.Overview.cs) with two viewer adaptations, both from the headless
/// plan (#1262): <see cref="CpuPercentForAlert"/> is always total non-idle CPU (the viewer has no
/// per-app <c>CpuAlertMode</c> preference — total is what the alert engine evaluates by default), and
/// the status is derived from collection freshness rather than a live ping (see
/// <see cref="ClassifyFreshness"/> / <see cref="ApplyFreshness"/>, which set Lite's own
/// (<see cref="IsOnline"/>, <see cref="HasCollectorErrors"/>) inputs). Every display property and brush
/// is otherwise Lite's verbatim.
/// </summary>
public sealed class ServerSummaryItem
{
    /// <summary>
    /// The fastest scheduled collector's cadence (wait_stats / cpu_utilization / memory_stats etc. all
    /// run every minute — see <c>CollectorScheduleDefaults</c>), so MAX(collection_time) tracks a
    /// one-minute rhythm on a healthy server. Freshness bands are multiples of this.
    /// </summary>
    private static readonly TimeSpan s_collectorCadence = TimeSpan.FromMinutes(1);

    /// <summary>Older than twice the cadence = the collection has visibly lagged (Warning).</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromTicks(s_collectorCadence.Ticks * 2);

    /// <summary>Older than this (or no collection at all) = the server is treated as Offline.</summary>
    public static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(15);

    public string DisplayName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public int ServerId { get; set; }
    public bool? IsOnline { get; set; }

    /// <summary>Warning (amber) state — in the viewer this means the collection has gone stale.</summary>
    public bool HasCollectorErrors { get; set; }

    /// <summary>SQL Server scheduler ProcessUtilization from sys.dm_os_ring_buffers. NULL on Azure SQL DB.</summary>
    public double? CpuPercent { get; set; }

    /// <summary>Non-SQL-Server CPU on the host (100 - SystemIdle - ProcessUtilization). NULL on Azure SQL DB.</summary>
    public double? OtherProcessCpuPercent { get; set; }

    /// <summary>Total non-idle CPU on the host = sql_server + other_process. Tracks OS user+system counters.</summary>
    public double? TotalCpuPercent =>
        CpuPercent.HasValue ? CpuPercent.Value + (OtherProcessCpuPercent ?? 0) : null;

    /// <summary>
    /// The CPU value the headline display / colour band uses. The viewer has no per-app CpuAlertMode, so
    /// it always uses total non-idle CPU (falling back to SQL-only when other-process is unavailable) —
    /// what the alert engine evaluates by default.
    /// </summary>
    public double? CpuPercentForAlert => TotalCpuPercent ?? CpuPercent;

    public double? MemoryMb { get; set; }
    public int BlockingCount { get; set; }
    public int DeadlockCount { get; set; }
    public DateTime? LastCollectionTime { get; set; }

    /// <summary>
    /// Headline CPU display: total non-idle CPU prominently with the SQL-only number alongside, e.g.
    /// "64% (SQL 60%)". Falls back to a single number when only one value is available.
    /// </summary>
    public string CpuDisplay
    {
        get
        {
            if (!CpuPercent.HasValue) return "--";
            if (!OtherProcessCpuPercent.HasValue) return $"{CpuPercent:F0}%";
            return $"{TotalCpuPercent:F0}% (SQL {CpuPercent:F0}%)";
        }
    }

    public string MemoryDisplay => MemoryMb.HasValue ? $"{MemoryMb / 1024.0:F1} GB" : "--";
    public string BlockingDisplay => BlockingCount > 0 ? BlockingCount.ToString() : "0";
    public string DeadlockDisplay => DeadlockCount > 0 ? DeadlockCount.ToString() : "0";

    /// <summary>
    /// The stored collection_time is naive UTC; the viewer shows it in the viewer machine's local time
    /// (the viewer convention — Lite used its per-server offset helper instead).
    /// </summary>
    public string LastCollectionDisplay => LastCollectionTime.HasValue
        ? ViewerTimeHelper.ForDisplay(LastCollectionTime.Value).ToString("HH:mm:ss")
        : "Never";

    /* Connection status — verbatim from Lite; in the viewer the inputs come from ApplyFreshness. */
    public string StatusDisplay => IsOnline switch
    {
        true when HasCollectorErrors => "Warning",
        true => "Online",
        false => "Offline",
        _ => "Unknown"
    };

    public SolidColorBrush StatusBrush => MakeBrush(IsOnline switch
    {
        true when HasCollectorErrors => "#FFD54F",  // amber — stale collection
        true => "#81C784",
        false => "#E57373",
        _ => "#888888"
    });

    public bool IsOffline => IsOnline == false;

    /* Color coding — verbatim from Lite. */
    public SolidColorBrush CpuBrush
    {
        get
        {
            var v = CpuPercentForAlert;
            return MakeBrush(v >= 80 ? "#E57373" : v >= 50 ? "#FFB74D" : "#81C784");
        }
    }

    public SolidColorBrush BlockingBrush => MakeBrush(BlockingCount > 0 ? "#FFB74D" : "#81C784");
    public SolidColorBrush DeadlockBrush => MakeBrush(DeadlockCount > 0 ? "#E57373" : "#81C784");
    public SolidColorBrush CardBorderBrush => MakeBrush(
        IsOnline == false ? "#E57373" :
        DeadlockCount > 0 ? "#E57373" :
        BlockingCount > 0 ? "#FFB74D" :
        CpuPercentForAlert >= 80 ? "#FFB74D" :
        HasCollectorErrors ? "#FFD54F" :   // amber border when the collection is stale
        "#2a2d35");

    public bool HasAlerts => BlockingCount > 0 || DeadlockCount > 0;

    /// <summary>
    /// The viewer's status derivation (#1262): classify how fresh the newest collection is. Pure over
    /// (last-collection, now) so it can be pinned without a store. Both instants are UTC (the store is
    /// naive UTC; <paramref name="nowUtc"/> is <see cref="DateTime.UtcNow"/>), so the subtraction is a
    /// true elapsed-time regardless of Kind.
    /// </summary>
    public static ServerFreshness ClassifyFreshness(DateTime? lastCollectionUtc, DateTime nowUtc)
    {
        if (!lastCollectionUtc.HasValue) return ServerFreshness.Offline;

        var age = nowUtc - lastCollectionUtc.Value;
        if (age > OfflineThreshold) return ServerFreshness.Offline;
        if (age > StaleThreshold) return ServerFreshness.Stale;
        return ServerFreshness.Fresh;
    }

    /// <summary>
    /// Maps the freshness band onto Lite's card inputs, taking the live-ping's place: Fresh → Online,
    /// Stale → the amber Warning state, Offline → the red Offline overlay.
    /// </summary>
    public void ApplyFreshness(DateTime nowUtc)
    {
        var freshness = ClassifyFreshness(LastCollectionTime, nowUtc);
        IsOnline = freshness != ServerFreshness.Offline;
        HasCollectorErrors = freshness == ServerFreshness.Stale;
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
