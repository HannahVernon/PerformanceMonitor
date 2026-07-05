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
    /// Collects raw system_health Extended Events (Stage 1 capture) via the shared
    /// <see cref="SystemHealthEventsCollector"/> definition (the built-in-session ring-buffer read, the
    /// six-event filter, the event_time watermark, and the raw-XML storage all live there — the
    /// cross-SKU parity contract). Unlike the deadlock collector there is NO XE session lifecycle:
    /// system_health is the Database Engine's always-on session, so nothing is created or started here,
    /// just read. Not collected on Azure SQL DB (the definition's AppliesTo skips the cycle).
    /// </summary>
    private Task<int> CollectSystemHealthEventsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(SystemHealthEventsCollector.Instance, server, cancellationToken);
}
