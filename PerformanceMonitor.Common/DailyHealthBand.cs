/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PerformanceMonitor.Common
{
    /// <summary>
    /// The composite health tier for a single day, used to color a Performance Calendar day cell at a
    /// glance. This is deliberately a <b>composite</b> band (folding deadlocks, collection failures, CPU,
    /// blocking, memory pressure and alerts into one verdict) rather than any single metric, so a month of
    /// cells reads as green / amber / red days.
    /// </summary>
    public enum DailyHealthBand
    {
        /// <summary>No collection happened that day — rendered neutral grey (the muted theme tier).</summary>
        NoData = 0,

        /// <summary>Collected, nothing elevated — green.</summary>
        Healthy = 1,

        /// <summary>Elevated but not critical (moderate CPU, some blocking, memory pressure, or alerts) — amber.</summary>
        Warning = 2,

        /// <summary>A serious day (deadlocks, collection failures, sustained high CPU, heavy blocking, or
        /// severe memory pressure) — red.</summary>
        Critical = 3,
    }

    /// <summary>
    /// The per-day signal counts that feed <see cref="DailyHealthBandCalculator.Classify"/>. Every field is
    /// a whole-day roll-up over the same source views the Daily Summary already aggregates. A day with no
    /// collection at all sets <see cref="HasData"/> = false (all other fields ignored → <see cref="DailyHealthBand.NoData"/>).
    /// </summary>
    public readonly record struct DailyHealthSignals
    {
        /// <summary>True when any collection ran that day. When false the band is always <see cref="DailyHealthBand.NoData"/>.</summary>
        public bool HasData { get; init; }

        /// <summary>Deadlocks captured that day. Any (&gt; 0) is Critical.</summary>
        public long Deadlocks { get; init; }

        /// <summary>Collector runs that ended in ERROR that day. Any (&gt; 0) is Critical — a monitoring blind spot is itself serious.</summary>
        public long CollectionErrors { get; init; }

        /// <summary>CPU samples where total host CPU (SQL + other-process) was ≥ 80%. Sustained (see
        /// <see cref="DailyHealthThresholds.HighCpuCriticalSamples"/>) is Critical; a few is Warning.</summary>
        public long HighCpuEvents { get; init; }

        /// <summary>Blocking events that day (blocked-process reports, falling back to DMV blocking snapshots).
        /// Past <see cref="DailyHealthThresholds.BlockingCriticalEvents"/> is Critical; some is Warning.</summary>
        public long BlockingEvents { get; init; }

        /// <summary>Memory-pressure events (a process or system indicator ≥ 2) that were not severe. Any is Warning.</summary>
        public long MemoryPressureEvents { get; init; }

        /// <summary>Severe memory-pressure events (a process indicator ≥ 3) that day. Any (&gt; 0) is Critical.</summary>
        public long MemoryCriticalEvents { get; init; }

        /// <summary>Actionable (non-resolution) alerts raised that day. At/over
        /// <see cref="DailyHealthThresholds.AlertWarningCount"/> is Warning.</summary>
        public long AlertCount { get; init; }
    }

    /// <summary>
    /// The one, documented, tunable place for the Performance Calendar's day-banding thresholds. If a month
    /// of cells reads too red or too green in practice, adjust these — nothing else needs to change. The
    /// defaults are chosen to be continuous with the Daily Summary's long-standing single-day banding
    /// (high-CPU &gt; 5 and blocking &gt; 10 were the original Critical triggers).
    /// </summary>
    public sealed record DailyHealthThresholds
    {
        /// <summary>High-CPU samples (≥ 80% total host) at or above which the day is Critical ("sustained high CPU").
        /// Default 6 preserves the original "high_cpu_events &gt; 5" Critical rule.</summary>
        public int HighCpuCriticalSamples { get; init; } = 6;

        /// <summary>High-CPU samples at or above which the day is at least Warning ("moderate CPU"). Default 1:
        /// any 80%+ total-host-CPU moment is worth a second look, short of the sustained (Critical) count.</summary>
        public int HighCpuWarningSamples { get; init; } = 1;

        /// <summary>Blocking events at or above which the day is Critical. Default 11 preserves the original
        /// "blocking_events &gt; 10" Critical rule.</summary>
        public int BlockingCriticalEvents { get; init; } = 11;

        /// <summary>Blocking events at or above which the day is at least Warning ("some blocking"). Default 1.</summary>
        public int BlockingWarningEvents { get; init; } = 1;

        /// <summary>Actionable alerts at or above which the day is at least Warning. Default 1 (any alert).</summary>
        public int AlertWarningCount { get; init; } = 1;

        /// <summary>The shipped defaults. Use this everywhere unless a caller has an explicit reason to override.</summary>
        public static DailyHealthThresholds Default { get; } = new();
    }

    /// <summary>
    /// The single, app-agnostic source of truth for the Performance Calendar's per-day composite health band.
    /// Both apps (Lite over DuckDB, the Darling viewer over Postgres) roll each day's signals into a
    /// <see cref="DailyHealthSignals"/> and call <see cref="Classify"/>, so the month heatmap bands identically
    /// no matter which store fed it — replacing the previously twinned-and-drifted single-day "OverallHealth"
    /// string logic (Lite's inline copy lacked the memory-critical escalation the viewer had).
    /// </summary>
    public static class DailyHealthBandCalculator
    {
        /// <summary>
        /// Folds a day's signals into a single <see cref="DailyHealthBand"/>. Severity is first-match-wins:
        /// no-data → critical checks → warning checks → healthy.
        /// </summary>
        /// <param name="signals">The day's rolled-up signal counts.</param>
        /// <param name="thresholds">Banding thresholds; <see cref="DailyHealthThresholds.Default"/> when null.</param>
        public static DailyHealthBand Classify(in DailyHealthSignals signals, DailyHealthThresholds? thresholds = null)
        {
            if (!signals.HasData)
                return DailyHealthBand.NoData;

            var t = thresholds ?? DailyHealthThresholds.Default;

            // Critical: anything that makes the day genuinely serious. Deadlocks, a monitoring gap
            // (collection errors), severe memory pressure, sustained high CPU, or heavy blocking.
            if (signals.Deadlocks > 0
                || signals.CollectionErrors > 0
                || signals.MemoryCriticalEvents > 0
                || signals.HighCpuEvents >= t.HighCpuCriticalSamples
                || signals.BlockingEvents >= t.BlockingCriticalEvents)
            {
                return DailyHealthBand.Critical;
            }

            // Warning: elevated but not critical — moderate CPU, some blocking, (non-severe) memory
            // pressure, or any actionable alert fired that day.
            if (signals.HighCpuEvents >= t.HighCpuWarningSamples
                || signals.BlockingEvents >= t.BlockingWarningEvents
                || signals.MemoryPressureEvents > 0
                || signals.AlertCount >= t.AlertWarningCount)
            {
                return DailyHealthBand.Warning;
            }

            return DailyHealthBand.Healthy;
        }

        /// <summary>A short human label for the band ("No Data" / "Healthy" / "Warning" / "Critical").</summary>
        public static string Label(DailyHealthBand band) => band switch
        {
            DailyHealthBand.Healthy => "Healthy",
            DailyHealthBand.Warning => "Warning",
            DailyHealthBand.Critical => "Critical",
            _ => "No Data",
        };

        /// <summary>
        /// The theme resource key for the band's fill brush. Resolved by each app's theme via
        /// <c>DynamicResource</c> — the shared calendar control has no theme of its own. The muted tier
        /// (<c>ForegroundMutedBrush</c>) is the neutral grey used for days with no collection.
        /// </summary>
        public static string BrushKey(DailyHealthBand band) => band switch
        {
            DailyHealthBand.Healthy => "SuccessBrush",
            DailyHealthBand.Warning => "WarningBrush",
            DailyHealthBand.Critical => "ErrorBrush",
            _ => "ForegroundMutedBrush",
        };

        /// <summary>
        /// Builds a multi-line tooltip summarizing the day's signals — one line per non-zero signal, or a
        /// terse "No data collected." / "No issues detected." when appropriate. Suitable for a cell ToolTip.
        /// </summary>
        public static string Describe(in DailyHealthSignals signals)
        {
            if (!signals.HasData)
                return "No data collected.";

            var lines = new List<string>();
            AppendCount(lines, signals.Deadlocks, "deadlock", "deadlocks");
            AppendCount(lines, signals.CollectionErrors, "collection error", "collection errors");
            AppendCount(lines, signals.HighCpuEvents, "high-CPU sample", "high-CPU samples");
            AppendCount(lines, signals.BlockingEvents, "blocking event", "blocking events");
            AppendCount(lines, signals.MemoryCriticalEvents, "severe memory-pressure event", "severe memory-pressure events");
            AppendCount(lines, signals.MemoryPressureEvents, "memory-pressure event", "memory-pressure events");
            AppendCount(lines, signals.AlertCount, "alert", "alerts");

            return lines.Count == 0 ? "No issues detected." : string.Join(Environment.NewLine, lines);
        }

        private static void AppendCount(List<string> lines, long count, string singular, string plural)
        {
            if (count <= 0)
                return;

            var noun = count == 1 ? singular : plural;
            lines.Add(count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun);
        }
    }
}
