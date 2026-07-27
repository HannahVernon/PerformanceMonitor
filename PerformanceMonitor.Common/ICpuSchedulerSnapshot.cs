/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Common;

/// <summary>
/// The host-neutral read surface of a single <c>cpu_scheduler_stats</c> snapshot — EXACTLY the properties the
/// shared <see cref="CpuSchedulerMetrics.BuildMetrics"/> metric projection and
/// <see cref="CpuSchedulerMetrics.ClassifyCpuPressure"/> pressure banding read off the latest row. Both apps'
/// snapshot types implement this so that (previously byte-identical) projection lives once in Common: Lite's
/// <c>CpuSchedulerSnapshot</c> class (DuckDB reads) and the Darling viewer's <c>CpuSchedulerSnapshot</c>
/// positional record (Postgres reads). <c>collection_time</c> is deliberately absent — the projection never
/// reads it (the render method keeps its own per-app time handling).
///
/// The two averaged/percentage columns are surfaced here as <see cref="double"/> (the natural computational
/// type): Lite already stores them as <c>double</c>, so it satisfies these implicitly; the Darling record
/// stores them as Postgres-<c>NUMERIC</c> <c>decimal</c> and satisfies them with a tiny explicit-interface
/// widening cast. Everything else matches by name and type across both snapshot declarations.
/// </summary>
public interface ICpuSchedulerSnapshot
{
    int MaxWorkersCount { get; }
    int SchedulerCount { get; }
    int CpuCount { get; }
    int TotalRunnableTasksCount { get; }
    long TotalWorkQueueCount { get; }
    int TotalCurrentWorkersCount { get; }
    double AvgRunnableTasksCount { get; }
    int TotalActiveRequestCount { get; }
    int TotalQueuedRequestCount { get; }
    int TotalBlockedTaskCount { get; }
    long TotalActiveParallelThreadCount { get; }
    int? RunnableRequestCount { get; }
    int? TotalRequestCount { get; }
    double? RunnablePercent { get; }
    bool WorkerThreadExhaustionWarning { get; }
    bool RunnableTasksWarning { get; }
    bool BlockedTasksWarning { get; }
    bool QueuedRequestsWarning { get; }
    long TotalPhysicalMemoryKb { get; }
    long AvailablePhysicalMemoryKb { get; }
    string? SystemMemoryStateDesc { get; }
    bool PhysicalMemoryPressureWarning { get; }
    int TotalNodeCount { get; }
    int NodesOnlineCount { get; }
    int OfflineCpuCount { get; }
    bool OfflineCpuWarning { get; }
}
