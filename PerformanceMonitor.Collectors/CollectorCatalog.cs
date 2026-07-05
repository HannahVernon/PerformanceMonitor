/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Every collector definition in the library, as the engine-neutral schema surface. This is the
/// single enumeration storage hosts build from — Darling generates its full Postgres schema by
/// walking this list. A new definition MUST be added here (the catalog test pins the count).
/// </summary>
public static class CollectorCatalog
{
    public static IReadOnlyList<ICollectorSchemaInfo> All { get; } = new ICollectorSchemaInfo[]
    {
        WaitStatsCollector.Instance,
        LatchStatsCollector.Instance,
        SpinlockStatsCollector.Instance,
        CpuSchedulerStatsCollector.Instance,
        PlanCacheStatsCollector.Instance,
        TempDbStatsCollector.Instance,
        MemoryGrantsCollector.Instance,
        CpuUtilizationCollector.Instance,
        MemoryStatsCollector.Instance,
        MemoryClerksCollector.Instance,
        MemoryPressureEventsCollector.Instance,
        FileIoStatsCollector.Instance,
        ServerPropertiesCollector.Instance,
        ServerConfigCollector.Instance,
        DatabaseConfigCollector.Instance,
        TraceFlagsCollector.Instance,
        DatabaseScopedConfigCollector.Instance,
        SessionStatsCollector.Instance,
        SessionSummaryStatsCollector.Instance,
        WaitingTasksCollector.Instance,
        ProcedureStatsCollector.Instance,
        RunningJobsCollector.Instance,
        PerfmonStatsCollector.Instance,
        DmvBlockingSnapshotCollector.Instance,
        DatabaseSizeStatsCollector.Instance,
        IndexObjectStatsCollector.Instance,
        QueryStatsCollector.Instance,
        QuerySnapshotsCollector.Instance,
        QueryStoreCollector.Instance,
        DeadlocksCollector.Instance,
        BlockedProcessReportCollector.Instance,
        SystemHealthEventsCollector.Instance,
    };
}
