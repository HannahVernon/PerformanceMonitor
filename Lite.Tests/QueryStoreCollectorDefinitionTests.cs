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
/// 60-minute fallback, and the 53-column payload. Second Name≠TargetTable case (query_store →
/// query_store_stats).
/// </summary>
public sealed class QueryStoreCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    private static CollectorContext MakeContext(
        bool isAzureSqlDb = false,
        object? probeResult = null,
        DateTime? watermark = null,
        DateTime? collectionTime = null)
        => new()
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = collectionTime ?? new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc),
            Deltas = s_deltas,
            Target = new CollectorTargetInfo { IsAzureSqlDb = isAzureSqlDb },
            Watermark = watermark,
            EnumerationProbeResult = probeResult,
        };

    [Fact]
    public void Identity_SecondNameTargetTableSplit_WithWatermark()
    {
        Assert.Equal("query_store", QueryStoreCollector.Instance.Name);
        Assert.Equal("query_store_stats", QueryStoreCollector.Instance.TargetTable);
        Assert.Equal("last_execution_time", QueryStoreCollector.Instance.WatermarkColumn);
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
        Assert.Contains("WHERE actual_state > 0", plan.Text, StringComparison.Ordinal);
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
        Assert.Contains("WHERE actual_state > 0", plan.Text, StringComparison.Ordinal);
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
    }

    [Fact]
    public void BuildPerItemQuery_2022Probe_PlanTypeColumn()
    {
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(probeResult: 16));

        Assert.Contains("plan_type = qsp.plan_type_desc,", plan.Text, StringComparison.Ordinal);
        Assert.Contains("plan_forcing_type = qsp.plan_forcing_type_desc,", plan.Text, StringComparison.Ordinal);
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
    public void BuildPerItemQuery_NoWatermark_FallsBack60Minutes()
    {
        var collectionTime = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        var plan = QueryStoreCollector.Instance.BuildPerItemQuery("SO", MakeContext(collectionTime: collectionTime));

        Assert.Equal(collectionTime.AddMinutes(-60), Assert.Single(plan.Parameters).Value);
    }

    [Fact]
    public void PayloadColumns_MatchSchemaOrder_53Columns()
    {
        var names = QueryStoreCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();

        Assert.Equal(53, names.Length);
        Assert.Equal("database_name", names[0]);
        Assert.Equal("query_id", names[1]);
        Assert.Equal("execution_count", names[9]);
        Assert.Equal("avg_num_physical_io_reads", names[36]);
        Assert.Equal("plan_type", names[45]);
        Assert.Equal("is_forced_plan", names[47]);
        Assert.Equal("compatibility_level", names[50]);
        Assert.Equal("query_plan_hash", names[52]);
    }

    [Fact]
    public async Task ReadItemAsync_WritePayload_Pins53ColumnOrder_AndTypeCoercions()
    {
        var context = MakeContext();
        var firstExec = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.FromHours(-4));
        var lastExec = new DateTimeOffset(2026, 7, 2, 11, 0, 0, TimeSpan.FromHours(-4));

        var row = new object[52];
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

        using var reader = new FakeCollectorDataReader(row);
        var rows = new System.Collections.Generic.List<QueryStoreCollector.Row>();
        await QueryStoreCollector.Instance.ReadItemAsync("SO", reader, rows, context, CancellationToken.None);

        var writer = new RecordingCollectorRowWriter();
        QueryStoreCollector.Instance.WritePayload(Assert.Single(rows), writer, context);

        Assert.Equal(53, writer.Values.Count);
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
        Assert.Empty(s_deltas.Calls);                               /* incremental snapshot — no deltas */
    }
}
