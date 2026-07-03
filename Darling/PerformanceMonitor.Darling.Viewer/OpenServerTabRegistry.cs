/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Tracks the open per-server tabs, keyed by server id, so re-opening a server focuses its existing
/// tab instead of duplicating it (Lite's dedupe-by-id rule). Generic over the tab payload and free of
/// any WPF dependency, so the dedupe/close bookkeeping is unit-testable; MainWindow parameterizes it
/// with <see cref="System.Windows.Controls.TabItem"/>. Keying by server id (not display name) means a
/// server whose display name later changes still resolves to its one open tab.
/// </summary>
internal sealed class OpenServerTabRegistry<TTab>
{
    private readonly Dictionary<int, TTab> _tabs = new();

    /// <summary>How many server tabs are currently open.</summary>
    public int Count => _tabs.Count;

    /// <summary>The existing tab for a server; false when none is open (the caller opens a new one).</summary>
    public bool TryGet(int serverId, [MaybeNullWhen(false)] out TTab tab) => _tabs.TryGetValue(serverId, out tab);

    /// <summary>Registers (or replaces) the tab for a server.</summary>
    public void Add(int serverId, TTab tab) => _tabs[serverId] = tab;

    /// <summary>Drops the tab for a server; returns false when nothing was tracked for it.</summary>
    public bool Remove(int serverId) => _tabs.Remove(serverId);
}
