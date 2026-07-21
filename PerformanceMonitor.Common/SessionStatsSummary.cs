/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Globalization;

namespace PerformanceMonitor.Common;

/// <summary>
/// The pure Session Stats summary-strip projection shared by Lite and the Darling viewer (previously the
/// byte-identical <c>FormatSessionSummary</c> in each app's Session Stats tab, itself the Dashboard's
/// <c>UpdateSessionStatsSummary</c> shaping). Given the latest server-wide session-summary snapshot it shapes
/// the three non-chartable attribution fields: Top Application / Top Host render as "<c>name (count)</c>" when
/// the snapshot names one and "N/A" otherwise; Databases is the distinct-databases count. A null point (no data
/// in the window) yields "N/A" for all three. WPF-free and static so the shaping is unit-testable without a
/// WpfPlot; each app keeps its own named-TextBlock setter that consumes this tuple.
/// </summary>
public static class SessionStatsSummary
{
    /// <summary>
    /// Shapes the summary strip's Top Application / Top Host / Databases strings from the latest snapshot
    /// (or all "N/A" when <paramref name="data"/> is null).
    /// </summary>
    public static (string TopApplication, string TopHost, string Databases) Format(ISessionStatsPoint? data)
    {
        if (data is null)
        {
            return ("N/A", "N/A", "N/A");
        }

        var topApp = !string.IsNullOrEmpty(data.TopApplicationName)
            ? $"{data.TopApplicationName} ({data.TopApplicationConnections ?? 0})"
            : "N/A";
        var topHost = !string.IsNullOrEmpty(data.TopHostName)
            ? $"{data.TopHostName} ({data.TopHostConnections ?? 0})"
            : "N/A";
        var databases = data.DatabasesWithConnections.ToString(CultureInfo.CurrentCulture);

        return (topApp, topHost, databases);
    }
}
