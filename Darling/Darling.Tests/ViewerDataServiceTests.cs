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
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the viewer's SQL against the Darling store contract (no live Postgres needed) and
/// unit-tests the pure display helpers: the version label. (The naive-UTC → display conversion moved to
/// the mode-aware <c>ViewerTimeHelper</c>, pinned in <c>ViewerTimeHelperTests</c>; the Overview trend
/// reads + wait-category roll-up are pinned in <c>ViewerTrendsTests</c>.)
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

    /* The naive-UTC → display conversion moved from ViewerDataService.ToLocalTime to the mode-aware
       ViewerTimeHelper (Server/Local/UTC); it is pinned in ViewerTimeHelperTests. */

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
    public void Parse_ManagedMode_DefaultsToAdminRole_DerivesFromTheAdminCredential()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-viewer-managed-");
        try
        {
            /* The SERVICE provisions the least-privilege roles and writes their credentials; the
               VIEWER (default connectAs = admin) must derive the connection string for the admin
               role from the admin credential (V8 security split — no longer the darling superuser). */
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var adminCredential = PerformanceMonitor.Darling.Service.DarlingManagedPostgres.AdminCredentialPathFor(dataDirectory);
            File.WriteAllText(adminCredential, PerformanceMonitor.Darling.Service.DarlingSecrets.Protect("admin-pw"));

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
            Assert.Equal("admin", parsed.Username);
            Assert.Equal("admin-pw", parsed.Password);
            Assert.Equal("darling", parsed.Database);
            /* The bare table names resolve to the V8 collect/config schemas on every connection. */
            Assert.Equal("collect,config,public", parsed.SearchPath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Parse_ManagedMode_ConnectAsViewer_DerivesFromTheViewerCredential()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-viewer-ro-");
        try
        {
            /* A locked-down deployment (connectAs = "viewer") reads the read-only viewer role's
               credential and connects as that role — the write surfaces then degrade gracefully. */
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var viewerCredential = PerformanceMonitor.Darling.Service.DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);
            File.WriteAllText(viewerCredential, PerformanceMonitor.Darling.Service.DarlingSecrets.Protect("viewer-pw"));

            var json = $$"""
                {
                  "postgres": {
                    "managed": true,
                    "port": 5991,
                    "connectAs": "viewer",
                    "dataDirectory": {{JsonSerializer.Serialize(dataDirectory)}}
                  }
                }
                """;

            var settings = ViewerSettings.Parse(json);
            var parsed = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
            Assert.Equal("viewer", parsed.Username);
            Assert.Equal("viewer-pw", parsed.Password);
            Assert.Equal("collect,config,public", parsed.SearchPath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Parse_ManagedMode_UnknownConnectAs_FallsBackToAdmin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-viewer-unknown-");
        try
        {
            /* A typo'd connectAs degrades to the admin role (the DarlingConfig default), not a crash. */
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var adminCredential = PerformanceMonitor.Darling.Service.DarlingManagedPostgres.AdminCredentialPathFor(dataDirectory);
            File.WriteAllText(adminCredential, PerformanceMonitor.Darling.Service.DarlingSecrets.Protect("admin-pw"));

            var json = $$"""
                {
                  "postgres": {
                    "managed": true,
                    "connectAs": "superadmin",
                    "dataDirectory": {{JsonSerializer.Serialize(dataDirectory)}}
                  }
                }
                """;

            var parsed = new NpgsqlConnectionStringBuilder(ViewerSettings.Parse(json).ConnectionString);
            Assert.Equal("admin", parsed.Username);
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

/// <summary>
/// The viewer's read-only degradation (V8 security hardening): the authoritative capability probe
/// (has_table_privilege on a config table, resolved through search_path) and the friendly
/// read-only exception the write paths translate a Postgres 42501 into. The live admin-writes /
/// viewer-denied round-trip is in the gated <c>DarlingSecuritySplitLiveTests</c>.
/// </summary>
public sealed class ViewerReadOnlyTests
{
    [Fact]
    public void ReadOnlyProbeSql_TestsConfigInsertPrivilege()
    {
        /* has_table_privilege on config_mute_rules INSERT: true => writable (admin/owner), false =>
           read-only viewer. The bare name resolves to config.config_mute_rules via search_path. */
        Assert.Equal("SELECT has_table_privilege('config_mute_rules', 'INSERT')", ViewerDataService.ReadOnlyProbeSql);
    }

    [Fact]
    public async System.Threading.Tasks.Task IsReadOnly_DefaultsFalse_BeforeProbing()
    {
        /* Defaults writable until DetectReadOnlyAsync runs (which fails safe to read-only). The
           constructor only creates the pooled data source; no connection is opened here. */
        await using var service = new ViewerDataService("Host=localhost;Port=1;Database=darling;Timeout=1");
        Assert.False(service.IsReadOnly);
    }

    [Fact]
    public void ViewerReadOnlyException_ExplainsHowToEnableWrites()
    {
        var ex = new ViewerReadOnlyException(new InvalidOperationException("42501"));

        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connectAs", ex.Message, StringComparison.Ordinal);
        Assert.Equal("42501", ex.InnerException?.Message);
    }
}

/// <summary>
/// The connect-time schema-skew gate (finding B1): the viewer probes the store's effective version via
/// <c>information_schema</c> (the owner-only <c>darling_schema_version</c> table can't be read) and blocks a
/// store behind the version this build needs, instead of letting a control-plane write throw a raw 42703.
/// </summary>
public sealed class ViewerSchemaVersionGateTests
{
    [Fact]
    public void StoreSchemaProbeSql_ProbesInformationSchema_ForTheV17ToV19Sentinels()
    {
        var sql = ViewerDataService.StoreSchemaProbeSql;

        /* Reads information_schema (visible to every role) — not the owner-only darling_schema_version. */
        Assert.Contains("information_schema.tables", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.columns", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("darling_schema_version", sql, StringComparison.Ordinal);

        /* The three version sentinels: V17 config control plane, V18 delivery-override column, V19 marker. */
        Assert.Contains("config_monitored_servers", sql, StringComparison.Ordinal);
        Assert.Contains("alert_delivery_mode_override", sql, StringComparison.Ordinal);
        Assert.Contains("analysis_state", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true, 19)]   // fully migrated
    [InlineData(true, true, false, 18)]  // pre-V19: no analysis_state
    [InlineData(true, false, false, 17)] // pre-V18: no delivery-override column
    [InlineData(false, false, false, 16)] // pre-V17: no config control plane at all
    public void MapProbedSchemaVersion_TakesTheHighestSatisfiedSentinel(
        bool hasConfigControlPlane, bool hasAlertDeliveryOverride, bool hasAnalysisState, int expected)
    {
        Assert.Equal(expected, ViewerDataService.MapProbedSchemaVersion(
            hasConfigControlPlane, hasAlertDeliveryOverride, hasAnalysisState));
    }

    [Fact]
    public void RequiredStoreSchemaVersion_TracksTheBuildSchemaVersion_AndTheProbeCoversIt()
    {
        /* The gate compares against the build's known schema version. */
        Assert.Equal(StorageVersion.SchemaVersion, ViewerDataService.RequiredStoreSchemaVersion);

        /* Pin: a fully-migrated store (all sentinels present) must map to exactly the required version. If a
           future migration bumps StorageVersion past 19, this fails until a matching sentinel + map arm is
           added — the guard against the probe silently under-reporting a newer store as skewed. */
        Assert.Equal(
            ViewerDataService.RequiredStoreSchemaVersion,
            ViewerDataService.MapProbedSchemaVersion(true, true, true));
    }
}
