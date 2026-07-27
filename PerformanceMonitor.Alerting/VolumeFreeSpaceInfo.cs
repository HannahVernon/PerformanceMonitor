/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Alerting;

/// <summary>
/// Free space for a single distinct volume (mount point) on a monitored server,
/// used by the low-disk alert. Azure SQL DB has no volume stats, so no rows are
/// produced there and the alert never fires.
/// Canonical shared copy (Phase-5 A0) — Lite and the Dashboard previously carried member-identical
/// local twins; both apps now alias this type via a global using so call sites are unchanged.
/// </summary>
public class VolumeFreeSpaceInfo
{
    public string MountPoint { get; set; } = "";
    public double TotalMb { get; set; }
    public double FreeMb { get; set; }

    public double FreePercent => TotalMb > 0 ? FreeMb / TotalMb * 100 : 0;
    public double FreeGb => FreeMb / 1024.0;
}
