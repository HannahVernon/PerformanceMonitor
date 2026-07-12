/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// THE forcing function for the collector gate-surface collapse (parity audit §01, Tier 0). Before this,
/// Lite gated in two places — the shared <see cref="ICollectorSchemaInfo.AppliesTo"/> AND its own
/// <c>IsCollectorSupported</c> — while Darling ran only <c>AppliesTo</c>, so every gate Lite kept in the
/// second layer (server_config/trace_flags on Azure SQL DB, the query_stats/query_store version gate, and
/// the running_jobs/job_history/agent_status msdb gate) was silently ignored by Darling. That layer is gone.
/// These pins assert:
/// <list type="number">
/// <item>each moved gate's <c>AppliesTo</c> truth table (Azure SQL DB / AWS RDS / no-msdb / pre-2016), so a
/// gate can't silently regress;</item>
/// <item>that the by-name surface Lite dispatches through (<see cref="CollectorCatalog.AppliesTo(string, CollectorTargetInfo)"/>)
/// agrees with the definition's own <c>AppliesTo</c> that Darling's runner calls — the proof both SKUs now
/// gate identically off ONE surface;</item>
/// <item>the <see cref="CollectorTargetInfo.HasMsdbAccess"/> default (unknown ⇒ assume access) and the
/// unknown-collector-name fall-through.</item>
/// </list>
/// </summary>
public sealed class CollectorGateSurfacePinTests
{
    /* Representative targets spanning every gate dimension. */
    private static readonly CollectorTargetInfo OnPrem2016 = new() { SqlMajorVersion = 13 };
    private static readonly CollectorTargetInfo OnPrem2014 = new() { SqlMajorVersion = 12 };
    private static readonly CollectorTargetInfo AzureSqlDb = new() { IsAzureSqlDb = true, SqlMajorVersion = 12 };
    private static readonly CollectorTargetInfo AzureMi = new() { IsAzureManagedInstance = true, SqlMajorVersion = 12 };
    private static readonly CollectorTargetInfo AwsRds = new() { IsAwsRds = true, SqlMajorVersion = 15 };
    private static readonly CollectorTargetInfo NoMsdb = new() { SqlMajorVersion = 15, HasMsdbAccess = false };
    private static readonly CollectorTargetInfo Unknown = new();

    private static readonly CollectorTargetInfo[] AllTargets =
        { OnPrem2016, OnPrem2014, AzureSqlDb, AzureMi, AwsRds, NoMsdb, Unknown };

    [Fact]
    public void ServerConfig_AppliesTo_SkipsOnlyAzureSqlDb()
    {
        Assert.False(ServerConfigCollector.Instance.AppliesTo(AzureSqlDb));  /* no sys.configurations */
        Assert.True(ServerConfigCollector.Instance.AppliesTo(AzureMi));
        Assert.True(ServerConfigCollector.Instance.AppliesTo(AwsRds));
        Assert.True(ServerConfigCollector.Instance.AppliesTo(OnPrem2016));
        Assert.True(ServerConfigCollector.Instance.AppliesTo(NoMsdb));
        Assert.True(ServerConfigCollector.Instance.AppliesTo(Unknown));
    }

    [Fact]
    public void TraceFlags_AppliesTo_SkipsOnlyAzureSqlDb()
    {
        Assert.False(TraceFlagsCollector.Instance.AppliesTo(AzureSqlDb));    /* no DBCC TRACESTATUS */
        Assert.True(TraceFlagsCollector.Instance.AppliesTo(AzureMi));
        Assert.True(TraceFlagsCollector.Instance.AppliesTo(AwsRds));
        Assert.True(TraceFlagsCollector.Instance.AppliesTo(OnPrem2016));
        Assert.True(TraceFlagsCollector.Instance.AppliesTo(Unknown));
    }

    [Fact]
    public void QueryStats_AppliesTo_SkipsOnlyPreSql2016OnPrem()
    {
        Assert.False(QueryStatsCollector.Instance.AppliesTo(OnPrem2014)); /* pre-2016 lacks columns read */
        Assert.True(QueryStatsCollector.Instance.AppliesTo(OnPrem2016));
        Assert.True(QueryStatsCollector.Instance.AppliesTo(AzureSqlDb));  /* low ProductMajorVersion, DMV OK */
        Assert.True(QueryStatsCollector.Instance.AppliesTo(AzureMi));
        Assert.True(QueryStatsCollector.Instance.AppliesTo(Unknown));     /* 0 ⇒ assume newest */
    }

