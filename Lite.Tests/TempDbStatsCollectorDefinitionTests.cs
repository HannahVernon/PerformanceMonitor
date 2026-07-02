/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted tempdb_stats definition: two result sets collapse
/// to exactly one row (zeros when empty — matching the original collector), and the payload
/// order matches the tempdb_stats schema.
/// </summary>
public sealed class TempDbStatsCollectorDefinitionTests
{
    [Fact]
    public void PayloadColumns_MatchSchemaOrder()
    {
        var names = TempDbStatsCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "user_object_reserved_mb",
                "internal_object_reserved_mb",
                "version_store_reserved_mb",
                "total_reserved_mb",
                "unallocated_mb",
                "total_sessions_using_tempdb",
                "top_session_id",
                "top_session_tempdb_mb",
            },
            names);
    }

    [Fact]
    public void Query_TargetsBothTempDbDmvs()
    {
        Assert.Contains("tempdb.sys.dm_db_file_space_usage", TempDbStatsCollector.Instance.Query, System.StringComparison.Ordinal);
        Assert.Contains("sys.dm_db_session_space_usage", TempDbStatsCollector.Instance.Query, System.StringComparison.Ordinal);
        Assert.Equal("tempdb_stats", TempDbStatsCollector.Instance.Name);
        Assert.Equal("tempdb_stats", TempDbStatsCollector.Instance.TargetTable);
    }

    [Fact]
    public async Task ReadAsync_CombinesTwoResultSets_IntoOneRow()
    {
        using var reader = FakeCollectorDataReader.WithResultSets(
            new[] { new object[] { 1.5m, 2.5m, 3.5m, 7.5m, 10.0m } },
            new[] { new object[] { 55, 12.25m, 9L } });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await TempDbStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(new TempDbStatsCollector.Row(1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m), row);
    }

    [Fact]
    public async Task ReadAsync_EmptyResultSets_StillYieldsOneZeroRow()
    {
        using var reader = FakeCollectorDataReader.WithResultSets(
            System.Array.Empty<object[]>(),
            System.Array.Empty<object[]>());

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await TempDbStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(default(TempDbStatsCollector.Row), row);
    }

    [Fact]
    public void WritePayload_EmitsSchemaOrder_NoDeltas()
    {
        var deltas = new RecordingCollectorDeltaCalculator();
        var writer = new RecordingCollectorRowWriter();
        var row = new TempDbStatsCollector.Row(1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m);

        TempDbStatsCollector.Instance.WritePayload(row, writer, CollectorTestContext.Make(deltas));

        Assert.Equal(new object?[] { 1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m }, writer.Values);
        Assert.Empty(deltas.Calls);
    }
}
