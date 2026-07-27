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
    /// Collects per-table/per-index size, usage, and locking statistics via the shared
    /// <see cref="IndexObjectStatsCollector"/> definition (the #temp-staged sp_IndexCleanup
    /// technique, the per-database execution on both platforms, and the dedicated 300 s
    /// per-database budget from the #1135 fix live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectIndexObjectStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(IndexObjectStatsCollector.Instance, server, cancellationToken);
}
