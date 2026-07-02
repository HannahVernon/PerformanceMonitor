/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Alerting;

/// <summary>
/// One poison wait type's delta since the previous collection, used by the poison-wait alert.
/// Canonical shared copy (Phase-5 A0) — Lite and the Dashboard previously carried member-identical
/// local twins; both apps now alias this type via a global using so call sites are unchanged.
/// </summary>
public class PoisonWaitDelta
{
    public string WaitType { get; set; } = "";
    public long DeltaMs { get; set; }
    public long DeltaTasks { get; set; }
    public double AvgMsPerWait { get; set; }
}
