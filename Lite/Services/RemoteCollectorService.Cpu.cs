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
    /// Collects CPU utilization via the shared <see cref="CpuUtilizationCollector"/> definition
    /// (ring buffer on-prem/MI/RDS, sys.dm_db_resource_stats on Azure SQL DB; the Linux
    /// NULL-other-CPU rule (#1048), the incomplete-SystemHealth skip (#989), and the
    /// watermark/dedup split live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectCpuUtilizationAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(CpuUtilizationCollector.Instance, server, cancellationToken);
}
