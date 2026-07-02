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
    /// Collects query statistics via the shared <see cref="QueryStatsCollector"/> definition
    /// (the plan_attributes database resolution, the Azure per-database variant, the full
    /// row-identity delta key that fixed multi-statement cross-contamination, and the
    /// interval-captured worker-time delta live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectQueryStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(QueryStatsCollector.Instance, server, cancellationToken);
}
