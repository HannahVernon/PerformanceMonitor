/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted deadlocks definition: the server-scoped vs
/// database-scoped ring-buffer reads (xml_deadlock_report vs database_xml_deadlock_report), the
/// deadlock_time watermark with its 10-minute fallback, the 5-column payload, the victim-inputbuf
/// extraction (victim match, first-process fallback, null/garbage tolerance), and the #1262 gated
/// best-effort victim plan resolution (off = byte-identical; on = the victim's frame →
/// dm_exec_query_stats → dm_exec_text_query_plan).
/// </summary>
public sealed class DeadlocksCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    private static string Collapse(string sql) => Regex.Replace(sql, @"\s+", "");

    private static CollectorContext MakeContext(
        bool isAzureSqlDb = false,
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
            CapturePlanXml = capturePlanXml,
        };

    [Fact]
    public void Identity_AndSessionNamePinned()
    {
        Assert.Equal("deadlocks", DeadlocksCollector.Instance.Name);
        Assert.Equal("deadlocks", DeadlocksCollector.Instance.TargetTable);
        Assert.Equal("deadlock_time", DeadlocksCollector.Instance.WatermarkColumn);
        Assert.Equal("PerformanceMonitor_Deadlock", DeadlocksCollector.XeSessionName);
    }

    [Fact]
    public void BuildQuery_ServerScoped_ReadsXmlDeadlockReport()
    {
        var plan = DeadlocksCollector.Instance.BuildQuery(MakeContext());

        Assert.Contains("sys.dm_xe_session_targets", plan.Text, StringComparison.Ordinal);
        Assert.Contains("JOIN sys.dm_xe_sessions AS xes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("N'PerformanceMonitor_Deadlock'", plan.Text, StringComparison.Ordinal);
        Assert.Contains("event[@name=\"xml_deadlock_report\"]", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("event[@name=\"database_xml_deadlock_report\"]", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("dm_xe_database_session_targets", plan.Text, StringComparison.Ordinal);
        Assert.Contains("> @cutoff_time", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_Azure_ReadsDatabaseScopedEvent()
    {
        var plan = DeadlocksCollector.Instance.BuildQuery(MakeContext(isAzureSqlDb: true));

        Assert.Contains("sys.dm_xe_database_session_targets", plan.Text, StringComparison.Ordinal);
        Assert.Contains("JOIN sys.dm_xe_database_sessions AS xes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("event[@name=\"database_xml_deadlock_report\"]", plan.Text, StringComparison.Ordinal);
        Assert.Contains("N'PerformanceMonitor_Deadlock'", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_PinsWatermarkCutoff_AndTenMinuteFallback()
    {
        var watermark = new DateTime(2026, 7, 2, 11, 55, 0, DateTimeKind.Utc);
        var withWatermark = DeadlocksCollector.Instance.BuildQuery(MakeContext(watermark: watermark));
        var parameter = Assert.Single(withWatermark.Parameters);
        Assert.Equal("@cutoff_time", parameter.Name);
        Assert.Equal(watermark, parameter.Value);
        Assert.Equal(CollectorParameterType.DateTime2, parameter.Type);

        var collectionTime = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        var fallback = DeadlocksCollector.Instance.BuildQuery(MakeContext(collectionTime: collectionTime));
        Assert.Equal(collectionTime.AddMinutes(-10), Assert.Single(fallback.Parameters).Value);
    }

    [Fact]
    public void PayloadColumns_FiveColumns_MatchSchemaOrder()
    {
        var names = DeadlocksCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();

        Assert.Equal(
            new[] { "deadlock_time", "victim_process_id", "victim_sql_text", "deadlock_graph_xml", "victim_query_plan_xml" },
            names);
    }

    [Fact]
    public void BuildQuery_PlanCaptureOffByDefault_NoResolution_LiteParity()
    {
        /* Lite never sets CapturePlanXml: no plan column, no victim frame shred — byte-identical to
           the no-plan form (server-scoped AND Azure). */
        var serverScoped = DeadlocksCollector.Instance.BuildQuery(MakeContext());
        var azure = DeadlocksCollector.Instance.BuildQuery(MakeContext(isAzureSqlDb: true));

        foreach (var text in new[] { serverScoped.Text, azure.Text })
        {
            Assert.DoesNotContain("query_plan", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dm_exec_text_query_plan", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/*DL_PLAN_SELECT*/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/*DL_PLAN_APPLY*/", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildQuery_PlanCaptureOn_ResolvesVictimFrameFromPlanCache()
    {
        /* Both variants share the fragment — the victim shred walks the identical
           data[@name="xml_report"]/value/deadlock payload and matches the victim by id (forward
           navigation only), then resolves via the proven #880 sql_handle + offsets → text-plan lookup. */
        foreach (var context in new[] { MakeContext(capturePlanXml: true), MakeContext(isAzureSqlDb: true, capturePlanXml: true) })
        {
            var plan = DeadlocksCollector.Instance.BuildQuery(context);

            Assert.Contains("victim_query_plan_xml = vqp.query_plan", plan.Text, StringComparison.Ordinal);
            Assert.Contains("victim-list/victimProcess/@id", plan.Text, StringComparison.Ordinal);
            Assert.Contains(
                "JOIN sys.dm_exec_query_stats AS qs\n      ON  qs.sql_handle = frames.frame_handle",
                plan.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("sys.dm_exec_text_query_plan", plan.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReadAsync_PlanCaptureOn_CapturesTrailingVictimPlanColumn()
    {
        var context = MakeContext(capturePlanXml: true);
        var deadlockTime = new DateTime(2026, 7, 2, 11, 58, 0, DateTimeKind.Utc);

        /* Flag on = the projection carries victim_query_plan_xml at ordinal 3. */
        using var reader = new FakeCollectorDataReader(
            new object[] { deadlockTime, "process123", SampleGraphXml, "<ShowPlanXML>victim</ShowPlanXML>" });
        var rows = await DeadlocksCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var writer = new RecordingCollectorRowWriter();
        DeadlocksCollector.Instance.WritePayload(Assert.Single(rows), writer, context);

        Assert.Equal(5, writer.Values.Count);
        Assert.Equal("<ShowPlanXML>victim</ShowPlanXML>", writer.Values[4]);
    }

    private const string SampleGraphXml = @"<deadlock>
 <victim-list><victimProcess id=""process123""/></victim-list>
 <process-list>
  <process id=""process123""><inputbuf> UPDATE t SET x = 1; </inputbuf></process>
  <process id=""process456""><inputbuf> SELECT 1; </inputbuf></process>
 </process-list>
</deadlock>";

    [Fact]
    public void ExtractVictimSqlText_MatchesVictim_FallsBackToFirstProcess_ToleratesGarbage()
    {
        Assert.Equal("UPDATE t SET x = 1;", DeadlocksCollector.ExtractVictimSqlText(SampleGraphXml, "process123"));
        Assert.Equal("SELECT 1;", DeadlocksCollector.ExtractVictimSqlText(SampleGraphXml, "PROCESS456")); /* case-insensitive id match */
        Assert.Equal("UPDATE t SET x = 1;", DeadlocksCollector.ExtractVictimSqlText(SampleGraphXml, "no-such-process"));
        Assert.Equal("UPDATE t SET x = 1;", DeadlocksCollector.ExtractVictimSqlText(SampleGraphXml, null));
        Assert.Null(DeadlocksCollector.ExtractVictimSqlText(null, "process123"));
        Assert.Null(DeadlocksCollector.ExtractVictimSqlText("", "process123"));
        Assert.Null(DeadlocksCollector.ExtractVictimSqlText("<not-xml", "process123"));
    }

    [Fact]
    public async Task ReadAsync_WritePayload_PinsFiveColumnOrder_AndVictimExtraction()
    {
        var context = MakeContext();
        var deadlockTime = new DateTime(2026, 7, 2, 11, 58, 0, DateTimeKind.Utc);

        using var reader = new FakeCollectorDataReader(
            new object[] { deadlockTime, "process123", SampleGraphXml },
            new object[] { DBNull.Value, DBNull.Value, DBNull.Value });
        var rows = await DeadlocksCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        Assert.Equal(2, rows.Count);

        var writer = new RecordingCollectorRowWriter();
        DeadlocksCollector.Instance.WritePayload(rows[0], writer, context);

        Assert.Equal(5, writer.Values.Count);
        Assert.Equal(deadlockTime, writer.Values[0]);
        Assert.Equal("process123", writer.Values[1]);
        Assert.Equal("UPDATE t SET x = 1;", writer.Values[2]);   /* extracted in the read phase */
        Assert.Equal(SampleGraphXml, writer.Values[3]);
        Assert.Null(writer.Values[4]);                           /* victim_query_plan_xml null when flag off */

        /* An all-NULL event row still writes (nothing filters it), exactly as the original. */
        var nullWriter = new RecordingCollectorRowWriter();
        DeadlocksCollector.Instance.WritePayload(rows[1], nullWriter, context);
        Assert.Equal(5, nullWriter.Values.Count);
        Assert.All(nullWriter.Values, Assert.Null);
        Assert.Empty(s_deltas.Calls);
    }
}
