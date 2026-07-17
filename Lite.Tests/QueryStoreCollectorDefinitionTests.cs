/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted query_store definition: the actual_state enumeration
/// (on-prem AG-aware, Azure not), the live PRODUCTVERSION probe deciding the 2017+/2022+ column
/// gates (default 13 when the probe fails), the last_execution_time incremental watermark with its
/// 60-minute fallback, and the 54-column payload. Second Name≠TargetTable case (query_store →
/// query_store_stats).
/// </summary>
public sealed class QueryStoreCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    private static CollectorContext MakeContext(
        bool isAzureSqlDb = false,
        object? probeResult = null,
        DateTime? watermark = null,
        DateTime? collectionTime = null,
        bool capturePlanXml = false)
        => new()
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = collectionTime ?? new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc),
            Deltas = s_deltas,
            Target = new CollectorTargetInfo { IsAzureSqlDb = isAzureSqlDb },
            Watermark = watermark,
            EnumerationProbeResult = probeResult,
            CapturePlanXml = capturePlanXml,
        };

    [Fact]
    public void Identity_SecondNameTargetTableSplit_WithWatermark()
    {
        Assert.Equal("query_store", QueryStoreCollector.Instance.Name);
        Assert.Equal("query_store_stats", QueryStoreCollector.Instance.TargetTable);
        Assert.Equal("last_execution_time", QueryStoreCollector.Instance.WatermarkColumn);
    }

    [Fact]
    public void AppliesTo_VersionGate_SkipsPreSql2016OnPrem_ButNotAzureOrUnknown()
    {
        /* Query Store first shipped in SQL 2016 (v13). Gate collapsed from Lite's IsCollectorSupported into
           the shared AppliesTo (so Darling gates too); a pre-2016 box has no Query Store at all. */
        Assert.False(QueryStoreCollector.Instance.AppliesTo(new CollectorTargetInfo { SqlMajorVersion = 12 }));
        Assert.True(QueryStoreCollector.Instance.AppliesTo(new CollectorTargetInfo { SqlMajorVersion = 13 }));
        Assert.True(QueryStoreCollector.Instance.AppliesTo(new CollectorTargetInfo { SqlMajorVersion = 16 }));
        /* Unknown (0) assumes newest; Azure SQL DB / MI report a low ProductMajorVersion but ship Query Store. */
        Assert.True(QueryStoreCollector.Instance.AppliesTo(new CollectorTargetInfo { SqlMajorVersion = 0 }));
        Assert.True(QueryStoreCollector.Instance.AppliesTo(new CollectorTargetInfo { IsAzureSqlDb = true, SqlMajorVersion = 12 }));
        Assert.True(QueryStoreCollector.Instance.AppliesTo(new CollectorTargetInfo { IsAzureManagedInstance = true, SqlMajorVersion = 12 }));
    }

    [Fact]
    public void BuildEnumerationQuery_OnPrem_AgAware_ProbesActualState_WithExclusions()
    {
        var plan = QueryStoreCollector.Instance.BuildEnumerationQuery(new CollectorContext
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = DateTime.UtcNow,
            Deltas = s_deltas,
            ExcludedDatabases = new[] { "SO" },
        });

        Assert.NotNull(plan);
        Assert.Contains("sys.dm_hadr_database_replica_states", plan!.Text, StringComparison.Ordinal);
        Assert.Contains("drs.is_primary_replica = 1", plan.Text, StringComparison.Ordinal);
        /* IN (1, 2, 4) = READ_ONLY/READ_WRITE/READ_CAPTURE_SECONDARY, not "> 0": 3 = ERROR must not
           pass the "is QS usable" gate. */
        Assert.Contains("WHERE actual_state IN (1, 2, 4)", plan.Text, StringComparison.Ordinal);
        Assert.Contains("AND d.name NOT IN (@excl_db_0)", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/*EXCLUSION_FILTER*/", plan.Text, StringComparison.Ordinal);
        Assert.Equal("SO", Assert.Single(plan.Parameters).Value);
    }

    [Fact]
    public void BuildEnumerationQuery_Azure_NoAgJoin()
    {
        var plan = QueryStoreCollector.Instance.BuildEnumerationQuery(MakeContext(isAzureSqlDb: true));

        Assert.NotNull(plan);
        Assert.DoesNotContain("dm_hadr_database_replica_states", plan!.Text, StringComparison.Ordinal);
        Assert.Contains("WHERE actual_state IN (1, 2, 4)", plan.Text, StringComparison.Ordinal);
        Assert.Empty(plan.Parameters);
    }

    [Fact]
    public void BuildEnumerationProbe_PinsLiveProductVersionCheck()
    {
        var probe = QueryStoreCollector.Instance.BuildEnumerationProbe(MakeContext());

        Assert.NotNull(probe);
        Assert.Equal(
            "SELECT CONVERT(integer, PARSENAME(CONVERT(sysname, SERVERPROPERTY('PRODUCTVERSION')), 4))",
            probe!.Text);
        Assert.Empty(probe.Parameters);
    }

    [Fact]
    public void BuildPerItemQuery_ProbeFailed_DefaultsTo2016_NullGatedColumns()
    {
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(probeResult: null));

        Assert.Contains("avg_num_physical_io_reads = NULL", plan.Text, StringComparison.Ordinal);
        Assert.Contains("avg_log_bytes_used = NULL", plan.Text, StringComparison.Ordinal);
        Assert.Contains("avg_tempdb_space_used = NULL", plan.Text, StringComparison.Ordinal);
        Assert.Contains("plan_forcing_type = NULL,", plan.Text, StringComparison.Ordinal);
        Assert.Contains("plan_type = NULL,", plan.Text, StringComparison.Ordinal);
        Assert.Contains("replica_role = CONVERT(nvarchar(1), NULL)", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.query_store_replicas", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPerItemQuery_2017Probe_RealColumns_NoPlanType()
    {
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(probeResult: 14));

        Assert.Contains("qsrs.avg_num_physical_io_reads, qsrs.min_num_physical_io_reads, qsrs.max_num_physical_io_reads,", plan.Text, StringComparison.Ordinal);
        Assert.Contains("avg_log_bytes_used = qsrs.avg_log_bytes_used", plan.Text, StringComparison.Ordinal);
        Assert.Contains("avg_tempdb_space_used = qsrs.avg_tempdb_space_used", plan.Text, StringComparison.Ordinal);
        Assert.Contains("plan_forcing_type = qsp.plan_forcing_type_desc,", plan.Text, StringComparison.Ordinal);
        Assert.Contains("plan_type = NULL,", plan.Text, StringComparison.Ordinal);

        /* Replica attribution is 2022+ only — sys.query_store_replicas does not exist on 2017. */
        Assert.Contains("replica_role = CONVERT(nvarchar(1), NULL)", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.query_store_replicas", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPerItemQuery_2022Probe_PlanTypeColumn()
    {
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(probeResult: 16));

        Assert.Contains("plan_type = qsp.plan_type_desc,", plan.Text, StringComparison.Ordinal);
        Assert.Contains("plan_forcing_type = qsp.plan_forcing_type_desc,", plan.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Replica attribution turns on at major 16 — sys.query_store_replicas and
    /// sys.query_store_runtime_stats.replica_group_id both exist from SQL 2022, verified live against
    /// 16.0.4255.1 (the docs' "2025+" claim for the view is wrong), so the gate is >= 16, not >= 17.
    /// </summary>
    [Fact]
    public void BuildPerItemQuery_2022Probe_ReplicaRole_LeftJoinsReplicas()
    {
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(probeResult: 16));

        /* replica_name read directly: contrary to the docs it IS populated on box SQL Server
           ('Primary', 'Secondary', 'Geo Secondary', 'Geo HA Secondary'), so no role_type CASE. */
        Assert.Contains("replica_role = qsr.replica_name", plan.Text, StringComparison.Ordinal);

        /* MUST be a LEFT JOIN. On a 2022 standalone sys.query_store_replicas has ZERO rows while real
           runtime-stats rows still carry replica_group_id = 1 — an INNER JOIN would match nothing and
           silently delete ALL Query Store collection on every 2022 standalone server. */
        Assert.Contains(
            "LEFT JOIN sys.query_store_replicas AS qsr",
            plan.Text,
            StringComparison.Ordinal);
        Assert.Contains("ON qsr.replica_group_id = qsrs.replica_group_id", plan.Text, StringComparison.Ordinal);

        /* No filter to a single role: rows are attributed, never dropped. */
        Assert.DoesNotContain("replica_name = N''Primary''", plan.Text, StringComparison.Ordinal);
    }

    /// <summary>SQL 2025 (major 17) keeps the 2022 attribution — the gate is >=, not ==.</summary>
    [Fact]
    public void BuildPerItemQuery_2025Probe_ReplicaRole_StillAttributed()
    {
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(probeResult: 17));

        Assert.Contains("replica_role = qsr.replica_name", plan.Text, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN sys.query_store_replicas AS qsr", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPerItemQuery_AzureWithFailedProbe_StillNewColumns()
    {
        /* Azure SQL DB reports low PRODUCTVERSION majors historically — the edition overrides
           the version gate, exactly as the original's isNew computation did. */
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(isAzureSqlDb: true, probeResult: null));

        Assert.Contains("qsrs.avg_num_physical_io_reads", plan.Text, StringComparison.Ordinal);
        Assert.Contains("plan_forcing_type = qsp.plan_forcing_type_desc,", plan.Text, StringComparison.Ordinal);
        Assert.Contains("plan_type = NULL,", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPerItemQuery_PinsWatermarkCutoff_AndSpExecutesqlShape()
    {
        var watermark = new DateTime(2026, 7, 2, 11, 30, 0, DateTimeKind.Utc);
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("we]ird", MakeContext(watermark: watermark));

        /* Line-ending agnostic: pin the bracket escaping + the sp_executesql shape. */
        Assert.StartsWith("EXECUTE [we]]ird].sys.sp_executesql", plan.Text.TrimStart('\r', '\n'), StringComparison.Ordinal);
        Assert.Contains("WHERE qsrs.last_execution_time > @cutoff_time", plan.Text, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE N''%PerformanceMonitorLite%''", plan.Text, StringComparison.Ordinal);
        Assert.Contains("OPTION(RECOMPILE, LOOP JOIN);", plan.Text, StringComparison.Ordinal);
        Assert.Contains("N'@cutoff_time datetime2(7)',", plan.Text, StringComparison.Ordinal);

        var parameter = Assert.Single(plan.Parameters);
        Assert.Equal("@cutoff_time", parameter.Name);
        Assert.Equal(watermark, parameter.Value);
        Assert.Equal(CollectorParameterType.DateTime2, parameter.Type);
    }

    [Fact]
    public void BuildPerItemQuery_PlanCapture_OffEmitsNullPlaceholder_OnMirrorsDashboard()
    {
        /* Lite parity (default off): query_plan_text is the nvarchar(1) NULL placeholder,
           byte-identical to the no-plan form. Darling (on): CONVERT(nvarchar(max), qsp.query_plan)
           from sys.query_store_plan — install/09_collect_query_store.sql's @collect_plan path. */
        var off = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext());
        Assert.Contains("query_plan_text = CONVERT(nvarchar(1), NULL),", off.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("qsp.query_plan,", off.Text, StringComparison.Ordinal);

        /* #1556 plan-text dedupe (ON branch): the plan lands once per plan_id per cycle — on the newest
           runtime-stats interval (rn = 1) — and NULL on the older intervals, instead of the full plan XML
           repeating on every interval row. */
        var on = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(capturePlanXml: true));
        Assert.Contains(
            "query_plan_text = CASE WHEN ROW_NUMBER() OVER (PARTITION BY qsp.plan_id ORDER BY qsrs.last_execution_time DESC) = 1 THEN CONVERT(nvarchar(max), qsp.query_plan) ELSE CONVERT(nvarchar(max), NULL) END,",
            on.Text,
            StringComparison.Ordinal);

        /* Scoped to query_plan_text rather than the bare placeholder: replica_role shares the same
           nvarchar(1) NULL idiom on a pre-2022 target (this context's probe defaults to 13), so an
           unqualified DoesNotContain would assert on an unrelated column. */
        Assert.DoesNotContain("query_plan_text = CONVERT(nvarchar(1), NULL)", on.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPerItemQuery_RowCapAndOrderByDesc_BoundBothCaptureModes()
    {
        /* #1556: a per-database server-side backstop — TOP (50000) keeps only the newest rows and
           ORDER BY last_execution_time DESC makes "newest" deterministic. Present in BOTH capture modes:
           the ORDER BY is load-bearing for the client byte budget's early-stop too (it keeps the newest
           rows when the reader is cut short). */
        foreach (var plan in new[]
        {
            QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext()),
            QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(capturePlanXml: true)),
        })
        {
            Assert.Contains($"TOP ({QueryStoreCollector.MaxRowsPerDatabase})", plan.Text, StringComparison.Ordinal);
            Assert.Contains("ORDER BY qsrs.last_execution_time DESC", plan.Text, StringComparison.Ordinal);
            /* The row-bounding ORDER BY sits before the existing query hint, which the OPTION pin still checks. */
            Assert.Contains("OPTION(RECOMPILE, LOOP JOIN);", plan.Text, StringComparison.Ordinal);
        }

        Assert.Equal(50_000, QueryStoreCollector.MaxRowsPerDatabase);
    }

    [Fact]
    public void PerDatabaseBounds_ExposeWatermarkColumnAndBudgets()
    {
        /* #1556: query_store now flushes per database, so its watermark is per-database (keyed on
           database_name — the deadlocks/BPR precedent), and it advertises both the row-cap warn threshold
           and the 256MB client byte budget so the host can surface the WARNING and the definition can
           enforce the early-stop. */
        Assert.Equal("database_name", QueryStoreCollector.Instance.PerDatabaseWatermarkColumn);
        Assert.Equal(QueryStoreCollector.MaxRowsPerDatabase, QueryStoreCollector.Instance.PerItemRowCountWarnThreshold);
        Assert.Equal(QueryStoreCollector.MaxTextBytesPerDatabase, QueryStoreCollector.Instance.PerItemTextByteBudget);
        Assert.Equal(256 * 1024 * 1024, QueryStoreCollector.MaxTextBytesPerDatabase);
    }

    [Fact]
    public async Task ReadItemAsync_ResetsTextBudgetSignal_AndNormalRowsDoNotTripIt()
    {
        /* #1556: ReadItemAsync resets the per-item truncation signal at entry (pre-set here to prove it),
           and a normal, small row never trips the 256MB budget — the signal stays false, so the host emits
           no spurious WARNING and every row is read. The 256MB early-stop itself is a `>= budget` break
           that cannot be sanely driven to a real quarter-gig in a unit test; its threshold is pinned above
           and its WARNING wiring is exercised by the shared-driver tests. */
        var context = MakeContext();
        context.PerItemTextBudgetExceeded = true;

        var row = new object[53];
        row[0] = 101L;
        row[1] = 202L;
        row[2] = "Regular";
        row[3] = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
        row[4] = new DateTimeOffset(2026, 7, 2, 11, 0, 0, TimeSpan.Zero);
        row[5] = "dbo.Proc";
        row[6] = "SELECT 1";
        row[7] = "0xQH";
        row[8] = 33L;
        for (int i = 9; i <= 43; i++) row[i] = (long)i;
        row[44] = DBNull.Value;
        row[45] = "MANUAL";
        row[46] = true;
        row[47] = 5L;
        row[48] = "NONE";
        row[49] = (short)160;
        row[50] = DBNull.Value;
        row[51] = "0xPH";
        row[52] = DBNull.Value;

        using var reader = new FakeCollectorDataReader(row);
        var rows = new System.Collections.Generic.List<QueryStoreCollector.Row>();
        await QueryStoreCollector.Instance.ReadItemAsync("SO", reader, rows, context, CancellationToken.None);

        Assert.False(context.PerItemTextBudgetExceeded);
        Assert.Single(rows);
    }

    [Fact]
    public void BuildPerItemQuery_NoWatermark_FallsBack60Minutes()
    {
        var collectionTime = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(collectionTime: collectionTime));

        Assert.Equal(collectionTime.AddMinutes(-60), Assert.Single(plan.Parameters).Value);
    }

    [Fact]
    public void PayloadColumns_MatchSchemaOrder_54Columns()
    {
        var names = QueryStoreCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();

        Assert.Equal(54, names.Length);
        Assert.Equal("database_name", names[0]);
        Assert.Equal("query_id", names[1]);
        Assert.Equal("execution_count", names[9]);
        Assert.Equal("avg_num_physical_io_reads", names[36]);
        Assert.Equal("plan_type", names[45]);
        Assert.Equal("is_forced_plan", names[47]);
        Assert.Equal("compatibility_level", names[50]);
        Assert.Equal("query_plan_hash", names[52]);

        /* replica_role is pinned LAST deliberately: both hosts' bulk writers are positional, and an
           upgraded store receives this column from an ALTER TABLE ADD COLUMN, which can only append.
           Moving it earlier would desync a fresh store (DDL generated from this list) from an upgraded
           one. See the CollectorColumn comment in QueryStoreCollector. */
        Assert.Equal("replica_role", names[53]);
    }

    [Fact]
    public async Task ReadItemAsync_WritePayload_Pins54ColumnOrder_AndTypeCoercions()
    {
        var context = MakeContext();
        var firstExec = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.FromHours(-4));
        var lastExec = new DateTimeOffset(2026, 7, 2, 11, 0, 0, TimeSpan.FromHours(-4));

        var row = new object[53];
        row[0] = 101L;                      /* query_id */
        row[1] = 202L;                      /* plan_id */
        row[2] = "Regular";                 /* execution_type_desc */
        row[3] = firstExec;                 /* first_execution_time (datetimeoffset) */
        row[4] = lastExec;                  /* last_execution_time (datetimeoffset) */
        row[5] = "dbo.Proc";                /* module_name */
        row[6] = "SELECT 1";                /* query_sql_text */
        row[7] = "0xQH";                    /* query_hash */
        row[8] = 33L;                       /* count_executions */
        row[9] = 123.7d;                    /* avg_duration: float catalog value -> (long) */
        row[10] = 456.9f;                   /* min_duration: single -> (long) */
        row[11] = 789m;                     /* max_duration: decimal -> (long) */
        row[12] = 42;                       /* avg_cpu_time: int passthrough */
        row[13] = (short)7;                 /* min_cpu_time: short passthrough */
        row[14] = DBNull.Value;             /* max_cpu_time: NULL -> 0 */
        for (int i = 15; i <= 43; i++) row[i] = (long)i;
        row[44] = DBNull.Value;             /* plan_type (pre-2022) */
        row[45] = "MANUAL";                 /* plan_forcing_type */
        row[46] = true;                     /* is_forced_plan */
        row[47] = 5L;                       /* force_failure_count */
        row[48] = "NONE";                   /* last_force_failure_reason */
        row[49] = (short)160;               /* compatibility_level: smallint -> int */
        row[50] = DBNull.Value;             /* query_plan_text (always NULL literal) */
        row[51] = "0xPH";                   /* query_plan_hash */
        row[52] = "Secondary";              /* replica_role (2022+ attributed the row to a secondary) */

        using var reader = new FakeCollectorDataReader(row);
        var rows = new System.Collections.Generic.List<QueryStoreCollector.Row>();
        await QueryStoreCollector.Instance.ReadItemAsync("SO", reader, rows, context, CancellationToken.None);

        var writer = new RecordingCollectorRowWriter();
        QueryStoreCollector.Instance.WritePayload(Assert.Single(rows), writer, context);

        Assert.Equal(54, writer.Values.Count);
        Assert.Equal("SO", writer.Values[0]);                       /* enumerated item leads the payload */
        Assert.Equal(101L, writer.Values[1]);
        Assert.Equal(firstExec.UtcDateTime, writer.Values[4]);      /* datetimeoffset -> UTC DateTime */
        Assert.Equal(123L, writer.Values[10]);                      /* double 123.7 truncated */
        Assert.Equal(456L, writer.Values[11]);                      /* float 456.9 truncated */
        Assert.Equal(789L, writer.Values[12]);
        Assert.Equal(42L, writer.Values[13]);
        Assert.Equal(7L, writer.Values[14]);
        Assert.Equal(0L, writer.Values[15]);                        /* NULL stat -> 0 */
        Assert.Null(writer.Values[45]);                             /* plan_type NULL */
        Assert.Equal(true, writer.Values[47]);
        Assert.Equal(160, writer.Values[50]);                       /* smallint compat -> int */
        Assert.Null(writer.Values[51]);
        Assert.Equal("0xPH", writer.Values[52]);
        Assert.Equal("Secondary", writer.Values[53]);               /* replica_role rides last, after query_plan_hash */
        Assert.Empty(s_deltas.Calls);                               /* incremental snapshot — no deltas */
    }
}
