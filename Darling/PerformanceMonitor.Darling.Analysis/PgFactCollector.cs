/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Analysis;

namespace PerformanceMonitor.Darling.Analysis;

/// <summary>
/// Collects facts from Darling's Postgres store for the analysis engine — Lite's
/// <c>DuckDbFactCollector</c> ported method-for-method (Phase-5 analysis slice AN2a): the same
/// 28 collect methods across the same seven partial files, the same fact keys / values /
/// metadata shapes, the same emission order, and the same per-method swallow-everything error
/// posture (Lite's collector logs nothing in those catches; neither does this port — a missing
/// table or empty store degrades to "no facts", never an exception). Lite's analysis SQL was
/// deliberately written in the PG-shared dialect and the V4 passthrough <c>v_&lt;table&gt;</c>
/// views exist precisely so these queries run VERBATIM — every query string here is
/// byte-identical to Lite's.
///
/// <para>
/// The documented PG-port deviations, none of which touch a query's text:
/// <list type="bullet">
/// <item><description>/* PG port: Lite wraps every read in <c>_duckDb.AcquireReadLock()</c> —
/// DuckDB is a single-writer embedded file and Lite serializes readers against the writer.
/// Postgres is MVCC; no read lock exists or is needed, so the lock line is dropped from every
/// method. */</description></item>
/// <item><description>/* PG port: parameters bind positionally as <c>$N</c> exactly like Lite's
/// DuckDB SQL already did, via Npgsql's positional <c>AddWithValue</c> (the PgFindingStore
/// pattern); every <see cref="DateTime"/> is bound naive-UTC Kind-Unspecified — the
/// PgCollectorRowWriter discipline, because Npgsql 6+ maps Kind-Utc to <c>timestamptz</c> and
/// rejects it against the store's naive <c>timestamp</c> columns. AnalysisContext times are
/// used as-is (host-UTC window semantics — no offset math). */</description></item>
/// <item><description>/* PG port: Lite's BigInteger-tolerant <c>ToInt64</c> exists because
/// DuckDB hands wide aggregates back boxed as <see cref="System.Numerics.BigInteger"/>; Npgsql
/// never does — Postgres returns <c>numeric</c> aggregates as <see cref="decimal"/>, which
/// <see cref="Convert.ToInt64(object)"/> already handles — so the BigInteger branch is dropped
/// (see <see cref="PgBlockingPairRowQuery.ToInt64"/>). */</description></item>
/// <item><description>/* PG port: the SQL moves from inline literals to <c>public const</c>
/// fields (text unchanged) so Darling.Tests can pin the dialect ungated — the
/// PgFindingStore/DarlingAlertReadAdapter convention. */</description></item>
/// </list>
/// No query used <c>NOW()</c>/<c>CURRENT_TIMESTAMP</c> (every window bound is a parameter), no
/// <c>QUALIFY</c> appears (that lives in BaselineProvider, a different slice), and the one
/// <c>any_value()</c> use is standard SQL:2023, in Postgres since 16 (the product's minimum PG
/// is 17) — all pinned by <c>PgFactCollectorTests</c>.
/// </para>
/// </summary>
public sealed partial class PgFactCollector : IFactCollector
{
    private readonly NpgsqlDataSource _postgres;

    public PgFactCollector(NpgsqlDataSource postgres)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
    }

    public async Task<List<Fact>> CollectFactsAsync(AnalysisContext context)
    {
        var facts = new List<Fact>();

        await CollectWaitStatsFactsAsync(context, facts);
        FactCollectorHelpers.GroupGeneralLockWaits(facts, context);
        FactCollectorHelpers.GroupParallelismWaits(facts, context);
        await CollectBlockingFactsAsync(context, facts);
        await CollectBlockingChainFactsAsync(context, facts);
        await CollectDeadlockFactsAsync(context, facts);
        await CollectServerConfigFactsAsync(context, facts);
        await CollectMemoryFactsAsync(context, facts);
        await CollectDatabaseSizeFactAsync(context, facts);
        await CollectServerMetadataFactsAsync(context, facts);
        await CollectCpuUtilizationFactsAsync(context, facts);
        await CollectRunnableTaskFactsAsync(context, facts);
        await CollectIoLatencyFactsAsync(context, facts);
        await CollectTempDbFactsAsync(context, facts);
        await CollectMemoryGrantFactsAsync(context, facts);
        await CollectQueryStatsFactsAsync(context, facts);
        await CollectParameterSensitivityFactsAsync(context, facts);
        await CollectPlanRegressionFactsAsync(context, facts);
        await CollectBadActorFactsAsync(context, facts);
        await CollectPerfmonFactsAsync(context, facts);
        await CollectMemoryClerkFactsAsync(context, facts);
        await CollectPlanCacheFactsAsync(context, facts);
        await CollectMemoryPressureEventFactsAsync(context, facts);
        await CollectDatabaseConfigFactsAsync(context, facts);
        await CollectFileAutogrowthFactsAsync(context, facts);
        await CollectProcedureStatsFactsAsync(context, facts);
        await CollectActiveQueryFactsAsync(context, facts);
        await CollectRunningJobFactsAsync(context, facts);
        await CollectSessionFactsAsync(context, facts);
        await CollectTraceFlagFactsAsync(context, facts);
        await CollectServerPropertiesFactsAsync(context, facts);
        await CollectDiskSpaceFactsAsync(context, facts);
        await CollectPlanAdvisoryFactsAsync(context, facts);

        return facts;
    }

    /// <summary>
    /// Every query this collector executes, for the ungated dialect/hygiene pins in
    /// Darling.Tests (no QUALIFY, no bare NOW()/CURRENT_TIMESTAMP, no read_parquet, $N
    /// positional parameters only, and every FROM/JOIN target resolves to a V4 passthrough
    /// view or a V1/V2 table).
    /// </summary>
    public static IReadOnlyList<string> AllSql { get; } = new[]
    {
        WaitStatsSql,
        BlockingSql,
        BlockingChainSql,
        PgBlockingPairRowQuery.DmvSnapshotSql,
        DeadlocksSql,
        ServerConfigSql,
        MemoryStatsSql,
        DatabaseSizeSql,
        ServerMetadataSql,
        CpuUtilizationSql,
        RunnableTaskStatsSql,
        IoLatencySql,
        TempDbSql,
        MemoryGrantSql,
        QueryStatsSql,
        ParameterSensitivitySql,
        PlanRegressionSql,
        BadActorSql,
        PerfmonSql,
        MemoryClerkSql,
        PlanCacheStatsSql,
        MemoryPressureEventsSql,
        DatabaseConfigSql,
        FileAutogrowthSql,
        ProcedureStatsSql,
        ActiveQuerySql,
        RunningJobsSql,
        SessionStatsSql,
        TraceFlagsSql,
        ServerPropertiesSql,
        DiskSpaceSql,
        PlanAdvisorySql
    };

    // Single impl lives in PgBlockingPairRowQuery (shared with the pair-row reader);
    // delegate so the check isn't duplicated — the Lite shape, minus DuckDB's BigInteger case.
    private static long ToInt64(object value) => PgBlockingPairRowQuery.ToInt64(value);

    /// <summary>Kind-Unspecified for parameter binds — Npgsql 6+ rejects Kind-Utc against
    /// <c>timestamp</c> (the PgCollectorRowWriter / PgFindingStore discipline).</summary>
    private static DateTime AsNaive(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
