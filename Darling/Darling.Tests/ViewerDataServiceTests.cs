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
using System.Text.Json;
using System.Windows.Media;
using Npgsql;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the viewer's SQL against the Darling store contract (no live Postgres needed) and
/// unit-tests the pure display helpers: the version label and the naive-UTC-to-local conversion.
/// (The Overview trend reads + wait-category roll-up are pinned in <c>ViewerTrendsTests</c>.)
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

    /* The Collection Health SQL is no longer the shell's DISTINCT-ON latest-run placeholder; W1i
       replaced it with Lite's 7-day aggregate, pinned in ViewerDailyHealthTests along with the Daily
       Summary reads. */

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

    /* The old placeholder CollectorHealthRow record (a single latest-run snapshot with
       CollectionTimeLocal) was replaced by Lite's rich 7-day aggregate class in W1i; its HealthStatus
       banding + formatting are pinned in ViewerDailyHealthTests. */
}

/// <summary>
/// The viewer's sliver of darling.json: the service's resolution order and lenient JSON
/// (comments, trailing commas, case-insensitive names), but only the postgres section —
/// including the managed bundled-Postgres mode, whose derivation (credential path convention +
/// DPAPI entropy) is pinned against the SERVICE's DarlingSecrets/DarlingManagedPostgres here,
/// because the viewer deliberately duplicates those constants instead of referencing the
/// service project.
/// </summary>
public sealed class ViewerSettingsTests
{
    [Fact]
    public void Parse_ManagedMode_DerivesFromTheServiceCredential()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-viewer-managed-");
        try
        {
            /* The SERVICE writes the credential (DarlingSecrets + the DarlingManagedPostgres
               path convention); the VIEWER must derive the identical connection string. */
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var credentialPath = PerformanceMonitor.Darling.Service.DarlingManagedPostgres.CredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, PerformanceMonitor.Darling.Service.DarlingSecrets.Protect("pw-from-service"));

            var json = $$"""
                {
                  "postgres": {
                    "managed": true,
                    "port": 5991,
                    "dataDirectory": {{JsonSerializer.Serialize(dataDirectory)}}
                  }
                }
                """;

            var settings = ViewerSettings.Parse(json);
            var parsed = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
            Assert.Equal("localhost", parsed.Host);
            Assert.Equal(5991, parsed.Port);
            Assert.Equal("darling", parsed.Username);
            Assert.Equal("pw-from-service", parsed.Password);
            Assert.Equal("darling", parsed.Database);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Parse_ManagedMode_MissingCredential_ThrowsAReadableFirstRunHint()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "pg");
        var json = $$"""{ "postgres": { "managed": true, "dataDirectory": {{JsonSerializer.Serialize(missing)}} } }""";

        var ex = Assert.Throws<InvalidDataException>(() => ViewerSettings.Parse(json));
        Assert.Contains("service", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>
    /// The release-zip layout: viewer\ under the service root, darling.json beside the SERVICE
    /// exe. Beside-binary wins when present; the parent probe covers the shipped layout; when
    /// neither exists the beside-binary path comes back so the not-found hint names the
    /// viewer's own directory.
    /// </summary>
    [Fact]
    public void ResolveConfigPath_PackagedLayout_FallsBackToParentDirectory()
    {
        var root = Directory.CreateTempSubdirectory("darling-viewer-resolve-");
        try
        {
            var viewerDirectory = Path.Combine(root.FullName, "viewer");
            Directory.CreateDirectory(viewerDirectory);
            var missing = Path.Combine(viewerDirectory, "darling.json");

            /* Neither location has a config: report the viewer's own (missing) path. */
            Assert.Equal(missing, ViewerSettings.ResolveConfigPath(baseDirectory: viewerDirectory));

            /* Only the service root has one — the shipped-zip case. */
            var atServiceRoot = Path.Combine(root.FullName, "darling.json");
            File.WriteAllText(atServiceRoot, "{}");
            Assert.Equal(atServiceRoot, ViewerSettings.ResolveConfigPath(baseDirectory: viewerDirectory));

            /* Beside the viewer binary still wins over the parent when both exist. */
            File.WriteAllText(missing, "{}");
            Assert.Equal(missing, ViewerSettings.ResolveConfigPath(baseDirectory: viewerDirectory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
