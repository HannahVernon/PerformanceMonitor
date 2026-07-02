/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    private readonly Lazy<HashSet<string>> _ignoredWaitTypes;

    /// <summary>
    /// Loads the set of wait types to ignore during collection, from the shared <see cref="IgnoredWaitTypes"/>
    /// source (per-user copy, then the bundled fallback) so collection and the wait-stats tab use one list.
    /// Thread-safe via Lazy&lt;T&gt; (multiple server tasks call this in parallel). Warns when the set is
    /// empty so a missing/unreadable list is visible rather than silently disabling filtering (#1240).
    /// </summary>
    private HashSet<string> LoadIgnoredWaitTypes()
    {
        var waits = IgnoredWaitTypes.Load();
        if (waits.Count == 0)
        {
            _logger?.LogWarning("ignored_wait_types.json not found or empty (per-user or bundled); wait-stat filtering disabled");
        }
        return waits;
    }

    /// <summary>
    /// Collects wait statistics from sys.dm_os_wait_stats via the shared
    /// <see cref="WaitStatsCollector"/> definition (query text, ignored-wait filtering, delta
    /// rules, and payload order live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectWaitStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(WaitStatsCollector.Instance, server, cancellationToken);
}
