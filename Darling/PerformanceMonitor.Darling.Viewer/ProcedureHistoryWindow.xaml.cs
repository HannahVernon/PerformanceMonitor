/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Top Procedures per-row double-click history window — ported from Lite's
/// <c>Windows/ProcedureHistoryWindow.xaml.cs</c>. Shows the selected procedure's (by database + schema +
/// object) metric trend over the window plus a filterable grid of its per-collection snapshots, with a metric
/// selector driving the chart. Reads come from <see cref="ViewerDataService.GetProcedureStatsHistoryAsync"/>
/// (Postgres). "View Plan" and the per-row Download button surface the STORED plan
/// (<see cref="ViewerDataService.GetProcedureStatsPlanXmlAsync"/>); procedures were already View-Plan-only in
/// Lite (re-executing a proc without parameter values is unsafe), so nothing extra is dropped.
/// </summary>
public partial class ProcedureHistoryWindow : Window
{
    private readonly ViewerDataService _dataService;
    private readonly int _serverId;
    private readonly string _databaseName;
    private readonly string _schemaName;
    private readonly string _objectName;
    private readonly DateTime _startUtc;
    private readonly DateTime _endUtc;
    private List<ViewerProcedureStatsHistoryRow> _historyData = new();
    private ChartHoverHelper? _chartHover;
    private readonly DataGridFilterManager<ViewerProcedureStatsHistoryRow> _filterManager;
    private Popup? _filterPopup;
    private ColumnFilterPopup? _filterPopupContent;

