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
/// Pins the parity contract of the extracted server_properties definition: the vCore parse rules,
/// the Azure-only vCore application, the supplemental WS5 health probe (skipped on Azure,
/// merge-by-replacement, failure leaves NULLs), and the 22-column payload incl. the
/// enterprise_features placeholder Lite never collects and the utc_offset_minutes the viewer's
/// Server-time display mode reads.
/// </summary>
public sealed class ServerPropertiesCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    [Theory]
    [InlineData("HS_Gen5_14", 14)]
    [InlineData("GP_Gen5_6", 6)]
    [InlineData("BC_Gen5_8", 8)]
    [InlineData("GP_S_Gen5_2", 2)]
    [InlineData("P1", null)]
    [InlineData("S0", null)]
    [InlineData("ElasticPool", null)]
    public void ParseVcore_FollowsTierPattern(string serviceObjective, int? expected)
    {
        Assert.Equal(expected, ServerPropertiesCollector.ParseVcoreFromServiceObjective(serviceObjective));
    }

    [Fact]
    public void SupplementalQuery_SkippedOnAzure_PresentOnPrem()
    {
        Assert.Null(ServerPropertiesCollector.Instance.BuildSupplementalQuery(CollectorTestContext.Make(s_deltas, isAzureSqlDb: true)));

        var plan = ServerPropertiesCollector.Instance.BuildSupplementalQuery(CollectorTestContext.Make(s_deltas));
        Assert.NotNull(plan);
        Assert.Contains("sys.dm_server_services", plan!.Text, StringComparison.Ordinal);
        Assert.Contains("sys.dm_server_memory_dumps", plan.Text, StringComparison.Ordinal);
        Assert.Contains("sql_memory_model IN (2, 3)", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesAndColumns_MatchDispatchAndSchema()
    {
        Assert.Equal("server_properties", ServerPropertiesCollector.Instance.Name);
        Assert.Equal("server_properties", ServerPropertiesCollector.Instance.TargetTable);
        Assert.Equal(
            new[]
            {
                "edition", "product_version", "product_level", "product_update_level",
                "engine_edition", "cpu_count", "hyperthread_ratio", "physical_memory_mb",
                "socket_count", "cores_per_socket", "is_hadr_enabled", "is_clustered",
                "enterprise_features", "service_objective", "vcore_count",
                "lock_pages_in_memory", "instant_file_initialization_enabled", "memory_dump_count",
                "sqlserver_start_time", "host_os_version", "ag_replica_role", "utc_offset_minutes",
            },
            ServerPropertiesCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task ReadAsync_Azure_ParsesVcore_FromServiceObjective()
    {
        using var reader = new FakeCollectorDataReader(AzureRow("GP_Gen5_6"));

        var rows = await ServerPropertiesCollector.Instance.ReadAsync(
            reader, CollectorTestContext.Make(s_deltas, isAzureSqlDb: true), CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(6, row.VcoreCount);
        Assert.Equal("Azure SQL Database (General Purpose)", row.Edition);
        Assert.Null(row.LockPagesInMemory);
    }

    [Fact]
    public async Task ReadAsync_NonAzure_NeverParsesVcore_EvenWithObjective()
    {
        using var reader = new FakeCollectorDataReader(AzureRow("GP_Gen5_6"));

        var rows = await ServerPropertiesCollector.Instance.ReadAsync(
            reader, CollectorTestContext.Make(s_deltas, isAzureSqlDb: false), CancellationToken.None);

        Assert.Null(Assert.Single(rows).VcoreCount);
    }

    [Fact]
    public async Task ApplySupplemental_MergesHealthValues_AndToleratesEmpty()
    {
        var context = CollectorTestContext.Make(s_deltas);
        using var mainReader = new FakeCollectorDataReader(AzureRow(null));
        var rows = await ServerPropertiesCollector.Instance.ReadAsync(mainReader, context, CancellationToken.None);

        using var healthReader = new FakeCollectorDataReader(new object[] { true, false, 3 });
        await ServerPropertiesCollector.Instance.ApplySupplementalAsync(rows, healthReader, context, CancellationToken.None);

        Assert.True(rows[0].LockPagesInMemory);
        Assert.False(rows[0].InstantFileInitializationEnabled);
        Assert.Equal(3, rows[0].MemoryDumpCount);

        /* Empty supplemental reader: values stay as-is; empty rows: no-op. */
        using var emptyReader = new FakeCollectorDataReader();
        await ServerPropertiesCollector.Instance.ApplySupplementalAsync(rows, emptyReader, context, CancellationToken.None);
        Assert.True(rows[0].LockPagesInMemory);
        await ServerPropertiesCollector.Instance.ApplySupplementalAsync(new System.Collections.Generic.List<ServerPropertiesCollector.Row>(), emptyReader, context, CancellationToken.None);
    }

    [Fact]
    public async Task WritePayload_Emits22Columns_WithNullEnterpriseFeatures()
    {
        using var reader = new FakeCollectorDataReader(AzureRow("HS_Gen5_14"));
        var rows = await ServerPropertiesCollector.Instance.ReadAsync(
            reader, CollectorTestContext.Make(s_deltas, isAzureSqlDb: true), CancellationToken.None);

        var writer = new RecordingCollectorRowWriter();
        ServerPropertiesCollector.Instance.WritePayload(rows[0], writer, CollectorTestContext.Make(s_deltas));

        Assert.Equal(22, writer.Values.Count);
        Assert.Equal("Azure SQL Database (General Purpose)", writer.Values[0]);
        Assert.Null(writer.Values[12]);           /* enterprise_features — never collected in Lite */
        Assert.Equal("HS_Gen5_14", writer.Values[13]);
        Assert.Equal(14, writer.Values[14]);
        /* The three inventory columns appended in v36 (#1372). */
        Assert.Equal(new DateTime(2026, 6, 1), writer.Values[18]);
        Assert.Equal("Windows Server 2022", writer.Values[19]);
        Assert.Null(writer.Values[20]);           /* ag_replica_role — standalone in the fixture */
        Assert.Equal(-300, writer.Values[21]);    /* utc_offset_minutes — the viewer's Server-time offset */
    }

    /// <summary>18-column main-query row; index 0 (server_name) is read past by the definition.</summary>
    private static object[] AzureRow(string? serviceObjective) => new object[]
    {
        "myserver", "Azure SQL Database (General Purpose)", "12.0.2000.8", "RTM", DBNull.Value,
        5, 80, 8, 415800L, DBNull.Value, DBNull.Value, false, false,
        serviceObjective is null ? DBNull.Value : serviceObjective,
        /* v36 inventory columns (#1372): sqlserver_start_time(14), host_os_version(15), ag_replica_role(16).
           v42 (#1409): utc_offset_minutes(17). */
        new DateTime(2026, 6, 1), "Windows Server 2022", DBNull.Value, -300,
    };
}
