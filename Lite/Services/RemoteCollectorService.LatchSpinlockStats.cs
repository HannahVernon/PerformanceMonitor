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
    /// Collects latch statistics from sys.dm_os_latch_stats via the shared
    /// <see cref="LatchStatsCollector"/> definition (query text, delta rules, and payload order
    /// live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectLatchStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(LatchStatsCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Collects spinlock statistics from sys.dm_os_spinlock_stats via the shared
    /// <see cref="SpinlockStatsCollector"/> definition (query text, delta rules, and payload order
    /// live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectSpinlockStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(SpinlockStatsCollector.Instance, server, cancellationToken);
}