    public ProcedureHistoryWindow(
        ViewerDataService dataService, int serverId, string databaseName, string schemaName, string objectName,
        DateTime startUtc, DateTime endUtc)
    {
        InitializeComponent();
        _dataService = dataService;
        _serverId = serverId;
        _databaseName = databaseName;
        _schemaName = schemaName;
        _objectName = objectName;
        _startUtc = startUtc;
        _endUtc = endUtc;

        _filterManager = new DataGridFilterManager<ViewerProcedureStatsHistoryRow>(HistoryDataGrid);
        DataGridFilterColumns.AddFilterButtons(HistoryDataGrid, Filter_Click);
        _filterManager.UpdateFilterButtonStyles();

        var fullName = string.IsNullOrEmpty(schemaName) ? objectName : $"{schemaName}.{objectName}";
        ProcIdentifierText.Text = $"Procedure History: {fullName} in [{databaseName}]";
        Loaded += async (_, _) => await LoadHistoryAsync();
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            _historyData = await _dataService.GetProcedureStatsHistoryAsync(_serverId, _databaseName, _schemaName, _objectName, _startUtc, _endUtc);
            _filterManager.UpdateData(_historyData);

            if (_historyData.Count > 0)
            {
                var totalExec = _historyData.Sum(r => r.DeltaExecutions);
                var totalCpu = _historyData.Sum(r => r.DeltaCpuMs);
                var first = ViewerTimeHelper.ForDisplay(_historyData.First().CollectionTime);
                var last = ViewerTimeHelper.ForDisplay(_historyData.Last().CollectionTime);
                SummaryText.Text = $"{_historyData.Count} samples from {first:MM/dd HH:mm} to {last:MM/dd HH:mm} | " +
                                   $"Total Executions: {totalExec:N0} | Total CPU: {totalCpu:N1} ms";
            }
            else
            {
                SummaryText.Text = "No history data found for this procedure in the selected time range.";
            }

            UpdateChart();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Error loading history: {ex.Message}";
        }
    }

    private void UpdateChart()
    {
        if (_historyData == null || _historyData.Count == 0)
        {
            HistoryChart.Plot.Clear();
            HistoryChart.Refresh();
            return;
        }

        HistoryChart.Plot.Clear();

        var selected = MetricSelector.SelectedItem as ComboBoxItem;
        var tag = selected?.Tag?.ToString() ?? "AvgCpuMs";
        var label = selected?.Content?.ToString() ?? "Avg CPU (ms)";

        var xs = _historyData.Select(r => ViewerTimeHelper.ForDisplay(r.CollectionTime).ToOADate()).ToArray();
        var ys = _historyData.Select(r => GetMetricValue(r, tag)).ToArray();

        var scatter = HistoryChart.Plot.Add.Scatter(xs, ys);
        scatter.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("MetricTrend"));
        ChartStyle.StyleScatter(scatter);
        scatter.LegendText = label;

        var unit = tag.Contains("Ms") ? "ms" : "";
        if (_chartHover == null)
            _chartHover = new ChartHoverHelper(HistoryChart, unit);
        else
            _chartHover.Unit = unit;
        _chartHover.Clear();
        _chartHover.Add(scatter, label);

        HistoryChart.Plot.Axes.DateTimeTicksBottom();
        ApplyTheme(HistoryChart);

        HistoryChart.Refresh();
    }

    /// <summary>Projects one history row to the metric the selector shows — the chart's Y value. Pure +
    /// static for unit testing (mirrors Lite's GetMetricValue switch).</summary>
    internal static double GetMetricValue(ViewerProcedureStatsHistoryRow row, string tag) => tag switch
    {
        "AvgCpuMs" => row.AvgCpuMs,
        "AvgElapsedMs" => row.AvgElapsedMs,
        "AvgReads" => row.AvgReads,
        "DeltaExecutions" => row.DeltaExecutions,
        "DeltaCpuMs" => row.DeltaCpuMs,
        "DeltaLogicalReads" => row.DeltaLogicalReads,
        "DeltaSpills" => row.DeltaSpills,
        _ => row.AvgCpuMs
    };

    private void MetricSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateChart(); }

    private static void ApplyTheme(ScottPlot.WPF.WpfPlot chart) => ChartStyle.ApplyMinimalChartTheme(chart);

    private void OnThemeChanged(string _)
    {
        ApplyTheme(HistoryChart);
        HistoryChart.Refresh();
        _filterManager.UpdateFilterButtonStyles();
    }

    // ── Stored plan surface (viewer has no live server — Lite's live fetch becomes a stored read) ──

    private async void DownloadPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (string.IsNullOrEmpty(_objectName)) return;

        btn.IsEnabled = false;
        btn.Content = "...";
        try
        {
            var plan = await _dataService.GetProcedureStatsPlanXmlAsync(_serverId, _databaseName, _schemaName, _objectName);
            if (string.IsNullOrEmpty(plan))
            {
                MessageBox.Show(this, "No plan was captured in the collected data for this procedure.",
                    "Plan Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "SQL Plan files (*.sqlplan)|*.sqlplan|All files (*.*)|*.*",
                DefaultExt = ".sqlplan",
                FileName = $"proc_plan_{_objectName}_{DateTime.Now:yyyyMMdd_HHmmss}.sqlplan"
            };

            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName, plan, Encoding.UTF8);
            btn.Content = "Saved";
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to retrieve plan: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (btn.Content is "...")
                btn.Content = "Download";
            btn.IsEnabled = true;
        }
    }

    /// <summary>"View Plan" — shows the procedure's stored estimated plan in a shared
    /// <see cref="PlanViewerControl"/> floated above this window. Procedures are View-Plan-only (Lite too).</summary>
    private async void ViewPlan_Click(object sender, RoutedEventArgs e)
    {
        var fullName = string.IsNullOrEmpty(_schemaName) ? _objectName : $"{_schemaName}.{_objectName}";
        string? planXml;
        try
        {
            planXml = await _dataService.GetProcedureStatsPlanXmlAsync(_serverId, _databaseName, _schemaName, _objectName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to retrieve plan: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (string.IsNullOrEmpty(planXml))
        {
            MessageBox.Show(this, "No plan was captured in the collected data for this procedure.",
                "No Plan Available", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var label = $"Est Plan - {fullName}";
        var viewer = new PlanViewerControl();
        try
        {
            await viewer.LoadPlan(planXml, label, queryText: null);
        }
        catch (Exception ex)
        {
            viewer.Cleanup();
            MessageBox.Show(this, $"Failed to load the execution plan:\n\n{ex.Message}",
                "Plan Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = GraphViewerWindow.ShowGraph(this, viewer, label);
        window.Closed += (_, _) => viewer.Cleanup();
    }

    // ── Column Filter Popup (mirrors WaitDrillDownWindow / ViewerServerTab.Filters.cs) ──

    private void EnsureFilterPopup()
    {
        if (_filterPopup == null)
        {
            _filterPopupContent = new ColumnFilterPopup();
            _filterPopup = new Popup
            {
                Child = _filterPopupContent,
                StaysOpen = false,
                Placement = PlacementMode.Bottom,
                AllowsTransparency = true
            };
        }
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string columnName) return;

        EnsureFilterPopup();

        _filterPopupContent!.FilterApplied -= FilterPopup_FilterApplied;
        _filterPopupContent.FilterCleared -= FilterPopup_FilterCleared;
        _filterPopupContent.FilterApplied += FilterPopup_FilterApplied;
        _filterPopupContent.FilterCleared += FilterPopup_FilterCleared;

        _filterManager.Filters.TryGetValue(columnName, out var existingFilter);
        _filterPopupContent.Initialize(columnName, existingFilter);

        _filterPopup!.PlacementTarget = button;
        _filterPopup.IsOpen = true;
    }

    private void FilterPopup_FilterApplied(object? sender, FilterAppliedEventArgs e)
    {
        if (_filterPopup != null) _filterPopup.IsOpen = false;
        _filterManager.SetFilter(e.FilterState);
    }

    private void FilterPopup_FilterCleared(object? sender, EventArgs e)
    {
        if (_filterPopup != null) _filterPopup.IsOpen = false;
    }

    // ── Copy / Export / Close ──

    private void CopyCell_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyCell(sender);
    private void CopyRow_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyRow(sender);
    private void CopyAllRows_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyAllRows(sender);
    private void ExportToCsv_Click(object sender, RoutedEventArgs e) => DataGridExport.ExportToCsv(sender, "procedure_history", ViewerExportSettings.CsvSeparator);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
