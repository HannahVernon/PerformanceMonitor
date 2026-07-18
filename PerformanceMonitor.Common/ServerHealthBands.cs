/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;

namespace PerformanceMonitor.Common
{
    /// <summary>
    /// The collection-freshness bands the headless surfaces derive a server's connection status from — the
    /// viewer has no live ping to a monitored server, so "is this server reporting" is answered by how old the
    /// newest collection is (see <see cref="ServerHealthClassifier.ClassifyFreshness"/>).
    /// </summary>
    public enum ServerFreshness
    {
        /// <summary>The newest collection is within twice the fastest collector's cadence — Online (green).</summary>
        Fresh,

        /// <summary>Collection has lagged past twice the cadence but the server isn't long-dead — Warning (amber).</summary>
        Stale,

        /// <summary>The newest collection is long-dead — the Offline overlay (red).</summary>
        Offline,

        /// <summary>
        /// No collection has EVER landed for this server (this run or any prior) — the service has not reached it
        /// yet. Distinct from <see cref="Offline"/> (which means data STOPPED): during a slow fleet bootstrap a
        /// red "Offline" on a server that was merely still queued sent a 24-server field report chasing a phantom
        /// scheduler bug. Rendered as an amber "Awaiting first collection", never the red overlay.
        /// </summary>
        NeverCollected,
    }

    /// <summary>
    /// Per-metric health bands for an Overview card's severity dots — a verbatim mirror of the Dashboard's
    /// <c>HealthSeverity</c>. <see cref="Unknown"/> is a metric with no collected data (e.g. Threads on Azure SQL
    /// DB) — it never escalates the card's overall band.
    /// </summary>
    public enum HealthSeverity
    {
        Unknown,
        Healthy,
        Warning,
        Critical,
    }

    /// <summary>
    /// A server's fleet-health band — the SAME banding an Overview card computes, collapsed to one label per
    /// server (offline / critical → red, warning / stale → amber, else calm).
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
    /// The one, documented place for the per-server health thresholds shared by the Darling web dashboard, the
    /// get_fleet_overview MCP tool, and the WPF viewer's Overview cards. Freshness thresholds and the per-metric
    /// severity cutoffs used to live twice (WPF <c>ServerSummaryItem</c> vs the service-side reads) at numerically
    /// equal but independently-editable values — a drift risk. They now live here once; each host maps a band to
    /// its own brush/color, but the numbers are read from this single source (#1562).
    /// </summary>
    public static class ServerHealthThresholds
    {
        /// <summary>
        /// The fastest scheduled collector's cadence (wait_stats / cpu_utilization / memory_stats etc. all run
        /// every minute), so MAX(collection_time) tracks a one-minute rhythm on a healthy server. Freshness bands
        /// are multiples of this.
        /// </summary>
        public static readonly TimeSpan CollectorCadence = TimeSpan.FromMinutes(1);

        /// <summary>Older than twice the cadence = the collection has visibly lagged (Warning).</summary>
        public static readonly TimeSpan StaleThreshold = TimeSpan.FromTicks(CollectorCadence.Ticks * 2);

        /// <summary>Older than this (or no collection at all) = the server is treated as Offline.</summary>
        public static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(15);
    }

    /// <summary>
    /// The raw per-metric inputs a server card bands on — no brushes, no store, no display strings. Every field
    /// is a value the collectors already produced; <see cref="ServerHealthClassifier"/> reduces them to the six
    /// per-metric bands, the card's overall band, and the fleet score. Both the service-side cross-server reader
    /// and the WPF viewer's <c>ServerSummaryItem</c> build this and hand it to the classifier, so the thresholds
    /// live in exactly one place.
    /// </summary>
    public readonly record struct ServerHealthMetrics
    {
        /// <summary>Total non-idle CPU the CPU band evaluates (SQL + other-process), or null with no snapshot.</summary>
        public double? CpuPercentForAlert { get; init; }

        /// <summary>True when the resource semaphore shows grant waiters, timeouts, or forced grants.</summary>
        public bool HasMemoryPressure { get; init; }

        /// <summary>Blocking events in the window.</summary>
        public int BlockingCount { get; init; }

        /// <summary>The worst blocking wait in the window, in seconds.</summary>
        public double MaxBlockedSeconds { get; init; }

        /// <summary>Deadlocks in the window.</summary>
        public int DeadlockCount { get; init; }

        /// <summary>Worker-thread ceiling (max_workers_count), or null with no scheduler snapshot (e.g. Azure SQL DB).</summary>
        public int? TotalThreads { get; init; }

        /// <summary>Available worker threads = ceiling - in-use, or null with no scheduler snapshot.</summary>
        public int? AvailableThreads { get; init; }

        /// <summary>Runnable tasks waiting for a CPU (total_runnable_tasks_count).</summary>
        public int ThreadsWaitingForCpu { get; init; }

        /// <summary>Requests starved of a worker thread (total_work_queue_count).</summary>
        public long RequestsWaitingForThreads { get; init; }

        /// <summary>Collectors whose 7-day band is FAILING (no success in over 24h).</summary>
        public int FailedCollectorCount { get; init; }
    }

