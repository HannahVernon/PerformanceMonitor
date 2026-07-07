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
using System.Windows.Media;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Fleet-wide NOC roll-up — the signature capability Darling's single central store enables that
/// NEITHER the Dashboard nor Lite can do. Each of those monitors one server per PerformanceMonitor
/// database, so they physically cannot rank or total servers against each other in SQL; Darling keys
/// every server's rows by <c>server_id</c> in ONE Postgres store, so a single cross-server GROUP-BY /
/// aggregate rolls the whole fleet up at once.
///
/// <para>This roll-up is deliberately split across two mechanisms so it can REUSE the per-server surface
/// rather than re-derive it:</para>
/// <list type="bullet">
/// <item><b>Fleet totals</b> (<see cref="GetFleetTotalsAsync"/> / <see cref="FleetTotalsSql"/>) are a
/// genuine single cross-server aggregate over the collector views — total blocking events and total
/// deadlocks in the window. Blocking honors the SAME per-server XE→DMV fallback the Overview cards use
/// (<c>ServerSummaryBlockingSql</c>): it counts per <c>server_id</c> from each source, takes the XE count
/// when a server has any XE row this window else its DMV count, then SUMs across the fleet — so the fleet
/// total reconciles with the sum of the per-server card counts. These are pure COUNT metrics with no C#
/// banding, so they belong in SQL.</item>
/// <item><b>Health-band counts, servers-with-collection-failures, and the worst-N ranking</b>
/// (<see cref="FleetRollup.Build"/>) are a pure C# reduction over the <see cref="ServerSummaryItem"/>
/// list the Overview already loads. They REUSE #1426's card banding verbatim: a server's fleet band comes
/// from <see cref="ServerSummaryItem.OverallMetricSeverity"/> + its freshness status (which themselves
/// reuse every per-metric band, including <see cref="CollectorHealthRow.HealthStatus"/> via the card's
/// FailedCollectorCount). Reproducing that six-metric composite in SQL would be inventing a parallel
/// banding, so the classification stays where the banding lives — in C#, over the already-read cards.</item>
/// </list>
///
/// <para>The totals read is windowed (parameterized, naive-UTC per the store convention). The Overview tab
/// has no per-tab toolbar; the caller passes the SAME one-hour window the per-server cards use, so the
/// fleet totals and the cards describe the same span.</para>
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>
    /// The cross-server fleet totals over a window — the read only Darling's central store can serve.
    /// <c>total_blocking_events</c> respects the Overview card's XE-preferred / DMV-fallback rule PER
    /// server before summing: each source is counted per <c>server_id</c> (a GROUP BY), a FULL OUTER JOIN
    /// lines the two sources up per server, and each server contributes its XE count when it has any XE row
    /// this window else its DMV count (Lite's <c>COALESCE(NULLIF(xe,0), dmv)</c>, applied per server) — so
    /// an AWS RDS server with only DMV snapshots still counts and a server with XE reports is never
    /// double-counted. <c>total_deadlocks</c> is a plain cross-server COUNT. $1 window start, $2 window end
    /// (both naive UTC).
    /// </summary>
    public const string FleetTotalsSql = @"
SELECT
    (
        SELECT COALESCE(SUM(CASE WHEN per_server.xe_count > 0 THEN per_server.xe_count ELSE per_server.dmv_count END), 0)
        FROM
        (
            SELECT
                COALESCE(xe.server_id, dmv.server_id) AS server_id,
                COALESCE(xe.cnt, 0) AS xe_count,
                COALESCE(dmv.cnt, 0) AS dmv_count
            FROM
            (
                SELECT server_id, COUNT(*) AS cnt
                FROM v_blocked_process_reports
                WHERE event_time >= $1
                AND   event_time <= $2
                GROUP BY server_id
            ) AS xe
            FULL OUTER JOIN
            (
                SELECT server_id, COUNT(*) AS cnt
                FROM v_dmv_blocking_snapshots
                WHERE event_time >= $1
                AND   event_time <= $2
                GROUP BY server_id
            ) AS dmv ON xe.server_id = dmv.server_id
        ) AS per_server
    ) AS total_blocking_events,
    (
        SELECT COUNT(*)
        FROM v_deadlocks
        WHERE deadlock_time >= $1
        AND   deadlock_time <= $2
    ) AS total_deadlocks";

    /// <summary>
    /// Reads the fleet's cross-server blocking + deadlock totals over the window. The single query that
    /// neither the Dashboard nor Lite can run (each sees one server's database). Window bounds are sent
    /// Kind=Unspecified (the naive-UTC store convention).
    /// </summary>
    public async Task<FleetTotals> GetFleetTotalsAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(FleetTotalsSql);
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified),
        });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified),
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new FleetTotals
            {
                TotalBlockingEvents = reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0)),
                TotalDeadlocks = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1)),
            };
        }

        return new FleetTotals();
    }
}

