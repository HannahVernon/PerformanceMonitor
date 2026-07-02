/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Alerting;

/// <summary>
/// The latest tempdb space snapshot for a server, used by the tempdb-space alert.
/// Canonical shared copy (Phase-5 A0) — Lite and the Dashboard previously carried member-identical
/// local twins; both apps now alias this type via a global using so call sites are unchanged.
/// </summary>
public class TempDbSpaceInfo
{
    public double TotalReservedMb { get; set; }
    public double UnallocatedMb { get; set; }
    public double UserObjectReservedMb { get; set; }
    public double InternalObjectReservedMb { get; set; }
    public double VersionStoreReservedMb { get; set; }
    public int TopConsumerSessionId { get; set; }
    public double TopConsumerMb { get; set; }

    public double UsedPercent => TotalReservedMb + UnallocatedMb > 0
        ? TotalReservedMb / (TotalReservedMb + UnallocatedMb) * 100
        : 0;
}
