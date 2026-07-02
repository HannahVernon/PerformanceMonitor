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
/// Pins the parity contract of the extracted blocked_process_report definition: the server- vs
/// database-scoped ring-buffer reads, the sp_HumanEventsBlockViewer-mirroring wait_resource →
/// contentious-object resolution batch, the event_time watermark with its 10-minute fallback,
/// the 38-column payload, and the report-XML parse (blocked + blocking process attributes,
/// monitorLoop root attribute, null on garbage/missing blocked-process). Third Name≠TargetTable
/// case (blocked_process_report → blocked_process_reports).
/// </summary>
public sealed class BlockedProcessReportCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    private static CollectorContext MakeContext(
        bool isAzureSqlDb = false,
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
        };

    [Fact]
    public void Identity_ThirdNameTargetTableSplit_WithWatermark()
    {
        Assert.Equal("blocked_process_report", BlockedProcessReportCollector.Instance.Name);
        Assert.Equal("blocked_process_reports", BlockedProcessReportCollector.Instance.TargetTable);
        Assert.Equal("event_time", BlockedProcessReportCollector.Instance.WatermarkColumn);
        Assert.Equal("PerformanceMonitor_BlockedProcess", BlockedProcessReportCollector.XeSessionName);
    }

    [Fact]
    public void BuildQuery_ServerScoped_PinsResolutionBatch()
    {
        var plan = BlockedProcessReportCollector.Instance.BuildQuery(MakeContext());

        Assert.Contains("sys.dm_xe_session_targets AS xet", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("dm_xe_database_session_targets", plan.Text, StringComparison.Ordinal);
        Assert.Contains("N'PerformanceMonitor_BlockedProcess'", plan.Text, StringComparison.Ordinal);
        Assert.Contains("event[@name=\"blocked_process_report\"]", plan.Text, StringComparison.Ordinal);
        Assert.Contains("INTO #bpr", plan.Text, StringComparison.Ordinal);
        Assert.Contains(".sys.partitions AS p", plan.Text, StringComparison.Ordinal);
        Assert.Contains("IF @product_version >= 15", plan.Text, StringComparison.Ordinal);
        Assert.Contains("sys.dm_db_page_info(b.resource_database_id, b.resource_file_id, b.resource_page_id, ''LIMITED'')", plan.Text, StringComparison.Ordinal);
        Assert.Contains("N'Unresolved: ' +", plan.Text, StringComparison.Ordinal);
        Assert.Contains("> @cutoff_time", plan.Text, StringComparison.Ordinal);
        /* The final projection keeps the original 5 reader columns. */
        Assert.Contains("b.contentious_object\nFROM #bpr AS b", plan.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_Azure_UsesDatabaseScopedDmvs()
    {
        var plan = BlockedProcessReportCollector.Instance.BuildQuery(MakeContext(isAzureSqlDb: true));

        Assert.Contains("sys.dm_xe_database_session_targets AS xet", plan.Text, StringComparison.Ordinal);
        Assert.Contains("JOIN sys.dm_xe_database_sessions AS xes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("event[@name=\"blocked_process_report\"]", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_PinsWatermarkCutoff_AndTenMinuteFallback()
    {
        var watermark = new DateTime(2026, 7, 2, 11, 45, 0, DateTimeKind.Utc);
        var withWatermark = BlockedProcessReportCollector.Instance.BuildQuery(MakeContext(watermark: watermark));
        var parameter = Assert.Single(withWatermark.Parameters);
        Assert.Equal("@cutoff_time", parameter.Name);
        Assert.Equal(watermark, parameter.Value);
        Assert.Equal(CollectorParameterType.DateTime2, parameter.Type);

        var collectionTime = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        var fallback = BlockedProcessReportCollector.Instance.BuildQuery(MakeContext(collectionTime: collectionTime));
        Assert.Equal(collectionTime.AddMinutes(-10), Assert.Single(fallback.Parameters).Value);
    }

    [Fact]
    public void PayloadColumns_MatchSchemaOrder_38Columns()
    {
        var names = BlockedProcessReportCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();

        Assert.Equal(38, names.Length);
        Assert.Equal("event_time", names[0]);
        Assert.Equal("blocked_spid", names[2]);
        Assert.Equal("wait_time_ms", names[6]);
        Assert.Equal("blocked_sql_text", names[16]);
        Assert.Equal("blocking_sql_text", names[22]);
        Assert.Equal("blocked_process_report_xml", names[33]);
        Assert.Equal("object_id", names[34]);
        Assert.Equal("database_id", names[35]);
        Assert.Equal("contentious_object", names[36]);
        Assert.Equal("monitor_loop", names[37]);
    }

    private const string SampleReportXml = @"<blocked-process-report monitorLoop=""332"">
 <blocked-process>
  <process id=""process1"" spid=""55"" ecid=""0"" waittime=""12345"" waitresource=""KEY: 7:72057594046185472 (abc123)"" lockMode=""U"" status=""suspended"" isolationlevel=""read committed (2)"" logused=""100"" trancount=""2"" clientapp=""app1"" hostname=""host1"" loginname=""login1"" transactionname=""UPDATE"" lasttranstarted=""2026-07-02T11:59:00"" lastbatchstarted=""2026-07-02T11:59:01"" lastbatchcompleted=""2026-07-02T11:58:59"" priority=""0"" currentdbname=""SO"">
   <inputbuf> UPDATE t SET x = 1; </inputbuf>
  </process>
 </blocked-process>
 <blocking-process>
  <process spid=""66"" ecid=""0"" status=""sleeping"" isolationlevel=""read committed (2)"" clientapp=""app2"" hostname=""host2"" loginname=""login2"" transactionname=""user_transaction"" lasttranstarted=""2026-07-02T11:58:00"" lastbatchstarted=""2026-07-02T11:58:01"" lastbatchcompleted=""2026-07-02T11:58:02"" priority=""0"">
   <inputbuf> BEGIN TRAN; </inputbuf>
  </process>
 </blocking-process>
</blocked-process-report>";

    [Fact]
    public void ParseReportXml_MapsBothProcesses_AndMonitorLoop()
    {
        var eventTime = new DateTime(2026, 7, 2, 11, 59, 30, DateTimeKind.Utc);
        var row = BlockedProcessReportCollector.ParseReportXml(SampleReportXml, eventTime);

        Assert.NotNull(row);
        Assert.Equal(eventTime, row!.EventTime);
        Assert.Equal(332, row.MonitorLoop);
        Assert.Equal("SO", row.DatabaseName);
        Assert.Equal(55, row.BlockedSpid);
        Assert.Equal(66, row.BlockingSpid);
        Assert.Equal(12345L, row.WaitTimeMs);
        Assert.Equal("KEY: 7:72057594046185472 (abc123)", row.WaitResource);
        Assert.Equal("U", row.LockMode);
        Assert.Equal(100L, row.BlockedLogUsed);
        Assert.Equal(2, row.BlockedTransactionCount);
        Assert.Equal("UPDATE t SET x = 1;", row.BlockedSqlText);
        Assert.Equal("BEGIN TRAN;", row.BlockingSqlText);
        Assert.Equal("UPDATE", row.BlockedTransactionName);
        Assert.Equal("user_transaction", row.BlockingTransactionName);
        Assert.Equal(new DateTime(2026, 7, 2, 11, 59, 0), row.BlockedLastTranStarted);
        Assert.Equal(new DateTime(2026, 7, 2, 11, 58, 2), row.BlockingLastBatchCompleted);
    }

    [Fact]
    public void ParseReportXml_NullOnGarbage_NullWithoutBlockedProcess()
    {
        Assert.Null(BlockedProcessReportCollector.ParseReportXml("<not-xml", null));
        Assert.Null(BlockedProcessReportCollector.ParseReportXml("<blocked-process-report/>", null));

        /* A report with only a blocking process is unusable — blocked side drives the row. */
        Assert.Null(BlockedProcessReportCollector.ParseReportXml(
            "<blocked-process-report><blocking-process><process spid=\"66\"/></blocking-process></blocked-process-report>", null));
    }

    [Fact]
    public async Task ReadAsync_SkipsEmptyAndUnparseable_WritePayloadPins38ColumnOrder()
    {
        var context = MakeContext();
        var eventTime = new DateTime(2026, 7, 2, 11, 59, 30, DateTimeKind.Utc);

        using var reader = new FakeCollectorDataReader(
            new object[] { eventTime, SampleReportXml, 1234, 7, "dbo.t" },
            new object[] { eventTime, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value },   /* empty xml -> skipped */
            new object[] { eventTime, "<garbage", DBNull.Value, DBNull.Value, DBNull.Value });    /* unparseable -> skipped */
        var rows = await BlockedProcessReportCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        var writer = new RecordingCollectorRowWriter();
        BlockedProcessReportCollector.Instance.WritePayload(row, writer, context);

        Assert.Equal(38, writer.Values.Count);
        Assert.Equal(eventTime, writer.Values[0]);
        Assert.Equal("SO", writer.Values[1]);
        Assert.Equal(55, writer.Values[2]);
        Assert.Equal(66, writer.Values[4]);
        Assert.Equal(12345L, writer.Values[6]);
        Assert.Equal("UPDATE t SET x = 1;", writer.Values[16]);
        Assert.Equal("BEGIN TRAN;", writer.Values[22]);
        Assert.Equal(SampleReportXml, writer.Values[33]);       /* raw XML rides along */
        Assert.Equal(1234, writer.Values[34]);                  /* event object_id (#1140) */
        Assert.Equal(7, writer.Values[35]);
        Assert.Equal("dbo.t", writer.Values[36]);               /* server-side resolved object */
        Assert.Equal(332, writer.Values[37]);                   /* monitorLoop from the root attribute */
        Assert.Empty(s_deltas.Calls);
    }
}
