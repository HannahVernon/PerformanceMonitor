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
    /// Collects the built-in Default Trace events (file auto-grow/shrink stalls, severe ErrorLog writes,
    /// schema DDL, security audits, Server Memory Change) via the shared
    /// <see cref="DefaultTraceEventsCollector"/> definition — the <c>sys.traces</c> +
    /// <c>sys.fn_trace_gettable</c> server-side read, the curated (config-slice-free) event set, and the
    /// <c>event_time</c> dedup watermark all live there (the cross-SKU parity contract). Read-only: nothing
    /// is created or reconfigured on the target, so a disabled default trace simply yields zero rows. Not
    /// collected on Azure SQL DB (the definition's AppliesTo skips the cycle).
    /// </summary>
    private Task<int> CollectDefaultTraceEventsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(DefaultTraceEventsCollector.Instance, server, cancellationToken);
}
