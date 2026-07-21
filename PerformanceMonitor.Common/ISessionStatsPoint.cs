/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Common;

/// <summary>
/// The host-neutral read surface of a single server-wide session-summary snapshot — EXACTLY the properties the
/// shared Session Stats chart renderer (the seven <c>SessionSeriesSpec</c> selectors + the
/// <c>CollectionTime</c> the trend orders and projects on) and the shared
/// <see cref="SessionStatsSummary.Format"/> summary-strip projection (top application / top host / distinct
/// databases) read. Both apps' snapshot types implement this so the (previously byte-identical) render + summary
/// live once: Lite's <c>SessionStatsPoint</c> class (DuckDB reads) and the Darling viewer's
/// <c>SessionStatsPoint</c> positional record (Postgres reads). Every member matches by name and type across
/// both snapshot declarations — no widening seam is needed (contrast <see cref="ICpuSchedulerSnapshot"/>).
/// </summary>
public interface ISessionStatsPoint
{
    /// <summary>Snapshot time (naive UTC); the trend orders oldest-first on it and projects it to display time.</summary>
    DateTime CollectionTime { get; }

    /* The seven chart-series status counts (the SessionSeriesSpec selectors, Total first). */
    int TotalSessions { get; }
    int RunningSessions { get; }
    int SleepingSessions { get; }
    int BackgroundSessions { get; }
    int DormantSessions { get; }
    int IdleSessionsOver30Min { get; }
    int SessionsWaitingForMemory { get; }

    /* The summary-strip attribution columns (SessionStatsSummary.Format). */
    int DatabasesWithConnections { get; }
    string? TopApplicationName { get; }
    int? TopApplicationConnections { get; }
    string? TopHostName { get; }
    int? TopHostConnections { get; }
}
