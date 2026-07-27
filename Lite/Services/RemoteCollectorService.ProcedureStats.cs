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
    /// Collects procedure/trigger/function statistics via the shared
    /// <see cref="ProcedureStatsCollector"/> definition (the dynamic-SQL spills splice, the
    /// double-escaped literal exclusion filter, the Azure single-database variant, and the
    /// plan_handle-keyed delta groups live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectProcedureStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(ProcedureStatsCollector.Instance, server, cancellationToken);
}
