/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Collects the CPU scheduler / workload-group / NUMA / OS-memory snapshot via the shared
    /// <see cref="CpuSchedulerStatsCollector"/> definition (query text and payload order live there —
    /// the cross-SKU parity contract). Skips Azure SQL DB (definition's AppliesTo) because
    /// sys.dm_os_sys_memory is not exposed and sys.dm_exec_requests is connection-scoped there.
    /// </summary>
    private Task<int> CollectCpuSchedulerStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(CpuSchedulerStatsCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Collects the plan-cache composition snapshot from sys.dm_exec_cached_plans via the shared
    /// <see cref="PlanCacheStatsCollector"/> definition (query text and payload order live there —
    /// the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectPlanCacheStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(PlanCacheStatsCollector.Instance, server, cancellationToken);
}
