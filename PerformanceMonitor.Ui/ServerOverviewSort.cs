/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace PerformanceMonitor.Ui;

/// <summary>NOC Overview tile sort modes, shared by Lite and the Darling viewer.</summary>
public enum ServerOverviewSortMode
{
    /// <summary>CPU% descending (default): busiest first, no-sample servers last.</summary>
    Cpu = 0,
    /// <summary>Display name ascending (case-insensitive).</summary>
    Name = 1,
}

/// <summary>
/// Single source of truth for ordering the Overview server tiles in both apps. Pure, deterministic,
/// and a TOTAL order, so re-applying it every refresh on the same data yields the identical sequence —
/// tiles never jitter. Parameterised by selectors because each app binds its own unrelated
/// ServerSummaryItem type; there is deliberately no shared row interface.
/// </summary>
public static class ServerOverviewSort
{
    public const ServerOverviewSortMode Default = ServerOverviewSortMode.Cpu;

    /// <summary>Persisted token for a mode — the enum member name ("Cpu" / "Name").</summary>
    public static string ToToken(ServerOverviewSortMode mode) => mode.ToString();

    /// <summary>Tolerant parse: case-insensitive; null/empty/unrecognised -> Default (Cpu).</summary>
    public static ServerOverviewSortMode ParseMode(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && Enum.TryParse<ServerOverviewSortMode>(token, ignoreCase: true, out var mode)
        && Enum.IsDefined(mode)
            ? mode
            : Default;

    public static List<T> Order<T>(
        IEnumerable<T> items,
        ServerOverviewSortMode mode,
        Func<T, double?> cpuSelector,
        Func<T, string?> nameSelector,
        Func<T, int> idSelector)
    {
        IOrderedEnumerable<T> ordered =
            mode == ServerOverviewSortMode.Name
                ? items.OrderBy(x => nameSelector(x) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : items
                    .OrderByDescending(x => cpuSelector(x).HasValue)      // no-sample sinks last
                    .ThenByDescending(x => cpuSelector(x) ?? 0.0)          // CPU% descending
                    .ThenBy(x => nameSelector(x) ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        return ordered.ThenBy(idSelector).ToList();   // final unique key -> total order
    }
}
