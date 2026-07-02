/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted index_object_stats definition: dual execution
/// (Azure per-database connections; on-prem enumeration + quote-doubled [db].sys.sp_executesql
/// body), the dedicated 300 s per-database budget (#1135), and the 44-column payload.
/// </summary>
public sealed class IndexObjectStatsCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    [Fact]
    public void DualExecution_AzurePerDatabase_OnPremEnumeration()
    {
        Assert.True(IndexObjectStatsCollector.Instance.RunsPerDatabase(new CollectorTargetInfo { IsAzureSqlDb = true }));
        Assert.False(IndexObjectStatsCollector.Instance.RunsPerDatabase(new CollectorTargetInfo()));

        Assert.Null(IndexObjectStatsCollector.Instance.BuildEnumerationQuery(CollectorTestContext.Make(s_deltas, isAzureSqlDb: true)));
        var enumeration = IndexObjectStatsCollector.Instance.BuildEnumerationQuery(CollectorTestContext.Make(s_deltas));
        Assert.NotNull(enumeration);
        Assert.Contains("HAS_DBACCESS(d.name) = 1", enumeration!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeoutOverride_IsTheDedicated1135Budget()
    {
        Assert.Equal(300, IndexObjectStatsCollector.Instance.CommandTimeoutSecondsOverride);
    }

    [Fact]
    public void PerItemQuery_QuoteDoublesTheBody_AndEscapesBrackets()
    {
        var plan = IndexObjectStatsCollector.Instance.BuildPerItemQuery("we]rd db", CollectorTestContext.Make(s_deltas));

        Assert.StartsWith("EXECUTE [we]]rd db].sys.sp_executesql N'", plan.Text, StringComparison.Ordinal);
        /* The body's own literals must arrive quote-doubled inside the N'...' wrapper. */
        Assert.Contains("o.type IN (N''U'', N''V'')", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("o.type IN (N'U', N'V')", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureQuery_IsTheRawStagedBody()
    {
        var plan = IndexObjectStatsCollector.Instance.BuildQuery(CollectorTestContext.Make(s_deltas, isAzureSqlDb: true));

        Assert.Contains("INTO #sizes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL)", plan.Text, StringComparison.Ordinal);
        Assert.Contains("o.type IN (N'U', N'V')", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadColumns_MatchSchemaOrder_44Columns()
    {
        var names = IndexObjectStatsCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();
        Assert.Equal(44, names.Length);
        Assert.Equal("sqlserver_start_time", names[0]);
        Assert.Equal("page_io_latch_wait_in_ms", names[43]);
    }

    [Fact]
    public async Task ReadItem_AndWrite_RoundTrip_WithBitConversions()
    {
        var start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var row44 = new object[44];
        row44[0] = start; row44[1] = "SO"; row44[2] = 7; row44[3] = "dbo"; row44[4] = 12345;
        row44[5] = "Posts"; row44[6] = 1; row44[7] = "PK_Posts"; row44[8] = "CLUSTERED";
        row44[9] = 1; row44[10] = 1; row44[11] = 0; row44[12] = 4;
        row44[13] = 1024.50m; row44[14] = 1000.25m; row44[15] = 900.00m; row44[16] = 50.00m; row44[17] = 25.00m;
        for (int i = 18; i < 44; i++) row44[i] = (long)(i * 10);
        /* last_user_* are timestamps at 23..26 */
        row44[23] = start; row44[24] = DBNull.Value; row44[25] = start; row44[26] = DBNull.Value;

        var rows = new List<IndexObjectStatsCollector.Row>();
        using (var reader = new FakeCollectorDataReader(row44))
        {
            await IndexObjectStatsCollector.Instance.ReadItemAsync("SO", reader, rows, CollectorTestContext.Make(s_deltas), CancellationToken.None);
        }

        var row = Assert.Single(rows);
        Assert.True(row.IsUnique);
        Assert.False(row.IsFiltered);
        Assert.Null(row.LastUserScan);

        var writer = new RecordingCollectorRowWriter();
        IndexObjectStatsCollector.Instance.WritePayload(row, writer, CollectorTestContext.Make(s_deltas));
        Assert.Equal(44, writer.Values.Count);
        Assert.Equal(start, writer.Values[0]);
        Assert.Equal(true, writer.Values[9]);
        Assert.Equal(430L, writer.Values[43]);
    }
}
