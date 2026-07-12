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
    /// Collects retained SQL Agent job-run history (every step row + the job-outcome row from
    /// msdb.dbo.sysjobhistory) via the shared <see cref="JobHistoryCollector"/> definition — the
    /// incremental instance_id high-water-mark dedup, the run_datetime / run_duration HHMMSS decode, and
    /// the sysjobs/syscategories joins all live there (the cross-SKU parity contract). Read-only. Not
    /// collected on Azure SQL DB nor when the login lacks msdb access — the definition's AppliesTo
    /// (<c>!IsAzureSqlDb &amp;&amp; HasMsdbAccess</c>) is the single gate, which RunCollectorAsync consults
    /// pre-dispatch for the clean SKIPPED log.
    /// </summary>
    private Task<int> CollectJobHistoryAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(JobHistoryCollector.Instance, server, cancellationToken);
}
