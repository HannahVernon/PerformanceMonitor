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
using System.Windows.Controls;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The System Events inner tab (system_health parity, Stage 2b) — eight sub-tabs, one per unique
/// system_health warning category, each a parse-on-read grid: <see cref="ViewerDataService"/> fetches the
/// raw event_xml for the category over the fixed 24-hour window, the Common <c>SystemHealthParser</c>
/// shreds it, and <c>SystemEventSignificance</c> keeps the sp_HealthParser-significant rows. Mirrors the
/// FinOps tab's inner sub-tab-strip + DataGrid conventions (visible-only load, shared column filters,
/// per-sub-tab count indicator + refresh). Timestamps render machine-local like the deadlock / blocked-
/// process grids (the event's naive-UTC XE @timestamp via <see cref="ViewerDataService.ToLocalTime"/>).
/// </summary>
public partial class ViewerServerTab
{
    /* System Events sub-tab order (matches the XAML TabItem order). */
    private const int SystemEventsSchedulerIssuesSubTabIndex = 0;
    private const int SystemEventsSevereErrorsSubTabIndex = 1;
    private const int SystemEventsMemoryConditionsSubTabIndex = 2;
    private const int SystemEventsMemoryBrokerSubTabIndex = 3;
    private const int SystemEventsMemoryNodeOomSubTabIndex = 4;
    private const int SystemEventsSignificantWaitsSubTabIndex = 5;
    private const int SystemEventsCpuTasksSubTabIndex = 6;
    private const int SystemEventsIoIssuesSubTabIndex = 7;

    private DataGridFilterManager<SchedulerIssueRow>? _seSchedulerFilterMgr;
    private DataGridFilterManager<SevereErrorRow>? _seSevereErrorFilterMgr;
    private DataGridFilterManager<MemoryConditionsRow>? _seMemoryConditionsFilterMgr;
    private DataGridFilterManager<MemoryBrokerRow>? _seMemoryBrokerFilterMgr;
    private DataGridFilterManager<MemoryNodeOomRow>? _seMemoryNodeOomFilterMgr;
    private DataGridFilterManager<SignificantWaitRow>? _seSignificantWaitsFilterMgr;
    private DataGridFilterManager<CpuTasksRow>? _seCpuTasksFilterMgr;
    private DataGridFilterManager<IoIssuesRow>? _seIoIssuesFilterMgr;

    /// <summary>
    /// Registers the six System Events grids' column-filter managers into the shared
    /// <c>_filterManagers</c> map (defined in <c>ViewerServerTab.Filters.cs</c>). Called from the
    /// constructor after InitializeComponent, alongside the other Initialize* hooks.
    /// </summary>
    private void InitializeSystemEventsTab()
    {
        _seSchedulerFilterMgr = new DataGridFilterManager<SchedulerIssueRow>(SchedulerIssuesGrid);
        _seSevereErrorFilterMgr = new DataGridFilterManager<SevereErrorRow>(SevereErrorsGrid);
        _seMemoryConditionsFilterMgr = new DataGridFilterManager<MemoryConditionsRow>(MemoryConditionsGrid);
        _seMemoryBrokerFilterMgr = new DataGridFilterManager<MemoryBrokerRow>(MemoryBrokerGrid);
        _seMemoryNodeOomFilterMgr = new DataGridFilterManager<MemoryNodeOomRow>(MemoryNodeOomGrid);
        _seSignificantWaitsFilterMgr = new DataGridFilterManager<SignificantWaitRow>(SignificantWaitsGrid);
        _seCpuTasksFilterMgr = new DataGridFilterManager<CpuTasksRow>(CpuTasksGrid);
        _seIoIssuesFilterMgr = new DataGridFilterManager<IoIssuesRow>(IoIssuesGrid);

        _filterManagers[SchedulerIssuesGrid] = _seSchedulerFilterMgr;
        _filterManagers[SevereErrorsGrid] = _seSevereErrorFilterMgr;
        _filterManagers[MemoryConditionsGrid] = _seMemoryConditionsFilterMgr;
        _filterManagers[MemoryBrokerGrid] = _seMemoryBrokerFilterMgr;
        _filterManagers[MemoryNodeOomGrid] = _seMemoryNodeOomFilterMgr;
        _filterManagers[SignificantWaitsGrid] = _seSignificantWaitsFilterMgr;
        _filterManagers[CpuTasksGrid] = _seCpuTasksFilterMgr;
        _filterManagers[IoIssuesGrid] = _seIoIssuesFilterMgr;
    }

