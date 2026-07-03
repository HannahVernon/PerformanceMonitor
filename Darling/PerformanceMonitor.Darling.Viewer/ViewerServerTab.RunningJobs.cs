/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using System.Windows;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Running Jobs inner tab — a COPY of Lite's Running Jobs grid (ServerTab.xaml + the
/// <c>RefreshRunningJobsAsync</c> load), reads rewired to Postgres. The grid shows the latest snapshot
/// of currently-running SQL Agent jobs with their historical duration comparison; the column-filter
/// headers ride the shared W1g filter manager (<c>_runningJobsFilterMgr</c>, registered in
/// ViewerServerTab.Filters.cs), and the <c>IsRunningLong</c> row highlight lives in the XAML row style.
/// The one behavioral deviation from Lite is the msdb-access banner: Lite drives it from a live
/// per-connection probe; the viewer derives it from the store (see <see cref="LoadRunningJobsAsync"/>).
/// </summary>
public partial class ViewerServerTab
{
    /// <summary>
    /// Running Jobs tab load: the latest-snapshot grid read and the collector-status read (for the
    /// msdb banner) fire concurrently — NpgsqlDataSource pools a connection for each — then the grid
    /// goes through the filter manager's UpdateData so any active column filter survives the refresh.
    /// </summary>
    private async Task LoadRunningJobsAsync()
    {
        var jobsTask = _dataService.GetRunningJobsAsync(_server.ServerId);
        var statusTask = _dataService.GetLatestRunningJobsCollectorStatusAsync(_server.ServerId);
        var jobs = await jobsTask;
        var status = await statusTask;

        _runningJobsFilterMgr!.UpdateData(jobs);

        /* msdb-access banner, derived from the store (the viewer has no live msdb probe): Lite shows
           the banner when its login lacks msdb access; here the running_jobs collector's latest run
           status stands in — a PERMISSIONS (or ERROR) outcome is the store-side signal that the
           service could not read msdb. SUCCESS / null (never run) hides it. */
        bool lacksMsdbAccess =
            string.Equals(status, "PERMISSIONS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase);
        RunningJobsMsdbWarning.Visibility = lacksMsdbAccess ? Visibility.Visible : Visibility.Collapsed;
    }
}
