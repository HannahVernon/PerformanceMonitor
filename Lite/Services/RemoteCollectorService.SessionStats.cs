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
    /// Collects per-application session statistics via the shared
    /// <see cref="SessionStatsCollector"/> definition.
    /// </summary>
    private Task<int> CollectSessionStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(SessionStatsCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Collects the server-wide session SUMMARY (connection-leak / idle signal) via the shared
    /// <see cref="SessionSummaryStatsCollector"/> definition (query text and payload order live there —
    /// the cross-SKU parity contract). Distinct from the per-application session_stats collector.
    /// </summary>
    private Task<int> CollectSessionSummaryStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(SessionSummaryStatsCollector.Instance, server, cancellationToken);
}