    /// <summary>
    /// The single, app-agnostic source of truth for a server's per-metric health bands, its overall card band,
    /// the collection-freshness band, and the fleet-ranking score. Reproduces the Dashboard's <c>ServerHealthStatus</c>
    /// CASE logic exactly. Pure + static so every host (web, MCP tool, WPF viewer) bands identically and the whole
    /// decision table is unit-testable without a store (#1562).
    /// </summary>
    public static class ServerHealthClassifier
    {
        /// <summary>
        /// Classify how fresh the newest collection is. Pure over (last-collection, now). Both instants are UTC
        /// (the store is naive UTC; <paramref name="nowUtc"/> is <see cref="DateTime.UtcNow"/>), so the
        /// subtraction is a true elapsed-time regardless of Kind.
        /// </summary>
        public static ServerFreshness ClassifyFreshness(DateTime? lastCollectionUtc, DateTime nowUtc)
        {
            if (!lastCollectionUtc.HasValue)
            {
                return ServerFreshness.NeverCollected;
            }

            var age = nowUtc - lastCollectionUtc.Value;
            if (age > ServerHealthThresholds.OfflineThreshold)
            {
                return ServerFreshness.Offline;
            }

            if (age > ServerHealthThresholds.StaleThreshold)
            {
                return ServerFreshness.Stale;
            }

            return ServerFreshness.Fresh;
        }

        /// <summary>CPU band on total non-idle CPU: >= 95% Critical, >= 80% Warning; no snapshot Unknown.</summary>
        public static HealthSeverity CpuSeverity(double? cpuPercentForAlert)
        {
            if (!cpuPercentForAlert.HasValue)
            {
                return HealthSeverity.Unknown;
            }

            if (cpuPercentForAlert >= 95)
            {
                return HealthSeverity.Critical;
            }

            if (cpuPercentForAlert >= 80)
            {
                return HealthSeverity.Warning;
            }

            return HealthSeverity.Healthy;
        }

        /// <summary>Memory band — Critical on any resource-semaphore pressure, else Healthy.</summary>
        public static HealthSeverity MemorySeverity(bool hasMemoryPressure) =>
            hasMemoryPressure ? HealthSeverity.Critical : HealthSeverity.Healthy;

        /// <summary>Blocking band: >= 60s max wait or >= 5 events Critical; >= 10s max wait, >= 2 events, or any blocking Warning.</summary>
        public static HealthSeverity BlockingSeverity(int blockingCount, double maxBlockedSeconds)
        {
            if (maxBlockedSeconds >= 60)
            {
                return HealthSeverity.Critical;
            }

            if (blockingCount >= 5)
            {
                return HealthSeverity.Critical;
            }

            if (maxBlockedSeconds >= 10)
            {
                return HealthSeverity.Warning;
            }

            if (blockingCount >= 2)
            {
                return HealthSeverity.Warning;
            }

            if (blockingCount > 0)
            {
                return HealthSeverity.Warning;
            }

            return HealthSeverity.Healthy;
        }

        /// <summary>Deadlock band — any deadlock in the window is Critical.</summary>
        public static HealthSeverity DeadlockSeverity(int deadlockCount) =>
            deadlockCount > 0 ? HealthSeverity.Critical : HealthSeverity.Healthy;

        /// <summary>
        /// Threads band: work-queue starvation Critical; >= 20 runnable-waiting or under 10% workers available
        /// Warning. Unknown when there is no scheduler snapshot.
        /// </summary>
        public static HealthSeverity ThreadsSeverity(int? totalThreads, int? availableThreads, int threadsWaitingForCpu, long requestsWaitingForThreads)
        {
            if (!totalThreads.HasValue)
            {
                return HealthSeverity.Unknown;
            }

            if (requestsWaitingForThreads > 0)
            {
                return HealthSeverity.Critical;
            }

            if (threadsWaitingForCpu >= 20)
            {
                return HealthSeverity.Warning;
            }

            if (totalThreads.Value > 0 && availableThreads < totalThreads.Value * 0.10)
            {
                return HealthSeverity.Warning;
            }

            return HealthSeverity.Healthy;
        }

