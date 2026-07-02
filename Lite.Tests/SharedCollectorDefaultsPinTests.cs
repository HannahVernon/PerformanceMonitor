/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Services;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Identity-pins between Lite's own defaults and the shared constants the Darling service
/// consumes (headless plan M2 slice A): the bundled ignored-wait-types json must equal
/// <see cref="IgnoredWaitDefaults"/>, ScheduleManager's default table must equal
/// <see cref="CollectorScheduleDefaults"/>, and the delta calculator / server-identity idioms
/// must be the shared implementations — so the two SKUs cannot drift apart silently.
/// </summary>
public sealed class SharedCollectorDefaultsPinTests
{
    private static string FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }

    [Fact]
    public void IgnoredWaitDefaults_MatchLiteBundledJson()
    {
        var jsonPath = FindRepoFile(Path.Combine("Lite", "config", "ignored_wait_types.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var fromJson = doc.RootElement.GetProperty("ignored_waits")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(124, fromJson.Count);
        Assert.Equal(fromJson, IgnoredWaitDefaults.All.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void CollectorScheduleDefaults_MatchScheduleManagerTable()
    {
        var liteDefaults = ScheduleManager.GetDefaultSchedules();

        Assert.Equal(liteDefaults.Count, CollectorScheduleDefaults.All.Count);
        foreach (var schedule in liteDefaults)
        {
            Assert.True(CollectorScheduleDefaults.All.TryGetValue(schedule.Name, out var shared),
                $"shared defaults missing collector '{schedule.Name}'");
            Assert.Equal(schedule.FrequencyMinutes, shared!.FrequencyMinutes);
            Assert.Equal(schedule.RetentionDays, shared.RetentionDays);
        }
    }

    [Fact]
    public void CollectorScheduleDefaults_CoverEveryCatalogCollector()
    {
        foreach (var definition in CollectorCatalog.All)
        {
            Assert.True(CollectorScheduleDefaults.All.ContainsKey(definition.Name),
                $"no default schedule for catalog collector '{definition.Name}'");
        }
    }

    [Fact]
    public void DeltaCalculator_IsTheSharedCoreImplementation()
    {
        Assert.True(typeof(CollectorDeltaCalculator).IsAssignableFrom(typeof(DeltaCalculator)));

        /* Behavior smoke on the shared core: baseline 0, then delta, counter-reset 0, gap 0. */
        var deltas = new CollectorDeltaCalculator();
        var t0 = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, deltas.CalculateDelta(1, "c", "k", 100, t0, 300));
        Assert.Equal(50, deltas.CalculateDelta(1, "c", "k", 150, t0.AddSeconds(60), 300));
        Assert.Equal(0, deltas.CalculateDelta(1, "c", "k", 10, t0.AddSeconds(120), 300));   /* reset */
        Assert.Equal(0, deltas.CalculateDelta(1, "c", "k", 500, t0.AddSeconds(120 + 3600), 300)); /* gap */
    }

    [Fact]
    public void ServerStorageName_ForwardsToSharedBuilder()
    {
        Assert.Equal("HOST", PerformanceMonitor.Common.ServerIdHelper.BuildStorageName("HOST", null, false));
        Assert.Equal("HOST:db1", PerformanceMonitor.Common.ServerIdHelper.BuildStorageName("HOST", "db1", false));
        Assert.Equal("HOST:db1:RO", PerformanceMonitor.Common.ServerIdHelper.BuildStorageName("HOST", "db1", true));
        Assert.Equal("HOST:RO", PerformanceMonitor.Common.ServerIdHelper.BuildStorageName("HOST", "", true));
    }
}
