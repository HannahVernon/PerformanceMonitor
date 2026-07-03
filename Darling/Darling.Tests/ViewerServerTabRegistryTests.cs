/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the per-server tab dedupe/close bookkeeping (headless-plan W0 IA inversion). The registry is
/// the WPF-free core of MainWindow's tab strip: a server gets at most one open tab, keyed by server id,
/// so double-clicking an already-open server focuses its tab instead of duplicating it — and closing a
/// tab drops it so the same server can be re-opened. The string payload here stands in for the WPF
/// TabItem MainWindow parameterizes the registry with.
/// </summary>
public sealed class ViewerServerTabRegistryTests
{
    /// <summary>Models MainWindow.OpenServerTab: TryGet first (focus existing), else create + Add.</summary>
    private static bool OpenTab(OpenServerTabRegistry<string> registry, int serverId, string tab)
    {
        if (registry.TryGet(serverId, out _))
        {
            return false; // focused an existing tab — no new tab created
        }

        registry.Add(serverId, tab);
        return true; // opened a new tab
    }

    [Fact]
    public void ReopeningTheSameServer_FocusesTheExistingTab_NoDuplicate()
    {
        var registry = new OpenServerTabRegistry<string>();

        Assert.True(OpenTab(registry, 5, "tab-A"));   // first open creates a tab
        Assert.False(OpenTab(registry, 5, "tab-A2"));  // second open focuses the existing one

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet(5, out var tab));
        Assert.Equal("tab-A", tab); // the original tab is kept, not the would-be duplicate
    }

    [Fact]
    public void DistinctServers_GetDistinctTabs()
    {
        var registry = new OpenServerTabRegistry<string>();

        Assert.True(OpenTab(registry, 5, "tab-A"));
        Assert.True(OpenTab(registry, 7, "tab-B"));

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet(5, out var five));
        Assert.True(registry.TryGet(7, out var seven));
        Assert.Equal("tab-A", five);
        Assert.Equal("tab-B", seven);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForAnUnopenedServer()
    {
        var registry = new OpenServerTabRegistry<string>();

        Assert.False(registry.TryGet(42, out var tab));
        Assert.Null(tab);
    }

    [Fact]
    public void Closing_DropsTheTab_AndAllowsReopen()
    {
        var registry = new OpenServerTabRegistry<string>();
        OpenTab(registry, 5, "tab-A");

        Assert.True(registry.Remove(5));   // close succeeds
        Assert.False(registry.Remove(5));  // closing again is a no-op
        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGet(5, out _));

        // The same server can be opened afresh after close.
        Assert.True(OpenTab(registry, 5, "tab-A-reopened"));
        Assert.True(registry.TryGet(5, out var tab));
        Assert.Equal("tab-A-reopened", tab);
    }
}
