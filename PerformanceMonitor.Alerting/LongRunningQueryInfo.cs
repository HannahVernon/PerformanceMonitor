/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Alerting;

/// <summary>
/// A currently-running query whose elapsed time exceeds the long-running-query alert threshold.
/// Canonical shared copy (Phase-5 A0) — Lite and the Dashboard previously carried member-identical
/// local twins; both apps now alias this type via a global using so call sites are unchanged.
/// </summary>
public class LongRunningQueryInfo
{
    public int SessionId { get; set; }
    public string DatabaseName { get; set; } = "";
    public string QueryText { get; set; } = "";
    public string ProgramName { get; set; } = "";
    public long ElapsedSeconds { get; set; }
    public long CpuTimeMs { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public string? WaitType { get; set; }
    public int? BlockingSessionId { get; set; }
    public string? QueryHash { get; set; }
}
