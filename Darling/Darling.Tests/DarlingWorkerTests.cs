/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// M2 slice D: the collection loop's dispatch must cover every collector in the shared catalog
/// and every scheduled default — a new definition that isn't wired into the worker fails here,
/// not silently in production.
/// </summary>
public sealed class DarlingWorkerTests
{
    [Fact]
    public void Dispatch_CoversEveryCatalogCollector_AndEveryScheduleDefault()
    {
        var dispatched = DarlingWorker.DispatchedCollectorNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in CollectorCatalog.All)
        {
            Assert.True(dispatched.Contains(definition.Name), $"worker dispatch missing catalog collector '{definition.Name}'");
        }

        foreach (var name in CollectorScheduleDefaults.All.Keys)
        {
            Assert.True(dispatched.Contains(name), $"worker dispatch missing scheduled collector '{name}'");
        }

        Assert.Equal(CollectorCatalog.All.Count, dispatched.Count);
    }

    /// <summary>
    /// A bring-your-own store connection string that omits the search path must get the canonical
    /// collect/config/public one injected BEFORE the data source is created — otherwise the Npgsql
    /// pool's first physical connections keep a pre-migration session search_path and a fresh BYO
    /// store silently collects nothing (42P01) until the service restarts. The injected path pins
    /// against the managed constant so both modes carry the same schemas in the same order.
    /// </summary>
    [Fact]
    public void EnsureStoreSearchPath_InjectsCanonicalPath_WhenByoStringOmitsIt()
    {
        const string byo = "Host=localhost;Port=5432;Username=darling;Database=darling";
        Assert.Empty(new NpgsqlConnectionStringBuilder(byo).SearchPath ?? string.Empty);

        var result = DarlingWorker.EnsureStoreSearchPath(byo);

        var parsed = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal("collect,config,public", parsed.SearchPath);
        Assert.Equal(DarlingManagedPostgres.SearchPath, parsed.SearchPath);

        /* The rest of the connection string is preserved. */
        Assert.Equal("localhost", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("darling", parsed.Username);
        Assert.Equal("darling", parsed.Database);
    }

    /// <summary>
    /// The Stage 2 pause gate: the collection sweep runs only when NOT paused. This pins the gate the loop
    /// keys off (config_service.paused -> _paused -> skip collection/alert/analysis/purge) so a future edit
    /// can't silently invert or drop it; the command loop keeps running while paused so a resume is honored.
    /// </summary>
    [Fact]
    public void ShouldRunCollection_SkipsOnlyWhenPaused()
    {
        Assert.True(DarlingWorker.ShouldRunCollection(paused: false));
        Assert.False(DarlingWorker.ShouldRunCollection(paused: true));
    }

    /// <summary>
    /// A connection string that ALREADY specifies a Search Path (managed mode carries it, and a BYO
    /// operator may set their own) is returned untouched — no double-set, and a non-default choice
    /// is respected.
    /// </summary>
    [Fact]
    public void EnsureStoreSearchPath_LeavesExistingSearchPathUnchanged()
    {
        /* Managed-mode shape: already carries collect,config,public. */
        var managed = DarlingManagedPostgres.BuildConnectionString(5641, "pw123");
        Assert.Equal(managed, DarlingWorker.EnsureStoreSearchPath(managed));

        /* A BYO operator's own non-default choice is respected, not overwritten. */
        const string custom = "Host=pg.example.com;Database=metrics;Search Path=collect,config,reporting,public";
        var result = DarlingWorker.EnsureStoreSearchPath(custom);
        Assert.Equal("collect,config,reporting,public", new NpgsqlConnectionStringBuilder(result).SearchPath);
    }
}
