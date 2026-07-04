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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// One monitored server's detail surface (headless-plan W0 IA inversion): a closable per-server tab
/// hosting an inner tab strip — Overview (CPU-utilization + wait-time-by-category trend charts),
/// Queries (top-50 query stats), Blocking (XE blocked-process reports with the DMV-snapshot fallback),
/// and Collection Health (per-collector last-run status). This is the per-server content relocated
/// out of MainWindow's old flat tab list; the load/render bodies are unchanged (pure re-hosting).
/// Loads are lazy per inner tab (Lite's visible-only rule): switching inner tabs loads the newly
/// visible one, and <see cref="RefreshActiveInnerTabAsync"/> — driven by MainWindow's 60-second timer
/// only when this server tab is the visible top-level tab — reloads just the active inner tab.
/// </summary>
public partial class ViewerServerTab : UserControl
{
    /// <summary>One window for every windowed surface: charts, queries, and blocking all read 24 hours.</summary>
    private static readonly TimeSpan s_dataWindow = TimeSpan.FromHours(24);

    /* Inner-tab order mirrors Lite's ServerTab relative order (Overview, Wait Stats, Queries,
       Plan Viewer, CPU, Memory, File I/O, tempdb, Blocking, Perfmon, Running Jobs, Configuration, Daily
       Summary, Collection Health) — ported tabs slot into Lite's positions as they arrive (Plan
       Viewer sits BETWEEN Queries and CPU, Memory sits BETWEEN CPU and File I/O, File I/O sits BEFORE
       tempdb, Perfmon/Running Jobs sit between Blocking and Configuration, and Daily Summary sits between
       Configuration and Collection Health, matching Lite's own order), so the constants renumber when a
       wave lands between existing tabs. */
    private const int OverviewInnerTabIndex = 0;
    private const int WaitStatsInnerTabIndex = 1;
    private const int QueriesInnerTabIndex = 2;
    private const int PlanViewerInnerTabIndex = 3;
    private const int CpuInnerTabIndex = 4;
    private const int MemoryInnerTabIndex = 5;
    private const int FileIoInnerTabIndex = 6;
    private const int TempDbInnerTabIndex = 7;
    private const int BlockingInnerTabIndex = 8;
    private const int PerfmonInnerTabIndex = 9;
    private const int RunningJobsInnerTabIndex = 10;
    private const int ConfigurationInnerTabIndex = 11;
    private const int DailySummaryInnerTabIndex = 12;
    private const int HealthInnerTabIndex = 13;

    private readonly ViewerDataService _dataService;
    private readonly DarlingServer _server;

    private bool _refreshInFlight;
    private bool _refreshRequested;

    /// <summary>Raised after a load so MainWindow can surface progress/errors in its status bar.</summary>
    public event Action<string>? StatusChanged;

    public ViewerServerTab(ViewerDataService dataService, DarlingServer server)
    {
        _dataService = dataService;
        _server = server;
        InitializeComponent();

        /* Column-filter managers for the Configuration sub-grids (copied from Lite's ServerTab filter
           wiring) — after InitializeComponent so the named grids exist. */
        InitializeFilterManagers();

        /* Overview lanes (copied from Lite): init the data service + server up front so the lanes theme
           their chrome and wire the correlated crosshair before the first load. The lanes' own
           "Show Active Queries at This Time" drill-down event is deliberately NOT wired — the viewer has
           no Active Queries surface yet (deferred). ThemeManager is fixed to Dark, so the shared chart
           chrome applies without any per-control theme plumbing. */
        OverviewLanes.Initialize(_dataService, _server.ServerId);

        /* CPU + tempdb inner-tab charts (copied from Lite): theme up front + wire hover. */
        InitializeCpuTempDbCharts();

        /* Memory inner-tab charts (copied from Lite): same up-front theme + hover for the five Memory
           charts (Overview trend, Clerks, Grant sizing/activity, Pressure events). */
        InitializeMemoryCharts();

        /* File I/O + Blocking-trend inner-tab charts (copied from Lite): same up-front theme + hover. */
        InitializeFileIoCharts();
        InitializeBlockingCharts();

        /* Queries tab (W1f-1): the three grids' bar-cell maxima hook + slicer RangeChanged wiring
           (copied from Lite's ServerTab). After InitializeComponent so the named grids/slicers exist. */
        InitializeQueriesTab();

        /* Collection Health's Duration Trends chart (copied from Lite): up-front theme + hover. */
        InitializeCollectionHealthChart();
    }

    /// <summary>The server this tab is bound to; MainWindow keys open tabs by this for dedupe/close.</summary>
    public int ServerId => _server.ServerId;

    /// <summary>The server record, for the tab header label and status text.</summary>
    public DarlingServer Server => _server;

