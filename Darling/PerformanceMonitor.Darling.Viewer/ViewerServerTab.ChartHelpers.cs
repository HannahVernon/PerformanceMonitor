/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
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
