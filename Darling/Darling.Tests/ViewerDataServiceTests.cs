/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the viewer's SQL against the Darling store contract (no live Postgres needed) and
/// unit-tests the pure display helpers: the wait-series pivot, the version label, and the
/// naive-UTC-to-local conversion.
/// </summary>
public sealed class ViewerDataServiceTests
{
    [Fact]
    public void ServersSql_ReadsTheServerListOrderedByDisplayName()
    {
        Assert.Contains("FROM servers", ViewerDataService.ServersSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY display_name", ViewerDataService.ServersSql, StringComparison.Ordinal);
        Assert.Contains("server_id, server_name, display_name, is_enabled, sql_major_version", ViewerDataService.ServersSql, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionHealthSql_TakesTheLatestRunPerCollector()
    {
        Assert.Contains("DISTINCT ON (collector_name)", ViewerDataService.CollectionHealthSql, StringComparison.Ordinal);
        Assert.Contains("FROM collection_log", ViewerDataService.CollectionHealthSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.CollectionHealthSql, StringComparison.Ordinal);
        /* DISTINCT ON keeps the FIRST row per collector, so the time sort must be descending. */
        Assert.Contains("ORDER BY collector_name, collection_time DESC", ViewerDataService.CollectionHealthSql, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitTrendSql_LimitsToTheTop8WaitTypesBySummedDelta()
    {
        Assert.Contains("FROM wait_stats", ViewerDataService.WaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.WaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY SUM(delta_wait_time_ms) DESC", ViewerDataService.WaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 8", ViewerDataService.WaitTrendSql, StringComparison.Ordinal);
        /* The pivot relies on rows arriving grouped by wait type in time order. */
        Assert.Contains("ORDER BY wait_type, collection_time", ViewerDataService.WaitTrendSql, StringComparison.Ordinal);
    }

    [Fact]
    public void PivotWaitSeries_GroupsByWaitType_PreservingFirstSeenOrder()
    {
        var t0 = new DateTime(2026, 7, 1, 10, 0, 0);
        var t1 = t0.AddMinutes(1);

        var points = new[]
        {
            new WaitTrendPoint(t0, "CXPACKET", 100),
            new WaitTrendPoint(t1, "CXPACKET", 150),
            new WaitTrendPoint(t0, "PAGEIOLATCH_SH", 40),
            new WaitTrendPoint(t1, "PAGEIOLATCH_SH", 60),
        };

        var series = ViewerDataService.PivotWaitSeries(points);

        Assert.Equal(2, series.Count);
        Assert.Equal("CXPACKET", series[0].WaitType);
        Assert.Equal("PAGEIOLATCH_SH", series[1].WaitType);
        Assert.Equal(new[] { t0, t1 }, series[0].Times);
        Assert.Equal(new[] { 100.0, 150.0 }, series[0].Values);
        Assert.Equal(new[] { 40.0, 60.0 }, series[1].Values);
    }

    [Fact]
    public void PivotWaitSeries_EmptyInput_YieldsNoSeries()
    {
        Assert.Empty(ViewerDataService.PivotWaitSeries(Array.Empty<WaitTrendPoint>()));
    }

    [Theory]
    [InlineData(13, "SQL Server 2016")]
    [InlineData(14, "SQL Server 2017")]
    [InlineData(15, "SQL Server 2019")]
    [InlineData(16, "SQL Server 2022")]
    [InlineData(17, "SQL Server 2025")]
    [InlineData(99, "SQL Server v99")]
    [InlineData(null, "")]
    public void SqlVersionLabel_MapsKnownMajors_AndFallsBack(int? major, string expected)
    {
        Assert.Equal(expected, ViewerDataService.SqlVersionLabel(major));
    }

    [Fact]
    public void ToLocalTime_TreatsTheNaiveValueAsUtc()
    {
        var naive = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var local = ViewerDataService.ToLocalTime(naive);

        Assert.Equal(DateTimeKind.Local, local.Kind);
        /* Round-tripping back to UTC recovers the stored value, whatever the machine's zone. */
        Assert.Equal(naive.Ticks, local.ToUniversalTime().Ticks);
    }

    [Fact]
    public void DarlingServer_VersionLabel_ComesFromTheMajorVersion()
    {
        var server = new DarlingServer(1, "SQL2022", "SQL2022", true, 16);
        Assert.Equal("SQL Server 2022", server.VersionLabel);
    }

    [Fact]
    public void CollectorHealthRow_CollectionTimeLocal_RoundTripsToTheStoredUtcValue()
    {
        var storedUtc = new DateTime(2026, 7, 1, 3, 30, 0, DateTimeKind.Unspecified);
        var row = new CollectorHealthRow("wait_stats", storedUtc, "SUCCESS", 42, 17, null);

        Assert.Equal(storedUtc.Ticks, row.CollectionTimeLocal.ToUniversalTime().Ticks);
    }
}

/// <summary>
/// The viewer's sliver of darling.json: the service's resolution order and lenient JSON
/// (comments, trailing commas, case-insensitive names), but only postgres.connectionString.
/// </summary>
public sealed class ViewerSettingsTests
{
    [Fact]
    public void Parse_ReadsTheConnectionString_WithCommentsAndTrailingCommas()
    {
        var json = """
            {
              // the same file the service reads
              "Postgres": {
                "ConnectionString": "Host=localhost;Database=darling",
              },
              "servers": [ { "name": "SQL2022", "host": "SQL2022" } ],
            }
            """;

        var settings = ViewerSettings.Parse(json);

        Assert.Equal("Host=localhost;Database=darling", settings.ConnectionString);
    }

    [Fact]
    public void Parse_MissingConnectionString_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ViewerSettings.Parse("{}"));
        Assert.Throws<InvalidDataException>(() => ViewerSettings.Parse("""{ "postgres": { "connectionString": "" } }"""));
    }

    [Fact]
    public void ResolveConfigPath_ExplicitPathWins()
    {
        Assert.Equal(@"C:\somewhere\darling.json", ViewerSettings.ResolveConfigPath(@"C:\somewhere\darling.json"));
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        Assert.Null(ViewerSettings.TryLoad(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "darling.json")));
    }
}

/// <summary>Status colors: SUCCESS green, PERMISSIONS orange, ERROR red, anything else default.</summary>
public sealed class StatusToBrushConverterTests
{
    private static Color ColorFor(string? status)
    {
        var converter = new StatusToBrushConverter();
        var brush = Assert.IsType<SolidColorBrush>(converter.Convert(status, typeof(Brush), null, CultureInfo.InvariantCulture));
        return brush.Color;
    }

    [Fact]
    public void Convert_MapsTheThreeStatuses_ToDistinctColors_CaseInsensitively()
    {
        var success = ColorFor("SUCCESS");
        var permissions = ColorFor("PERMISSIONS");
        var error = ColorFor("ERROR");
        var other = ColorFor("SKIPPED");

        Assert.NotEqual(success, permissions);
        Assert.NotEqual(success, error);
        Assert.NotEqual(permissions, error);
        Assert.NotEqual(success, other);

        Assert.Equal(success, ColorFor("success"));
        Assert.Equal(error, ColorFor("Error"));
        Assert.Equal(other, ColorFor(null));
    }
}
