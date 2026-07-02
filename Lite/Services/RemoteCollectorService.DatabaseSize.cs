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
    /// Collects per-file database sizes via the shared <see cref="DatabaseSizeStatsCollector"/>
    /// definition (the server-side cursor + FILEPROPERTY staging batch, the two-site exclusion
    /// splice, volume stats, and the Azure per-database variant live there — the cross-SKU
    /// parity contract).
    /// </summary>
    private Task<int> CollectDatabaseSizeStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(DatabaseSizeStatsCollector.Instance, server, cancellationToken);
}
