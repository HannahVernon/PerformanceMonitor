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
    /// Collects server memory statistics via the shared <see cref="MemoryStatsCollector"/>
    /// definition (on-prem vs. Azure SQL DB variants, incl. the #857 elastic-pool schedulers
    /// skip, live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectMemoryStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(MemoryStatsCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Collects top memory clerks via the shared <see cref="MemoryClerksCollector"/> definition.
    /// </summary>
    private Task<int> CollectMemoryClerksAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(MemoryClerksCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Collects memory pressure notifications via the shared
    /// <see cref="MemoryPressureEventsCollector"/> definition (not applicable on Azure SQL DB —
    /// the definition's AppliesTo skips the cycle; watermark dedup lives there too).
    /// </summary>
    private Task<int> CollectMemoryPressureEventsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(MemoryPressureEventsCollector.Instance, server, cancellationToken);
}