    /// <summary>
    /// A System Events sub-tab switch reloads through the shell's overlap-guarded
    /// <see cref="RefreshActiveInnerTabAsync"/> (mirrors the FinOps/Queries sub-tab handlers). Gated on
    /// <see cref="FrameworkElement.IsLoaded"/> and the sub-TabControl's own selection so build-time and
    /// bubbled child selections are ignored.
    /// </summary>
    private async void SystemEventsSubTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SystemEventsSubTabControl) || !IsLoaded)
        {
            return;
        }

        await RefreshActiveInnerTabAsync();
    }

    /// <summary>
    /// Loads the System Events tab's ACTIVE sub-tab only (Lite's visible-only rule). The shell's
    /// <see cref="LoadInnerTabAsync"/> owns the try/catch that surfaces failures on the status bar.
    /// </summary>
    private async Task LoadSystemEventsAsync()
    {
        switch (SystemEventsSubTabControl.SelectedIndex)
        {
            case SystemEventsSevereErrorsSubTabIndex:
                await LoadSevereErrorsAsync();
                break;
            case SystemEventsMemoryConditionsSubTabIndex:
                await LoadMemoryConditionsAsync();
                break;
            case SystemEventsMemoryBrokerSubTabIndex:
                await LoadMemoryBrokerAsync();
                break;
            case SystemEventsMemoryNodeOomSubTabIndex:
                await LoadMemoryNodeOomAsync();
                break;
            case SystemEventsSignificantWaitsSubTabIndex:
                await LoadSignificantWaitsAsync();
                break;
            case SystemEventsCpuTasksSubTabIndex:
                await LoadCpuTasksAsync();
                break;
            case SystemEventsIoIssuesSubTabIndex:
                await LoadIoIssuesAsync();
                break;
            case SystemEventsSchedulerIssuesSubTabIndex:
            default:
                await LoadSchedulerIssuesAsync();
                break;
        }
    }

    private async Task LoadSchedulerIssuesAsync()
    {
        var endUtc = DateTime.UtcNow;
        var data = await _dataService.GetSchedulerIssuesAsync(_server.ServerId, endUtc - s_dataWindow, endUtc);
        _seSchedulerFilterMgr!.UpdateData(data);
        SchedulerIssuesNoDataMessage.Visibility = data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SchedulerIssuesCountIndicator.Text = data.Count > 0 ? $"{data.Count} event(s)" : "";
    }

    private async Task LoadSevereErrorsAsync()
    {
        var endUtc = DateTime.UtcNow;
        var data = await _dataService.GetSevereErrorsAsync(_server.ServerId, endUtc - s_dataWindow, endUtc);
        _seSevereErrorFilterMgr!.UpdateData(data);
        SevereErrorsNoDataMessage.Visibility = data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SevereErrorsCountIndicator.Text = data.Count > 0 ? $"{data.Count} event(s)" : "";
    }

    private async Task LoadMemoryConditionsAsync()
    {
        var endUtc = DateTime.UtcNow;
        var data = await _dataService.GetMemoryConditionsAsync(_server.ServerId, endUtc - s_dataWindow, endUtc);
        _seMemoryConditionsFilterMgr!.UpdateData(data);
        MemoryConditionsNoDataMessage.Visibility = data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MemoryConditionsCountIndicator.Text = data.Count > 0 ? $"{data.Count} event(s)" : "";
    }

    private async Task LoadMemoryBrokerAsync()
    {
        var endUtc = DateTime.UtcNow;
        var data = await _dataService.GetMemoryBrokerAsync(_server.ServerId, endUtc - s_dataWindow, endUtc);
        _seMemoryBrokerFilterMgr!.UpdateData(data);
        MemoryBrokerNoDataMessage.Visibility = data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MemoryBrokerCountIndicator.Text = data.Count > 0 ? $"{data.Count} event(s)" : "";
    }

    private async Task LoadMemoryNodeOomAsync()
    {
        var endUtc = DateTime.UtcNow;
        var data = await _dataService.GetMemoryNodeOomAsync(_server.ServerId, endUtc - s_dataWindow, endUtc);
        _seMemoryNodeOomFilterMgr!.UpdateData(data);
        MemoryNodeOomNoDataMessage.Visibility = data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MemoryNodeOomCountIndicator.Text = data.Count > 0 ? $"{data.Count} event(s)" : "";
    }

    private async Task LoadSignificantWaitsAsync()
    {
        var endUtc = DateTime.UtcNow;
        var data = await _dataService.GetSignificantWaitsAsync(_server.ServerId, endUtc - s_dataWindow, endUtc);
        _seSignificantWaitsFilterMgr!.UpdateData(data);
        SignificantWaitsNoDataMessage.Visibility = data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SignificantWaitsCountIndicator.Text = data.Count > 0 ? $"{data.Count} event(s)" : "";
    }

    private async Task LoadCpuTasksAsync()
    {
        var endUtc = DateTime.UtcNow;
        var data = await _dataService.GetCpuTasksAsync(_server.ServerId, endUtc - s_dataWindow, endUtc);
        _seCpuTasksFilterMgr!.UpdateData(data);
        CpuTasksNoDataMessage.Visibility = data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CpuTasksCountIndicator.Text = data.Count > 0 ? $"{data.Count} event(s)" : "";
    }

    private async Task LoadIoIssuesAsync()
    {
        var endUtc = DateTime.UtcNow;
        var data = await _dataService.GetIoIssuesAsync(_server.ServerId, endUtc - s_dataWindow, endUtc);
        _seIoIssuesFilterMgr!.UpdateData(data);
        IoIssuesNoDataMessage.Visibility = data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IoIssuesCountIndicator.Text = data.Count > 0 ? $"{data.Count} event(s)" : "";
    }

    /// <summary>
    /// The per-sub-tab Refresh button reloads the active System Events sub-tab through the same status-bar
    /// error surfacing the shell's <see cref="LoadInnerTabAsync"/> uses (mirrors FinOps' RunFinOpsLoad).
    /// </summary>
    private async void SystemEventsRefresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadSystemEventsAsync();
            StatusChanged?.Invoke($"{_server.DisplayName} — refreshed {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"refresh failed: {ex.Message}");
        }
    }
}