        /// <summary>Collectors band — any FAILING collector is Warning.</summary>
        public static HealthSeverity CollectorSeverity(int failedCollectorCount) =>
            failedCollectorCount > 0 ? HealthSeverity.Warning : HealthSeverity.Healthy;

        /// <summary>The six per-metric card severities, in card row order — the reuse surface for scoring / reasons.</summary>
        public static IEnumerable<HealthSeverity> MetricSeverities(ServerHealthMetrics m)
        {
            yield return CpuSeverity(m.CpuPercentForAlert);
            yield return ThreadsSeverity(m.TotalThreads, m.AvailableThreads, m.ThreadsWaitingForCpu, m.RequestsWaitingForThreads);
            yield return MemorySeverity(m.HasMemoryPressure);
            yield return BlockingSeverity(m.BlockingCount, m.MaxBlockedSeconds);
            yield return DeadlockSeverity(m.DeadlockCount);
            yield return CollectorSeverity(m.FailedCollectorCount);
        }

        /// <summary>
        /// The card's worst metric band (offline handled separately by the border / overlay). Unknown and Healthy
        /// never escalate — matching <c>ServerHealthStatus.OverallSeverity</c>'s reduce.
        /// </summary>
        public static HealthSeverity OverallMetricSeverity(in ServerHealthMetrics m)
        {
            var worst = HealthSeverity.Healthy;
            foreach (var s in MetricSeverities(m))
            {
                if (s == HealthSeverity.Critical)
                {
                    return HealthSeverity.Critical;
                }

                if (s == HealthSeverity.Warning)
                {
                    worst = HealthSeverity.Warning;
                }
            }

            return worst;
        }

        /// <summary>
        /// Collapses a server's health to one fleet band, mirroring the card border: offline collection -> Offline;
        /// a never-collected (queued-during-bootstrap) server -> Warning (attention-worthy but not the red overlay);
        /// else the card's worst metric band, with a stale collection also Warning.
        /// </summary>
        public static FleetHealthBand ClassifyBand(bool? isOnline, bool awaitingFirstCollection, bool hasCollectorErrors, HealthSeverity overallMetricSeverity)
        {
            if (isOnline == false)
            {
                return FleetHealthBand.Offline;
            }

            if (awaitingFirstCollection)
            {
                return FleetHealthBand.Warning;
            }

            return overallMetricSeverity switch
            {
                HealthSeverity.Critical => FleetHealthBand.Critical,
                HealthSeverity.Warning => FleetHealthBand.Warning,
                _ => hasCollectorErrors ? FleetHealthBand.Warning : FleetHealthBand.Healthy,
            };
        }

        /// <summary>
        /// The worst-first ordering score. Band rank dominates (Offline &gt; Critical &gt; Warning &gt; Healthy) in
        /// steps of 1000; within a band, servers are ranked by how many of the six card metrics are Critical (x100)
        /// or Warning (x10), with the blocking + deadlock counts (capped at 99) as a final tiebreak. The within-band
        /// terms are bounded well under 1000, so they never reorder bands.
        /// </summary>
        public static long FleetHealthScore(FleetHealthBand band, in ServerHealthMetrics m)
        {
            long bandRank = band switch
            {
                FleetHealthBand.Offline => 4000,
                FleetHealthBand.Critical => 3000,
                FleetHealthBand.Warning => 2000,
                _ => 0,
            };

            var criticals = 0;
            var warnings = 0;
            foreach (var sev in MetricSeverities(m))
            {
                if (sev == HealthSeverity.Critical)
                {
                    criticals++;
                }
                else if (sev == HealthSeverity.Warning)
                {
                    warnings++;
                }
            }

            long magnitude = (criticals * 100L) + (warnings * 10L);
            long incidents = Math.Min(m.BlockingCount + m.DeadlockCount, 99);
            return bandRank + magnitude + incidents;
        }

        /// <summary>A short human label for a fleet band ("Healthy" / "Warning" / "Critical" / "Offline").</summary>
        public static string BandLabel(FleetHealthBand band) => band switch
        {
            FleetHealthBand.Critical => "Critical",
            FleetHealthBand.Warning => "Warning",
            FleetHealthBand.Offline => "Offline",
            _ => "Healthy",
        };
    }
}
