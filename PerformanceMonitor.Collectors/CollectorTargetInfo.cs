/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Collectors;

/// <summary>
/// What a definition may need to know about the monitored server to build its query.
/// Grown deliberately as the sweep demands (engine edition today; version gates arrive with
/// the collectors that need them) — every added field is parity-critical target logic.
/// </summary>
public sealed class CollectorTargetInfo
{
    /// <summary>True when the target is Azure SQL Database (engine edition 5).</summary>
    public bool IsAzureSqlDb { get; init; }
}
