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
    /// Collects the SQL Agent service status snapshot (Running/Stopped from sys.dm_server_services + next
    /// scheduled run from msdb.dbo.sysjobschedules) via the shared <see cref="AgentStatusCollector"/> —
    /// issue #1433 Phase 2, the current-state facts behind the Job History tab header and the "Agent Not
    /// Running" alert. Read-only. Not collected on Azure SQL DB, AWS RDS, nor when the login lacks msdb
    /// access — the shared AppliesTo (<c>!IsAzureSqlDb &amp;&amp; !IsAwsRds &amp;&amp; HasMsdbAccess</c>) is the
    /// single gate, which RunCollectorAsync consults pre-dispatch for the clean SKIPPED log.
    /// </summary>
    private Task<int> CollectAgentStatusAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(AgentStatusCollector.Instance, server, cancellationToken);
}
