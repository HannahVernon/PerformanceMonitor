/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Alerting;

/// <summary>
/// A currently-running SQL Agent job whose duration exceeds the anomaly threshold (Nx its
/// historical average), used by the long-running-job alert.
/// Canonical shared copy (Phase-5 A0) — Lite and the Dashboard previously carried member-identical
/// local twins; both apps now alias this type via a global using so call sites are unchanged.
/// </summary>
public class AnomalousJobInfo
{
    public string JobName { get; set; } = "";
    public string JobId { get; set; } = "";
    public long CurrentDurationSeconds { get; set; }
    public long AvgDurationSeconds { get; set; }
    public long P95DurationSeconds { get; set; }
    public decimal? PercentOfAverage { get; set; }
    public DateTime StartTime { get; set; }
}