/// <summary>The cross-server fleet totals over a window — pure SQL aggregates keyed by <c>server_id</c>.</summary>
public sealed class FleetTotals
{
    /// <summary>Blocking events across the whole fleet this window (per-server XE→DMV fallback, then summed).</summary>
    public long TotalBlockingEvents { get; set; }

    /// <summary>Deadlocks across the whole fleet this window.</summary>
    public long TotalDeadlocks { get; set; }
}

/// <summary>
/// A server's fleet-health band — the SAME banding #1426's Overview cards compute, collapsed to one label
/// per server. <see cref="FleetRollup.ClassifyBand"/> derives it from the card's
/// <see cref="ServerSummaryItem.OverallMetricSeverity"/> + freshness, so it never invents a new band: it
/// mirrors <see cref="ServerSummaryItem.CardBorderBrush"/> (offline / critical → red, warning / stale →
/// amber, else calm).
/// </summary>
public enum FleetHealthBand
{
    Healthy,
    Warning,
    Critical,

    /// <summary>The server's collection is long-dead / never happened (the card's red offline overlay).</summary>
    Offline,
}

/// <summary>
/// One entry in the fleet's worst-first "Needs attention" ranking — a problem server with its band, a
/// short human reason (built from the card's own metric displays), and its composite score. Clicking the
/// entry opens that server's tab (the caller maps <see cref="ServerId"/> back to the sidebar server and
/// calls MainWindow's OpenServerTab). Every display is a pure format of the underlying
/// <see cref="ServerSummaryItem"/>; the brushes reuse the card's severity palette.
/// </summary>
public sealed class FleetRankedServer
{
    private static readonly SolidColorBrush s_criticalBrush = MakeBrush("#E57373");
    private static readonly SolidColorBrush s_warningBrush = MakeBrush("#FFD54F");
    private static readonly SolidColorBrush s_healthyBrush = MakeBrush("#81C784");
    private static readonly SolidColorBrush s_offlineBrush = MakeBrush("#888888");

    public int ServerId { get; init; }
    public string DisplayName { get; init; } = "";
    public FleetHealthBand Band { get; init; }

    /// <summary>The composite worst-first ordering score (band rank + severity magnitude + incident tiebreak).</summary>
    public long Score { get; init; }

    /// <summary>A short "why it needs attention" line, e.g. "CPU 97%, Blocking 6" or "Offline — no recent collection".</summary>
    public string Reason { get; init; } = "";

    public string BandLabel => Band switch
    {
        FleetHealthBand.Critical => "Critical",
        FleetHealthBand.Warning => "Warning",
        FleetHealthBand.Offline => "Offline",
        _ => "Healthy",
    };

    /// <summary>The band's dot / label brush — the card's severity palette (offline = the Unknown grey).</summary>
    public SolidColorBrush BandBrush => Band switch
    {
        FleetHealthBand.Critical => s_criticalBrush,
        FleetHealthBand.Warning => s_warningBrush,
        FleetHealthBand.Offline => s_offlineBrush,
        _ => s_healthyBrush,
    };

    private static SolidColorBrush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The fleet-wide roll-up view-model rendered above the Overview's per-server cards: the fleet health
/// summary (total servers + counts by band), the fleet totals (cross-server blocking / deadlock sums +
/// servers with collection failures), and the worst-first "Needs attention" ranking. Built by
/// <see cref="Build"/> from the same <see cref="ServerSummaryItem"/> list the Overview loads plus the
/// <see cref="FleetTotals"/> SQL read — REUSING #1426's card banding for every band decision.
/// </summary>
public sealed class FleetRollup
{
    /// <summary>The default depth of the worst-first ranking (the "Needs attention" list caps at this).</summary>
    public const int DefaultWorstCount = 5;

    /// <summary>An empty fleet (no servers registered) — every count zero, no ranking.</summary>
    public static readonly FleetRollup Empty = new();

    public int TotalServers { get; init; }
    public int HealthyCount { get; init; }
    public int WarningCount { get; init; }
    public int CriticalCount { get; init; }
    public int OfflineCount { get; init; }

