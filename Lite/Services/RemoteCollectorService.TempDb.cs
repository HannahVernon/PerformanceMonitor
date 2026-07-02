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
    /// Collects TempDB space usage statistics via the shared <see cref="TempDbStatsCollector"/>
    /// definition (two result sets → one row; query text and payload order live there — the
    /// cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectTempDbStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(TempDbStatsCollector.Instance, server, cancellationToken);
}
