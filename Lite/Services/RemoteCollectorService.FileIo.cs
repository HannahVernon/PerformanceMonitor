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
    /// Collects file I/O statistics via the shared <see cref="FileIoStatsCollector"/> definition
    /// (on-prem server-wide vs. Azure per-database execution, the exclusion-filter splice, and
    /// the eight "{database}|{file}" delta groups live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectFileIoStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(FileIoStatsCollector.Instance, server, cancellationToken);
}