    /// <summary>Servers whose 7-day collection banding flags at least one FAILING collector (card reuse).</summary>
    public int ServersWithCollectionFailures { get; init; }

    /// <summary>Fleet-wide blocking events this window (from the cross-server SQL total).</summary>
    public long TotalBlockingEvents { get; init; }

    /// <summary>Fleet-wide deadlocks this window (from the cross-server SQL total).</summary>
    public long TotalDeadlocks { get; init; }

    /// <summary>The worst-first problem servers (band != Healthy), capped at the requested depth.</summary>
    public IReadOnlyList<FleetRankedServer> WorstServers { get; init; } = Array.Empty<FleetRankedServer>();

    /// <summary>Problem servers beyond the capped ranking — surfaced as a "+N more" affordance.</summary>
    public int AdditionalProblemCount { get; init; }

    /// <summary>Any server needs attention (band != Healthy) — drives the ranking list vs the all-clear line.</summary>
    public bool HasProblems => WorstServers.Count > 0;

    /// <summary>"+N more need attention" when the ranking overflows its cap, else empty.</summary>
    public string AdditionalProblemText =>
        AdditionalProblemCount > 0 ? $"+{AdditionalProblemCount} more need attention" : "";

    /// <summary>The all-clear affirmation shown when nothing needs attention.</summary>
    public string AllHealthyText => TotalServers == 1
        ? "All 1 server healthy"
        : $"All {TotalServers} servers healthy";

    /// <summary>"Monitoring N servers" subtitle for the fleet header.</summary>
    public string MonitoringText => TotalServers == 1
        ? "Monitoring 1 server"
        : $"Monitoring {TotalServers} servers";

    /// <summary>"N server(s)" for the collection-failures total line.</summary>
    public string CollectionFailuresText => ServersWithCollectionFailures == 1
        ? "1 server"
        : $"{ServersWithCollectionFailures} servers";

    /// <summary>
    /// Rolls the per-server Overview cards up into the fleet view-model. The band counts,
    /// servers-with-failures, and worst-first ranking reduce the <paramref name="summaries"/> using #1426's
    /// card banding (no new bands); the blocking / deadlock totals come from the cross-server
    /// <paramref name="totals"/> SQL read. <paramref name="worstCount"/> caps the "Needs attention" list.
    /// </summary>
    public static FleetRollup Build(IReadOnlyList<ServerSummaryItem> summaries, FleetTotals totals, int worstCount = DefaultWorstCount)
    {
        if (summaries.Count == 0)
        {
            return new FleetRollup
            {
                TotalBlockingEvents = totals.TotalBlockingEvents,
                TotalDeadlocks = totals.TotalDeadlocks,
            };
        }

        var healthy = 0;
        var warning = 0;
        var critical = 0;
        var offline = 0;
        var failures = 0;

        foreach (var s in summaries)
        {
            switch (ClassifyBand(s))
            {
                case FleetHealthBand.Offline: offline++; break;
                case FleetHealthBand.Critical: critical++; break;
                case FleetHealthBand.Warning: warning++; break;
                default: healthy++; break;
            }

            /* Servers with collection failures — the card's own FAILING count (which reuses
               CollectorHealthRow.HealthStatus), not a re-derived SQL band. */
            if (s.FailedCollectorCount > 0)
            {
                failures++;
            }
        }

        /* Worst-first ranking: problem servers only (band != Healthy), highest composite score first,
           name as the stable tiebreak. The composite score keeps offline > critical > warning and, within
           a band, ranks by how many metrics are bad — see FleetHealthScore. */
        var problems = summaries
            .Where(s => ClassifyBand(s) != FleetHealthBand.Healthy)
            .OrderByDescending(FleetHealthScore)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var worst = problems
            .Take(worstCount)
            .Select(s => new FleetRankedServer
            {
                ServerId = s.ServerId,
                DisplayName = s.DisplayName,
                Band = ClassifyBand(s),
                Score = FleetHealthScore(s),
                Reason = BuildReason(s),
            })
            .ToList();

        return new FleetRollup
        {
            TotalServers = summaries.Count,
            HealthyCount = healthy,
            WarningCount = warning,
            CriticalCount = critical,
            OfflineCount = offline,
            ServersWithCollectionFailures = failures,
            TotalBlockingEvents = totals.TotalBlockingEvents,
            TotalDeadlocks = totals.TotalDeadlocks,
            WorstServers = worst,
            AdditionalProblemCount = Math.Max(0, problems.Count - worst.Count),
        };
    }