    /// <summary>
    /// Lazy per-inner-tab load: switching inner tabs loads the newly visible one. SelectionChanged is
    /// a bubbling routed event, so selections inside the tab content (a grid) reach here too — only
    /// react to the inner TabControl's own. Gated on <see cref="System.Windows.FrameworkElement.IsLoaded"/>
    /// so the selection raised while the TabControl is first built (before this tab is shown) is ignored:
    /// MainWindow drives the initial load when it makes this server tab visible, and the 60-second timer
    /// keeps it fresh — so the control loads exactly once per event, not on construction too.
    /// </summary>
    private async void InnerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, InnerTabs) || !IsLoaded)
        {
            return;
        }

        await RefreshActiveInnerTabAsync();
    }

    /// <summary>
    /// Loads the ACTIVE inner tab, with the overlap guard mirroring MainWindow's aggregate-tab loop.
    /// If the inner tab switches while a load is in flight, the triggering event bounces off the guard,
    /// so after each load we loop when the selection moved on and load again — leaving no inner tab
    /// stranded. A trigger that arrives mid-load (the 60-second tick, an inner-tab switch) sets
    /// <see cref="_refreshRequested"/> instead of being dropped, so the running loop reloads once more.
    /// </summary>
    public async Task RefreshActiveInnerTabAsync()
    {
        if (_refreshInFlight)
        {
            _refreshRequested = true;
            return;
        }

        _refreshInFlight = true;
        try
        {
            do
            {
                _refreshRequested = false;

                int loadedTab;
                do
                {
                    loadedTab = InnerTabs.SelectedIndex;
                    await LoadInnerTabAsync(loadedTab);
                }
                while (InnerTabs.SelectedIndex != loadedTab);
            }
            while (_refreshRequested);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private async Task LoadInnerTabAsync(int tabIndex)
    {
        try
        {
            switch (tabIndex)
            {
                case WaitStatsInnerTabIndex:
                    await LoadWaitStatsAsync();
                    break;
                case QueriesInnerTabIndex:
                    await LoadQueriesAsync();
                    break;
                case PlanViewerInnerTabIndex:
                    /* No data feed: plans are pushed into the host by OpenPlanTab (a "View Plan" click),
                       not loaded on tab-switch. Explicit no-op so selecting it doesn't fall through to the
                       default Overview reload, and so the 60-second timer leaves any open plan tabs alone. */
                    break;
                case BlockingInnerTabIndex:
                    await LoadBlockingAsync();
                    break;
                case PerfmonInnerTabIndex:
                    await LoadPerfmonAsync();
                    break;
                case RunningJobsInnerTabIndex:
                    await LoadRunningJobsAsync();
                    break;
                case ConfigurationInnerTabIndex:
                    await LoadConfigurationAsync();
                    break;
                case DailySummaryInnerTabIndex:
                    await LoadDailySummaryAsync();
                    break;
                case HealthInnerTabIndex:
                    await LoadHealthAsync();
                    break;
                case CpuInnerTabIndex:
                    await LoadCpuAsync();
                    break;
                case MemoryInnerTabIndex:
                    await LoadMemoryAsync();
                    break;
                case TempDbInnerTabIndex:
                    await LoadTempDbAsync();
                    break;
                case FileIoInnerTabIndex:
                    await LoadFileIoAsync();
                    break;
                case OverviewInnerTabIndex:
                default:
                    await LoadOverviewChartsAsync();
                    break;
            }

            StatusChanged?.Invoke($"{_server.DisplayName} — refreshed {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"refresh failed: {ex.Message}");
        }
    }

    private async Task LoadOverviewChartsAsync()
    {
        /* The lanes control reads its own six feeds + four baselines concurrently over the fixed
           24-hour window (s_dataWindow); the viewer has no custom-range picker, so fromDate/toDate
           are null. Replaces wave-4's CPU-trend + wait-category interim charts. */
        await OverviewLanes.RefreshAsync((int)s_dataWindow.TotalHours, null, null);
    }

    /* LoadHealthAsync now lives in ViewerServerTab.CollectionHealth.cs (W1i moved it there with the
       Collection Health sub-tabs), and LoadQueriesAsync now lives in ViewerServerTab.Queries.cs — it
       dispatches to the Queries tab's active sub-tab (Top Queries / Top Procedures / Query Store),
       loading that grid + its slicer + (when Compare is active) its comparison grid. */

    /* LoadBlockingAsync now lives in ViewerServerTab.Blocking.cs — it dispatches to the Blocking tab's
       active sub-tab (Trends / Current Waits / Blocked Process Reports) instead of loading the grid
       directly. */

    /* The Overview's rendering now lives entirely in the copied CorrelatedTimelineLanesControl
       (OverviewLanes); wave-4's RenderCpuTrend / RenderWaitCategoryTrend and their reads
       (ViewerDataService.Trends.cs) were superseded by the lanes and removed. */
}