    [Fact]
    public void QueryStore_AppliesTo_SkipsOnlyPreSql2016OnPrem()
    {
        Assert.False(QueryStoreCollector.Instance.AppliesTo(OnPrem2014)); /* Query Store shipped in 2016 */
        Assert.True(QueryStoreCollector.Instance.AppliesTo(OnPrem2016));
        Assert.True(QueryStoreCollector.Instance.AppliesTo(AzureSqlDb));
        Assert.True(QueryStoreCollector.Instance.AppliesTo(AzureMi));
        Assert.True(QueryStoreCollector.Instance.AppliesTo(Unknown));
    }

    [Fact]
    public void RunningJobs_AppliesTo_SkipsAzureSqlDbRdsAndNoMsdb()
    {
        Assert.False(RunningJobsCollector.Instance.AppliesTo(AzureSqlDb));
        Assert.False(RunningJobsCollector.Instance.AppliesTo(AwsRds));    /* joins msdb.dbo.syssessions */
        Assert.False(RunningJobsCollector.Instance.AppliesTo(NoMsdb));
        Assert.True(RunningJobsCollector.Instance.AppliesTo(AzureMi));
        Assert.True(RunningJobsCollector.Instance.AppliesTo(OnPrem2016));
        Assert.True(RunningJobsCollector.Instance.AppliesTo(Unknown));
    }

    [Fact]
    public void JobHistory_AppliesTo_SkipsAzureSqlDbAndNoMsdb_ButNotRds()
    {
        Assert.False(JobHistoryCollector.Instance.AppliesTo(AzureSqlDb));
        Assert.False(JobHistoryCollector.Instance.AppliesTo(NoMsdb));
        Assert.True(JobHistoryCollector.Instance.AppliesTo(AwsRds));      /* never touches syssessions */
        Assert.True(JobHistoryCollector.Instance.AppliesTo(AzureMi));
        Assert.True(JobHistoryCollector.Instance.AppliesTo(OnPrem2016));
        Assert.True(JobHistoryCollector.Instance.AppliesTo(Unknown));
    }

    [Fact]
    public void AgentStatus_AppliesTo_SkipsAzureSqlDbRdsAndNoMsdb()
    {
        Assert.False(AgentStatusCollector.Instance.AppliesTo(AzureSqlDb));
        Assert.False(AgentStatusCollector.Instance.AppliesTo(AwsRds));    /* no sys.dm_server_services */
        Assert.False(AgentStatusCollector.Instance.AppliesTo(NoMsdb));
        Assert.True(AgentStatusCollector.Instance.AppliesTo(AzureMi));
        Assert.True(AgentStatusCollector.Instance.AppliesTo(OnPrem2016));
        Assert.True(AgentStatusCollector.Instance.AppliesTo(Unknown));
    }

    [Fact]
    public void CatalogByNameGate_AgreesWithDefinitionAppliesTo_ForEveryCollectorAndTarget()
    {
        /* The parity crux: Lite consults CollectorCatalog.AppliesTo(name, target) pre-dispatch; Darling's
           runner calls definition.AppliesTo(target). If those ever disagreed the two SKUs would gate
           differently — exactly the drift this collapse removes. Pin that they are identical for every
           catalog collector across every target dimension. */
        foreach (var definition in CollectorCatalog.All)
        {
            foreach (var target in AllTargets)
            {
                Assert.Equal(
                    definition.AppliesTo(target),
                    CollectorCatalog.AppliesTo(definition.Name, target));
            }
        }
    }

    [Fact]
    public void CatalogGate_UnknownCollectorName_IsNotGated()
    {
        /* A typo'd/renamed name is not silently skipped — it falls through to true so the host's dispatch
           switch raises its "Unknown collector" error instead of the gate masking it. */
        Assert.True(CollectorCatalog.AppliesTo("no_such_collector", AzureSqlDb));
        Assert.True(CollectorCatalog.AppliesTo("no_such_collector", NoMsdb));
    }

    [Fact]
    public void HasMsdbAccess_DefaultsToTrue_SoUnknownTargetsStillAttemptAgentCollectors()
    {
        /* The probe returns NULL ⇒ assume access; every bare CollectorTargetInfo must mirror that, or the
           three Agent collectors would silently gate off on the unknown path. */
        Assert.True(new CollectorTargetInfo().HasMsdbAccess);
        Assert.True(RunningJobsCollector.Instance.AppliesTo(new CollectorTargetInfo()));
        Assert.True(JobHistoryCollector.Instance.AppliesTo(new CollectorTargetInfo()));
        Assert.True(AgentStatusCollector.Instance.AppliesTo(new CollectorTargetInfo()));
    }
}
