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
    /// Collects the always-on DMV blocking snapshot via the shared
    /// <see cref="DmvBlockingSnapshotCollector"/> definition (the layered minimum-wait floors and
    /// the synthetic negative monitor_loop live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectDmvBlockingSnapshotAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(DmvBlockingSnapshotCollector.Instance, server, cancellationToken);
}
