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
    /// Collects Query Store data via the shared <see cref="QueryStoreCollector"/> definition
    /// (the actual_state enumeration probe, the live PRODUCTVERSION check deciding the
    /// 2017+/2022+ column gates, the last_execution_time incremental watermark, and the
    /// per-database sp_executesql query live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectQueryStoreAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(QueryStoreCollector.Instance, server, cancellationToken);
}
