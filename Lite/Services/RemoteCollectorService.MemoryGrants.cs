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
    /// Collects memory grant statistics from sys.dm_exec_query_resource_semaphores via the
    /// shared <see cref="MemoryGrantsCollector"/> definition (query text, composite delta key,
    /// and payload order live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectMemoryGrantStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(MemoryGrantsCollector.Instance, server, cancellationToken);
}
