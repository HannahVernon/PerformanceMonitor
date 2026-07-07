/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the V20 alert-tuning knobs the service now honors through the store instead of hardcoding them: the
/// long-running-query read shape (row cap + the five noise-filter opt-outs the shared <c>AlertEngine</c>
/// forwards to <c>GetLongRunningQueriesAsync</c>) and the connection-change notify gate. The pure tests need no
/// Postgres — <see cref="DarlingAlertSettings"/> exposes them through the by-reference config seam, so a store
/// reload (<see cref="StoreConfigProvider.ApplyToConfig"/>) is reflected on the same adapter instance — exactly
/// the V18 delivery-mode pattern. The live test (gated on DARLING_TEST_PG) round-trips the new columns through a
/// real seed/read against an ISOLATED scratch store.
/// </summary>
public sealed class DarlingAlertTuningKnobsTests
{
    /* ---------------- pure: the long-running-query read shape through the settings seam ---------------- */

    [Fact]
    public void DarlingAlertSettings_ReadsLongRunningQueryReadShape_ThroughTheByReferenceSeam()
    {
        var config = new DarlingConfig();
        var settings = new DarlingAlertSettings(config);

        /* Defaults mirror the V20 DDL (and Lite's App.*): 5 rows, every noise filter on. */
        Assert.Equal(5, settings.LongRunningQueryMaxResults);
        Assert.True(settings.LongRunningQueryExcludeSpServerDiagnostics);
        Assert.True(settings.LongRunningQueryExcludeWaitFor);
        Assert.True(settings.LongRunningQueryExcludeBackups);
        Assert.True(settings.LongRunningQueryExcludeMiscWaits);
        Assert.True(settings.LongRunningQueryExcludeCdc);

        /* A store reload mutates the held config in place; the SAME adapter instance reflects it (no caching). */
        config.Alerts.LongRunningQueryMaxResults = 25;
        config.Alerts.LongRunningQueryExcludeSpServerDiagnostics = false;
        config.Alerts.LongRunningQueryExcludeWaitFor = false;
        config.Alerts.LongRunningQueryExcludeBackups = false;
        config.Alerts.LongRunningQueryExcludeMiscWaits = false;
        config.Alerts.LongRunningQueryExcludeCdc = false;

        Assert.Equal(25, settings.LongRunningQueryMaxResults);
        Assert.False(settings.LongRunningQueryExcludeSpServerDiagnostics);
        Assert.False(settings.LongRunningQueryExcludeWaitFor);
        Assert.False(settings.LongRunningQueryExcludeBackups);
        Assert.False(settings.LongRunningQueryExcludeMiscWaits);
        Assert.False(settings.LongRunningQueryExcludeCdc);
    }

    [Fact]
    public void ApplyToConfig_SwapsLongRunningQueryReadShape_ReflectedThroughTheSettingsSeam()
    {
        var config = new DarlingConfig();
        var settings = new DarlingAlertSettings(config);

        StoreConfigProvider.ApplyToConfig(config, new StoreConfigView
        {
            Alerts = new AlertsConfig
            {
                LongRunningQueryMaxResults = 42,
                LongRunningQueryExcludeSpServerDiagnostics = false,
                LongRunningQueryExcludeCdc = false,
            },
        });

        Assert.Equal(42, settings.LongRunningQueryMaxResults);
        Assert.False(settings.LongRunningQueryExcludeSpServerDiagnostics);
        Assert.False(settings.LongRunningQueryExcludeCdc);
        /* The unset filters keep the AlertsConfig defaults (true). */
        Assert.True(settings.LongRunningQueryExcludeWaitFor);
        Assert.True(settings.LongRunningQueryExcludeBackups);
        Assert.True(settings.LongRunningQueryExcludeMiscWaits);
    }

    /* ---------------- live (DARLING_TEST_PG): seed -> read round-trip of the V20 columns ---------------- */

    [Fact]
    public async Task SeedAndRead_RoundTripsLongRunningQueryReadShape_AgainstScratchPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to an ISOLATED scratch Postgres (it seeds the singleton config rows) to run the seed/read round-trip.");

        var ct = TestContext.Current.CancellationToken;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await PgMigrations.MigrateAsync(connection, ct); // brings the scratch store to V20
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var provider = new StoreConfigProvider(dataSource);

        var config = new DarlingConfig();
        config.Alerts.LongRunningQueryMaxResults = 17;
        config.Alerts.LongRunningQueryExcludeSpServerDiagnostics = false;
        config.Alerts.LongRunningQueryExcludeWaitFor = false;
        config.Alerts.LongRunningQueryExcludeBackups = true;
        config.Alerts.LongRunningQueryExcludeMiscWaits = false;
        config.Alerts.LongRunningQueryExcludeCdc = true;
        config.Servers.Add(new MonitoredServer { Name = "v20-lrq", Host = "v20-scratch-host", Auth = "integrated" });

        await provider.SeedIfEmptyAsync(config, ct);

        var view = await provider.LoadViewAsync(new DarlingConfig(), ct);
        Assert.NotNull(view);

        Assert.Equal(17, view!.Alerts.LongRunningQueryMaxResults);
        Assert.False(view.Alerts.LongRunningQueryExcludeSpServerDiagnostics);
        Assert.False(view.Alerts.LongRunningQueryExcludeWaitFor);
        Assert.True(view.Alerts.LongRunningQueryExcludeBackups);
        Assert.False(view.Alerts.LongRunningQueryExcludeMiscWaits);
        Assert.True(view.Alerts.LongRunningQueryExcludeCdc);
    }
}
