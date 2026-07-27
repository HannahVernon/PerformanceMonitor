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
    /// Builds the query snapshots SQL with or without live query plan support — forwards to the
    /// shared <see cref="QuerySnapshotsCollector"/> templates so the live snapshot button
    /// (ServerTab) and the scheduled collector keep building identical SQL (#857 Azure #req
    /// staging included).
    /// </summary>
    internal static string BuildQuerySnapshotsQuery(bool supportsLiveQueryPlan, bool isAzureSqlDatabase)
        => QuerySnapshotsCollector.BuildSnapshotQuery(supportsLiveQueryPlan, isAzureSqlDatabase);

    /// <summary>
    /// Collects currently running queries via the shared <see cref="QuerySnapshotsCollector"/>
    /// definition (the CDC-capture detection, the live-plan version gate, the Azure #req staging
    /// from #857, and the #1286 memory/tempdb/transaction triage columns live there — the
    /// cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectQuerySnapshotsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(QuerySnapshotsCollector.Instance, server, cancellationToken);
}