    /// <summary>
    /// Collapses a card's health to one fleet band — REUSING #1426's banding, mirroring
    /// <see cref="ServerSummaryItem.CardBorderBrush"/>: offline collection → Offline; else the card's
    /// worst metric band (<see cref="ServerSummaryItem.OverallMetricSeverity"/>) maps Critical → Critical,
    /// Warning → Warning, and a stale collection (<see cref="ServerSummaryItem.HasCollectorErrors"/>) is
    /// Warning too; otherwise Healthy. No new thresholds are introduced here.
    /// </summary>
    public static FleetHealthBand ClassifyBand(ServerSummaryItem s)
    {
        if (s.IsOnline == false)
        {
            return FleetHealthBand.Offline;
        }

        return s.OverallMetricSeverity switch
        {
            HealthSeverity.Critical => FleetHealthBand.Critical,
            HealthSeverity.Warning => FleetHealthBand.Warning,
            _ => s.HasCollectorErrors ? FleetHealthBand.Warning : FleetHealthBand.Healthy,
        };
    }

    /// <summary>
    /// The worst-first ordering score. Band rank dominates (Offline &gt; Critical &gt; Warning &gt;
    /// Healthy) in steps of 1000; within a band, servers are ranked by how many of the six card metrics
    /// are Critical (×100) or Warning (×10), with the blocking + deadlock counts (capped) as a final
    /// tiebreak. The within-band terms are bounded well under 1000, so they never reorder bands. Reuses the
    /// card's per-metric severities — no new signal.
    /// </summary>
    public static long FleetHealthScore(ServerSummaryItem s)
    {
        long bandRank = ClassifyBand(s) switch
        {
            FleetHealthBand.Offline => 4000,
            FleetHealthBand.Critical => 3000,
            FleetHealthBand.Warning => 2000,
            _ => 0,
        };

        var criticals = 0;
        var warnings = 0;
        foreach (var sev in MetricSeverities(s))
        {
            if (sev == HealthSeverity.Critical) criticals++;
            else if (sev == HealthSeverity.Warning) warnings++;
        }

        /* criticals + warnings <= 6, so magnitude <= 600; incidents <= 99 — the within-band total stays
           under 1000 and can never cross a band boundary. */
        long magnitude = (criticals * 100L) + (warnings * 10L);
        long incidents = Math.Min(s.BlockingCount + s.DeadlockCount, 99);
        return bandRank + magnitude + incidents;
    }

    /// <summary>The six per-metric card severities, in card row order — the reuse surface for scoring / reasons.</summary>
    private static IEnumerable<HealthSeverity> MetricSeverities(ServerSummaryItem s)
    {
        yield return s.CpuSeverity;
        yield return s.ThreadsSeverity;
        yield return s.MemorySeverity;
        yield return s.BlockingSeverity;
        yield return s.DeadlockSeverity;
        yield return s.CollectorSeverity;
    }

    /// <summary>
    /// A short human reason for the ranking, built from the card's OWN metric displays so it never drifts
    /// from what the card shows. Offline states say so; otherwise it names the metrics that are Warning or
    /// Critical (CPU / Blocking / Deadlocks / Memory / Threads / Collectors), plus a stale-collection note.
    /// </summary>
    public static string BuildReason(ServerSummaryItem s)
    {
        if (s.IsOnline == false)
        {
            return "Offline — no recent collection";
        }

        var parts = new List<string>();

        if (s.CpuSeverity >= HealthSeverity.Warning)
        {
            parts.Add($"CPU {s.CpuDisplay}");
        }
        if (s.ThreadsSeverity >= HealthSeverity.Warning)
        {
            parts.Add($"Threads {s.ThreadsDisplay}");
        }
        if (s.MemorySeverity >= HealthSeverity.Warning && s.HasMemoryPressure)
        {
            parts.Add($"Memory: {s.MemoryDetail}");
        }
        if (s.BlockingSeverity >= HealthSeverity.Warning && s.BlockingCount > 0)
        {
            parts.Add($"Blocking {s.BlockingCount}");
        }
        if (s.DeadlockSeverity >= HealthSeverity.Warning && s.DeadlockCount > 0)
        {
            parts.Add($"Deadlocks {s.DeadlockCount}");
        }
        if (s.CollectorSeverity >= HealthSeverity.Warning)
        {
            parts.Add($"{s.FailedCollectorCount} collector{(s.FailedCollectorCount == 1 ? "" : "s")} failing");
        }
        if (s.HasCollectorErrors)
        {
            parts.Add("collection stale");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "Needs attention";
    }
}
