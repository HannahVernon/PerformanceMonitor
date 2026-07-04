/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Chart-helper bridge (headless-plan W0): the ScottPlot helper shapes copied verbatim from Lite's
/// <c>ServerTab.Charts.cs</c> (ClearChart, ShowChartLegend, and the ApplyTheme / ReapplyAxisColors /
/// SetChartYLimitsWithLegendPadding forwarders that delegate to the shared <see cref="ChartStyle"/>),
/// plus the <c>_legendPanels</c> tracking dictionary. These exist so the later waves that port Lite's
/// Render* chart bodies into this control can drop them in byte-identical — no rewrite of the chart
/// idiom. W0's own relocated Overview charts call <see cref="ChartStyle"/> directly and don't need
/// these yet; they're the compatibility surface for the tab ports that follow.
/// </summary>
public partial class ViewerServerTab
{
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, ScottPlot.IPanel?> _legendPanels = new();

    /// <summary>
    /// Clears a chart and removes any existing legend panel to prevent duplication.
    /// </summary>
    private void ClearChart(ScottPlot.WPF.WpfPlot chart)
    {
        if (_legendPanels.TryGetValue(chart, out var existingPanel) && existingPanel != null)
        {
            chart.Plot.Axes.Remove(existingPanel);
            _legendPanels[chart] = null;
        }

        /* Reset fully — Plot.Clear() leaves stale DateTime axes behind,
           and DateTimeTicksBottom() replaces the axis object entirely.
           Resetting the plot object avoids tick generator type mismatches. */
        chart.Reset();
        chart.Plot.Clear();
    }

    /// <summary>
    /// Shows legend on chart and tracks it for proper cleanup on next refresh.
    /// </summary>
    private void ShowChartLegend(ScottPlot.WPF.WpfPlot chart)
    {
        _legendPanels[chart] = chart.Plot.ShowLegend(ScottPlot.Edge.Bottom);
        chart.Plot.Legend.FontSize = 13;
    }

    /// <summary>
    /// Sets up an empty chart with dark theme, Y-axis label, legend, and "No Data" annotation — copied
    /// verbatim from Lite's <c>ServerTab.Charts.cs</c> RefreshEmptyChart. Used by the Performance Trends
    /// + Query Heatmap ports (W1f-2), the first viewer charts that adopt Lite's empty-state idiom.
    /// </summary>
    private void RefreshEmptyChart(ScottPlot.WPF.WpfPlot chart, string legendText, string yAxisLabel)
    {
        ReapplyAxisColors(chart);

        /* Add invisible scatter to create legend entry (matches data chart layout) */
        var placeholder = chart.Plot.Add.Scatter(new double[] { 0 }, new double[] { 0 });
        placeholder.LegendText = legendText;
        placeholder.Color = ScottPlot.Color.FromHex(ChartPalette.AccentColor("Placeholder"));
        placeholder.MarkerSize = 0;
        placeholder.LineWidth = 0;

        /* Add centered "No Data" text */
        var text = chart.Plot.Add.Text($"{legendText}\nNo Data", 0, 0);
        text.LabelFontColor = ScottPlot.Color.FromHex(ChartPalette.AccentColor("Placeholder"));
        text.LabelFontSize = 14;
        text.LabelAlignment = ScottPlot.Alignment.MiddleCenter;

        /* Configure axes */
        chart.Plot.HideGrid();
        chart.Plot.Axes.SetLimitsX(-1, 1);
        chart.Plot.Axes.SetLimitsY(-1, 1);
        chart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.EmptyTickGenerator();
        chart.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.EmptyTickGenerator();
        chart.Plot.YLabel(yAxisLabel);

        /* Show legend to match data chart layout */
        ShowChartLegend(chart);
        chart.Refresh();
    }

    /// <summary>
    /// Applies the chrome theme to a ScottPlot chart.
    /// Delegates to the shared <see cref="ChartStyle"/> — single source of truth across apps.
    /// </summary>
    private static void ApplyTheme(ScottPlot.WPF.WpfPlot chart) => ChartStyle.ApplyThemeToChart(chart);

    /// <summary>
    /// Reapplies theme-appropriate axis text colors/sizes after DateTimeTicksBottom() resets them.
    /// Delegates to the shared <see cref="ChartStyle"/>.
    /// </summary>
    private static void ReapplyAxisColors(ScottPlot.WPF.WpfPlot chart) => ChartStyle.ReapplyAxisColors(chart);

    /// <summary>
    /// Sets Y-axis limits with padding for bottom legend and top breathing room.
    /// Delegates to the shared <see cref="ChartStyle"/>.
    /// </summary>
    private static void SetChartYLimitsWithLegendPadding(ScottPlot.WPF.WpfPlot chart, double dataYMin = 0, double dataYMax = 0)
        => ChartStyle.SetChartYLimitsWithLegendPadding(chart, dataYMin, dataYMax);
}
