/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the CPU-tab and tempdb-tab reads (W1a viewer copy-parity) against the Darling store contract,
/// no live Postgres. The CPU tab (and, since W1d, the Overview's CPU lane) plots RAW per-sample rows
/// (windowed on the naive-UTC collection_time, ordered by the server-local sample_time) — deliberately
/// not an average-per-collection roll-up. The tempdb reads mirror Lite's view-based queries
/// (v_tempdb_stats / v_file_io_stats): the numeric(18,2) MB columns are CAST to double precision for
/// the typed reader, total_sessions_using_tempdb stays bigint (GetInt64), and the file-I/O read filters
/// to tempdb and averages stall/op per file.
/// </summary>
public sealed class ViewerCpuTempDbSqlTests
{
    [Fact]
    public void CpuUtilizationSql_SelectsRawSamples_WindowedOnCollectionTime_OrderedBySampleTime()
    {
        Assert.Contains("FROM cpu_utilization_stats", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
        Assert.Contains("sample_time", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
        Assert.Contains("sqlserver_cpu_utilization", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
        Assert.Contains("other_process_cpu_utilization", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY sample_time", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuUtilizationSql_IsRawNotAveraged_NoAvgOrGroupBy()
    {
        /* The CPU tab (and the Overview's CPU lane) plots every ring-buffer sample; it must NOT
           average per collection. */
        Assert.DoesNotContain("AVG(", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY", ViewerDataService.CpuUtilizationSql, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuUtilizationSql_ReadsColumnsThatExistInTheGeneratedCpuTable()
    {
        Assert.Equal("cpu_utilization_stats", CpuUtilizationCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(CpuUtilizationCollector.Instance);
        foreach (var column in new[] { "sample_time", "sqlserver_cpu_utilization", "other_process_cpu_utilization" })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TempDbTrendSql_SelectsUsageColumns_CastsMbToDouble_OverTheWindow()
    {
        Assert.Contains("FROM v_tempdb_stats", ViewerDataService.TempDbTrendSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.TempDbTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.TempDbTrendSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time", ViewerDataService.TempDbTrendSql, StringComparison.Ordinal);

        /* Every numeric(18,2) MB column is CAST to double precision for the typed GetDouble reader. */
        foreach (var col in new[]
        {
            "user_object_reserved_mb", "internal_object_reserved_mb", "version_store_reserved_mb",
            "total_reserved_mb", "unallocated_mb", "top_session_tempdb_mb",
        })
        {
            Assert.Contains($"CAST({col} AS double precision)", ViewerDataService.TempDbTrendSql, StringComparison.Ordinal);
        }

        /* bigint session count + integer session id are selected raw (read with their own getters). */
        Assert.Contains("total_sessions_using_tempdb", ViewerDataService.TempDbTrendSql, StringComparison.Ordinal);
        Assert.Contains("top_session_id", ViewerDataService.TempDbTrendSql, StringComparison.Ordinal);
        /* The bigint session count must NOT be cast to double (it's read via GetInt64). */
        Assert.DoesNotContain("CAST(total_sessions_using_tempdb", ViewerDataService.TempDbTrendSql, StringComparison.Ordinal);
    }

    [Fact]
    public void TempDbTrendSql_ReadsColumnsThatExistInTheGeneratedTempDbTable()
    {
        Assert.Equal("tempdb_stats", TempDbStatsCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(TempDbStatsCollector.Instance);
        foreach (var column in new[]
        {
            "collection_time", "user_object_reserved_mb", "internal_object_reserved_mb",
            "version_store_reserved_mb", "total_reserved_mb", "unallocated_mb",
            "total_sessions_using_tempdb", "top_session_id", "top_session_tempdb_mb",
        })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TempDbFileIoTrendSql_FiltersToTempDb_GroupsPerFile_CastsStallToDouble()
    {
        Assert.Contains("FROM v_file_io_stats", ViewerDataService.TempDbFileIoTrendSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.TempDbFileIoTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.TempDbFileIoTrendSql, StringComparison.Ordinal);
        Assert.Contains("database_name = 'tempdb'", ViewerDataService.TempDbFileIoTrendSql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY collection_time, file_name", ViewerDataService.TempDbFileIoTrendSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time, file_name", ViewerDataService.TempDbFileIoTrendSql, StringComparison.Ordinal);
        Assert.Contains("CAST(delta_stall_read_ms AS double precision)", ViewerDataService.TempDbFileIoTrendSql, StringComparison.Ordinal);
        Assert.Contains("CAST(delta_stall_write_ms AS double precision)", ViewerDataService.TempDbFileIoTrendSql, StringComparison.Ordinal);
    }

    [Fact]
    public void TempDbFileIoTrendSql_ReadsColumnsThatExistInTheGeneratedFileIoTable()
    {
        Assert.Equal("file_io_stats", FileIoStatsCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(FileIoStatsCollector.Instance);
        foreach (var column in new[]
        {
            "collection_time", "database_name", "file_name",
            "delta_reads", "delta_writes", "delta_stall_read_ms", "delta_stall_write_ms",
        })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("tempdb")]
    [InlineData("fileio")]
    public void NewViewerReads_PgDialect_PositionalParams_NoBareNow_NoNLiterals(string which)
    {
        var sql = which switch
        {
            "cpu" => ViewerDataService.CpuUtilizationSql,
            "tempdb" => ViewerDataService.TempDbTrendSql,
            _ => ViewerDataService.TempDbFileIoTrendSql,
        };

        Assert.DoesNotContain("now(", sql.ToLowerInvariant());
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        Assert.Contains("$2", sql, StringComparison.Ordinal);
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the three new W1a reads: raw CPU samples (order +
/// NULL-other-as-0), the tempdb usage read (MB-as-double and — the load-bearing check — the bigint
/// total_sessions_using_tempdb read via GetInt64, which GetInt32 would throw on), and the tempdb
/// file-I/O read (tempdb-only filtering + per-file average-latency computation). Shares the serialized
/// "live-postgres" collection so the row churn can't race another class; uses negative sentinel
/// server_ids and cleans up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerCpuTempDbLivePostgresTests
{
    private const int CpuServerId = -949494;
    private const string CpuServerName = "viewer-cpu-tab-e2e";

    private const int TempDbServerId = -959595;
    private const string TempDbServerName = "viewer-tempdb-tab-e2e";

    private const int FileIoServerId = -969696;
    private const string FileIoServerName = "viewer-tempdb-fileio-e2e";

    [Fact]
    public async Task Cpu_ReadsRawSamplesInOrder_NullOtherAsZero_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live CPU-tab test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "cpu_utilization_stats", CpuServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var collUtc = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            var s1 = collUtc;
            var s2 = collUtc.AddMinutes(1);
            var s3 = collUtc.AddMinutes(2);

            /* Three raw samples in one collection; the middle one has NULL other-process CPU (SQL on
               Linux). Inserted out of sample_time order to prove the read's ORDER BY sample_time. */
            await InsertCpuAsync(connection, collUtc, s3, sqlCpu: 30, otherCpu: 15);
            await InsertCpuAsync(connection, collUtc, s1, sqlCpu: 10, otherCpu: 5);
            await InsertCpuAsync(connection, collUtc, s2, sqlCpu: 20, otherCpu: null);

            var samples = await viewer.GetCpuUtilizationAsync(CpuServerId, collUtc.AddMinutes(-1));

            Assert.Equal(3, samples.Count);
            /* Raw (not averaged), ordered by sample_time. */
            Assert.Equal(new[] { s1.Ticks, s2.Ticks, s3.Ticks }, samples.Select(s => s.SampleTime.Ticks));
            Assert.Equal(new[] { 10, 20, 30 }, samples.Select(s => s.SqlServerCpu));
            /* Middle sample's NULL other-process CPU reads as 0. */
            Assert.Equal(new[] { 5, 0, 15 }, samples.Select(s => s.OtherProcessCpu));
        }
        finally
        {
            await DeleteRowsAsync(connection, "cpu_utilization_stats", CpuServerId);
        }
    }

    [Fact]
    public async Task TempDb_ReadsMbAsDouble_AndBigintSessionCount_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live tempdb-usage test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "tempdb_stats", TempDbServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var t1 = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            var t2 = t1.AddMinutes(5);

            /* total_sessions deliberately exceeds int.MaxValue so a GetInt32 reader would throw —
               this is the read's load-bearing bigint check. */
            const long bigSessionCount = 5_000_000_000L;

            await InsertTempDbAsync(connection, t1,
                userMb: 100.25m, internalMb: 50.50m, versionMb: 10.75m,
                totalMb: 161.50m, unallocMb: 38.50m, totalSessions: 3, topSessionId: 55, topSessionMb: 12.00m);
            await InsertTempDbAsync(connection, t2,
                userMb: 200.00m, internalMb: 60.00m, versionMb: 20.00m,
                totalMb: 280.00m, unallocMb: 120.00m, totalSessions: bigSessionCount, topSessionId: 77, topSessionMb: 25.50m);

            var samples = await viewer.GetTempDbTrendAsync(TempDbServerId, t1.AddMinutes(-1));

            Assert.Equal(2, samples.Count);

            /* Ordered by collection_time; MB columns read as double. */
            Assert.Equal(t1.Ticks, samples[0].CollectionTime.Ticks);
            Assert.Equal(100.25, samples[0].UserObjectReservedMb, precision: 3);
            Assert.Equal(50.50, samples[0].InternalObjectReservedMb, precision: 3);
            Assert.Equal(10.75, samples[0].VersionStoreReservedMb, precision: 3);
            Assert.Equal(161.50, samples[0].TotalReservedMb, precision: 3);
            Assert.Equal(38.50, samples[0].UnallocatedMb, precision: 3);
            Assert.Equal(3L, samples[0].TotalSessionsUsingTempDb);

            /* The bigint session count round-trips through GetInt64. */
            Assert.Equal(t2.Ticks, samples[1].CollectionTime.Ticks);
            Assert.Equal(bigSessionCount, samples[1].TotalSessionsUsingTempDb);
            Assert.Equal(77, samples[1].TopSessionId);
            Assert.Equal(25.50, samples[1].TopSessionTempDbMb, precision: 3);
        }
        finally
        {
            await DeleteRowsAsync(connection, "tempdb_stats", TempDbServerId);
        }
    }

    [Fact]
    public async Task TempDbFileIo_TempDbOnly_PerFileAverageLatency_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live tempdb-file-I/O test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "file_io_stats", FileIoServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var t1 = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));

            /* Two tempdb files plus a user-database file that must be excluded by the WHERE clause. */
            await InsertFileIoAsync(connection, t1, "tempdb", "tempdb_data",
                deltaReads: 10, deltaWrites: 4, deltaStallReadMs: 100, deltaStallWriteMs: 20);
            await InsertFileIoAsync(connection, t1, "tempdb", "tempdb_log",
                deltaReads: 0, deltaWrites: 8, deltaStallReadMs: 0, deltaStallWriteMs: 80);
            await InsertFileIoAsync(connection, t1, "StackOverflow2010", "so_data",
                deltaReads: 1000, deltaWrites: 1000, deltaStallReadMs: 999999, deltaStallWriteMs: 999999);

            var rows = await viewer.GetTempDbFileIoTrendAsync(FileIoServerId, t1.AddMinutes(-1));

            /* Only the two tempdb files survive the database_name = 'tempdb' filter. */
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(t1.Ticks, r.CollectionTime.Ticks));
            /* Ordered by file_name. */
            Assert.Equal(new[] { "tempdb_data", "tempdb_log" }, rows.Select(r => r.FileName));

            /* tempdb_data: 100 stall / 10 reads = 10 ms read; 20 stall / 4 writes = 5 ms write. */
            Assert.Equal(10.0, rows[0].AvgReadLatencyMs, precision: 3);
            Assert.Equal(5.0, rows[0].AvgWriteLatencyMs, precision: 3);
            /* tempdb_log: 0 reads → CASE ELSE 0 read latency; 80 stall / 8 writes = 10 ms write. */
            Assert.Equal(0.0, rows[1].AvgReadLatencyMs, precision: 3);
            Assert.Equal(10.0, rows[1].AvgWriteLatencyMs, precision: 3);
        }
        finally
        {
            await DeleteRowsAsync(connection, "file_io_stats", FileIoServerId);
        }
    }

    private static async Task InsertCpuAsync(
        NpgsqlConnection connection, DateTime collectionTimeUtc, DateTime sampleTimeUtc, int sqlCpu, int? otherCpu)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO cpu_utilization_stats
    (collection_id, collection_time, server_id, server_name, sample_time,
     sqlserver_cpu_utilization, other_process_cpu_utilization)
VALUES ($1, $2, $3, $4, $5, $6, $7)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(CpuServerId);
        command.Parameters.AddWithValue(CpuServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(sampleTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(sqlCpu);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)otherCpu ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertTempDbAsync(
        NpgsqlConnection connection, DateTime collectionTimeUtc,
        decimal userMb, decimal internalMb, decimal versionMb, decimal totalMb, decimal unallocMb,
        long totalSessions, int topSessionId, decimal topSessionMb)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO tempdb_stats
    (collection_id, collection_time, server_id, server_name,
     user_object_reserved_mb, internal_object_reserved_mb, version_store_reserved_mb,
     total_reserved_mb, unallocated_mb, total_sessions_using_tempdb,
     top_session_id, top_session_tempdb_mb)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(TempDbServerId);
        command.Parameters.AddWithValue(TempDbServerName);
        command.Parameters.AddWithValue(userMb);
        command.Parameters.AddWithValue(internalMb);
        command.Parameters.AddWithValue(versionMb);
        command.Parameters.AddWithValue(totalMb);
        command.Parameters.AddWithValue(unallocMb);
        command.Parameters.AddWithValue(totalSessions);
        command.Parameters.AddWithValue(topSessionId);
        command.Parameters.AddWithValue(topSessionMb);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertFileIoAsync(
        NpgsqlConnection connection, DateTime collectionTimeUtc, string databaseName, string fileName,
        long deltaReads, long deltaWrites, long deltaStallReadMs, long deltaStallWriteMs)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO file_io_stats
    (collection_id, collection_time, server_id, server_name,
     database_name, file_name, delta_reads, delta_writes,
     delta_stall_read_ms, delta_stall_write_ms)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(FileIoServerId);
        command.Parameters.AddWithValue(FileIoServerName);
        command.Parameters.AddWithValue(databaseName);
        command.Parameters.AddWithValue(fileName);
        command.Parameters.AddWithValue(deltaReads);
        command.Parameters.AddWithValue(deltaWrites);
        command.Parameters.AddWithValue(deltaStallReadMs);
        command.Parameters.AddWithValue(deltaStallWriteMs);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static async Task DeleteRowsAsync(NpgsqlConnection connection, string table, int serverId)
    {
        using var cleanup = new NpgsqlCommand($"DELETE FROM {table} WHERE server_id = {serverId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
