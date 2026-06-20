/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Windows;
using System.Windows.Media;
using ScottPlot.WPF;

namespace PerformanceMonitor.Ui
{
    /// <summary>
    /// Shared CHROME styling for ScottPlot charts — the single source of truth for chart
    /// theming across Dashboard and Lite. "Chrome" = figure/data backgrounds, axis + grid +
    /// legend colors, tick label colors/sizes, and axis-mechanics helpers. It deliberately does
    /// NOT own series/category COLOR IDENTITY (that is a separate concern; see ChartPalette).
    ///
    /// Theme colors read <see cref="ThemeManager.CurrentTheme"/> (Dark / Light / CoolBreeze).
    /// </summary>
    public static class ChartStyle
    {
        /// <summary>
        /// Applies the current color theme (chrome only) to a ScottPlot chart.
        /// </summary>
        public static void ApplyThemeToChart(WpfPlot chart)
        {
            ScottPlot.Color figureBackground, dataBackground, textColor, gridColor, legendBg, legendFg, legendOutline;

            if (ThemeManager.CurrentTheme == "CoolBreeze")
            {
                figureBackground = ScottPlot.Color.FromHex("#EEF4FA");
                dataBackground   = ScottPlot.Color.FromHex("#DAE6F0");
                textColor        = ScottPlot.Color.FromHex("#1A2A3A");
                gridColor        = ScottPlot.Color.FromHex("#A8BDD0").WithAlpha(120);
                legendBg         = ScottPlot.Color.FromHex("#EEF4FA");
                legendFg         = ScottPlot.Color.FromHex("#1A2A3A");
                legendOutline    = ScottPlot.Color.FromHex("#A8BDD0");
            }
            else if (ThemeManager.HasLightBackground)
            {
                figureBackground = ScottPlot.Color.FromHex("#FFFFFF");
                dataBackground   = ScottPlot.Color.FromHex("#F5F7FA");
                textColor        = ScottPlot.Color.FromHex("#1A1D23");
                gridColor        = ScottPlot.Colors.Black.WithAlpha(20);
                legendBg         = ScottPlot.Color.FromHex("#FFFFFF");
                legendFg         = ScottPlot.Color.FromHex("#1A1D23");
                legendOutline    = ScottPlot.Color.FromHex("#DEE2E6");
            }
            else
            {
                figureBackground = ScottPlot.Color.FromHex("#22252b");
                dataBackground   = ScottPlot.Color.FromHex("#111217");
                textColor        = ScottPlot.Color.FromHex("#E4E6EB");
                gridColor        = ScottPlot.Colors.White.WithAlpha(40);
                legendBg         = ScottPlot.Color.FromHex("#22252b");
                legendFg         = ScottPlot.Color.FromHex("#E4E6EB");
                legendOutline    = ScottPlot.Color.FromHex("#2a2d35");
            }

            chart.Plot.FigureBackground.Color = figureBackground;
            chart.Plot.DataBackground.Color = dataBackground;
            chart.Plot.Axes.Color(textColor);
            chart.Plot.Grid.MajorLineColor = gridColor;
            chart.Plot.Legend.BackgroundColor = legendBg;
            chart.Plot.Legend.FontColor = legendFg;
            chart.Plot.Legend.OutlineColor = legendOutline;
            chart.Plot.Legend.Alignment = ScottPlot.Alignment.LowerCenter;
            chart.Plot.Legend.Orientation = ScottPlot.Orientation.Horizontal;
            chart.Plot.Axes.Margins(bottom: 0); // No bottom margin - SetChartYLimitsWithLegendPadding handles Y-axis

            // Explicitly set axis tick label colors (needed after DateTimeTicksBottom() is called)
            chart.Plot.Axes.Bottom.TickLabelStyle.ForeColor = textColor;
            chart.Plot.Axes.Left.TickLabelStyle.ForeColor = textColor;
            chart.Plot.Axes.Bottom.Label.ForeColor = textColor;
            chart.Plot.Axes.Left.Label.ForeColor = textColor;
            chart.Plot.Axes.Bottom.TickLabelStyle.FontSize = 13;
            chart.Plot.Axes.Left.TickLabelStyle.FontSize = 13;

            // Set the WPF control Background to match so no white flash appears before ScottPlot's render loop fires
            chart.Background = new SolidColorBrush(Color.FromRgb(figureBackground.R, figureBackground.G, figureBackground.B));

            // Ensure ScottPlot renders with the correct colors the very first time it gets pixel dimensions.
            // Without this, ScottPlot's first auto-render (triggered by SizeChanged) would show a white canvas
            // before our FigureBackground color takes visual effect.
            chart.Loaded -= HandleChartFirstLoaded;
            if (!chart.IsLoaded)
                chart.Loaded += HandleChartFirstLoaded;
        }

        private static void HandleChartFirstLoaded(object sender, RoutedEventArgs e)
        {
            var chart = (WpfPlot)sender;
            chart.Loaded -= HandleChartFirstLoaded;
            chart.Refresh();
        }

        /// <summary>
        /// Reapplies theme-appropriate text colors (and font sizes) to chart axes.
        /// Call this AFTER DateTimeTicksBottom() or other axis modifications that reset them.
        /// </summary>
        public static void ReapplyAxisColors(WpfPlot chart)
        {
            var textColor = ThemeManager.CurrentTheme == "CoolBreeze"
                ? ScottPlot.Color.FromHex("#1A2A3A")
                : ThemeManager.HasLightBackground
                    ? ScottPlot.Color.FromHex("#1A1D23")
                    : ScottPlot.Color.FromHex("#E4E6EB");
            chart.Plot.Axes.Bottom.TickLabelStyle.ForeColor = textColor;
            chart.Plot.Axes.Left.TickLabelStyle.ForeColor = textColor;
            chart.Plot.Axes.Bottom.Label.ForeColor = textColor;
            chart.Plot.Axes.Left.Label.ForeColor = textColor;
            chart.Plot.Axes.Bottom.TickLabelStyle.FontSize = 13;
            chart.Plot.Axes.Left.TickLabelStyle.FontSize = 13;
        }
    }
}
