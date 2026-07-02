/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Host-supplied context a collector definition runs under: the server identity and collection
/// timestamp the host stamps on every row, the delta calculator, and host-configured filters.
/// </summary>
public sealed class CollectorContext
{
    private static readonly IReadOnlySet<string> s_emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public required int ServerId { get; init; }

    public required string ServerName { get; init; }

    /// <summary>UTC timestamp the host stamps as collection_time; definitions pass it to delta calls.</summary>
    public required DateTime CollectionTime { get; init; }

    public required ICollectorDeltaCalculator Deltas { get; init; }

    /// <summary>Wait types excluded from collection (Lite: ignored_wait_types.json — #1240).</summary>
    public IReadOnlySet<string> IgnoredWaitTypes { get; init; } = s_emptySet;
}
