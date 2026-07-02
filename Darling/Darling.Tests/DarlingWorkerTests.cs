/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
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
}
