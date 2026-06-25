/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using PerformanceMonitor.Common;
using ScottPlot.WPF;

namespace PerformanceMonitor.Ui
{
    /// <summary>
    /// What a <see cref="FinOpsHeatmapRenderer.Render"/> call leaves on the chart: the heatmap plottable
    /// (for hover hit-testing) and the colorbar panel (so the next render can remove it before re-adding).
    /// The host stores this between renders, exactly as the query-heatmap code tracks its plottable + legend.
    /// </summary>
    public sealed class FinOpsHeatmapHandle
    {
        public ScottPlot.Plottables.Heatmap? Plottable { get; init; }
        public ScottPlot.Panels.ColorBar? ColorBar { get; init; }
    }

    /// <summary>
    /// The single, shared ScottPlot heatmap renderer for the FinOps object-growth heatmap (#1138), used by
    /// BOTH apps so the two hand-duplicated FinOps hosts can never drift from each other (the parity risk
    /// the plan calls out, R4/R5). It is the query-heatmap renderer with the count-specific couplings
    /// stripped: continuous-metric colorbar (no integer "nice-number" ticks, no 10000 cap), a "M/d" daily
    /// X axis, and a caller-supplied colorbar label. Chrome/theming flows through <see cref="ChartStyle"/>,
    /// identical to every other chart. The 0→NaN behaviour is reused unchanged (a blank cell = object
    /// absent/empty that day), which is benign for an absolute-MB, growth-ranked top-N (plan §3A/M2).
    /// </summary>
    public static class FinOpsHeatmapRenderer
    {
        /// <summary>
        /// Renders <paramref name="matrix"/> into <paramref name="chart"/> and returns a handle for hover +
        /// the next render's cleanup. Pass the previous handle so its colorbar panel is removed first (panels
        /// survive <c>Plot.Clear()</c>). Row index 0 renders at the BOTTOM (FlipVertically), matching the
        /// query heatmap, so the caller orders rows bottom-to-top.
        /// </summary>
        public static FinOpsHeatmapHandle Render(
            WpfPlot chart,
            FinOpsHeatmapMatrix matrix,
            string colorBarLabel,
            string title,
            FinOpsHeatmapHandle? previous,
            Func<double, string>? colorBarTickFormat = null)
        {
            if (previous?.ColorBar != null)
                chart.Plot.Axes.Remove(previous.ColorBar);
            chart.Plot.Clear();
            ChartStyle.ApplyThemeToChart(chart);

            if (matrix.IsEmpty)
            {
                chart.Plot.Title($"{title} — No Data");
                chart.Refresh();
                return new FinOpsHeatmapHandle();
            }

            int numRows = matrix.Intensities.GetLength(0);
            int numCols = matrix.Intensities.GetLength(1);

            // Log1p scaling; NaN for empty/zero cells so they render as background (the renderer's existing,
            // unchanged 0→NaN behaviour — see class summary).
            var scaled = new double[numRows, numCols];
            for (int r = 0; r < numRows; r++)
                for (int c = 0; c < numCols; c++)
                    scaled[r, c] = matrix.Intensities[r, c] > 0
                        ? Math.Log(1 + matrix.Intensities[r, c])
                        : double.NaN;

            var heatmap = chart.Plot.Add.Heatmap(scaled);
            heatmap.FlipVertically = true; // row 0 at the bottom
            heatmap.Colormap = new ScottPlot.Colormaps.Viridis();
            heatmap.NaNCellColor = chart.Plot.DataBackground.Color;

            // X-axis: daily date labels at column positions ("M/d"), ~12 labels max.
            var xTicks = new ScottPlot.TickGenerators.NumericManual();
            int xStep = Math.Max(1, numCols / 12);
            for (int i = 0; i < numCols; i += xStep)
                xTicks.AddMajor(i, matrix.Days[i].ToString("M/d", CultureInfo.InvariantCulture));
            chart.Plot.Axes.Bottom.TickGenerator = xTicks;

            // Y-axis: object identity labels.
            var yTicks = new ScottPlot.TickGenerators.NumericManual();
            for (int i = 0; i < matrix.RowLabels.Length; i++)
                yTicks.AddMajor(i, matrix.RowLabels[i]);
            chart.Plot.Axes.Left.TickGenerator = yTicks;

            chart.Plot.Axes.SetLimitsX(-0.5, numCols - 0.5);
            chart.Plot.Axes.SetLimitsY(-0.5, numRows - 0.5);

            ChartStyle.ReapplyAxisColors(chart);

            // Continuous-metric colorbar: log positions, "nice" round values up to the data max (no integer
            // tick coercion, no 10000 cap — those were query-count specifics).
            double maxRaw = 0;
            for (int r = 0; r < numRows; r++)
                for (int c = 0; c < numCols; c++)
                    if (matrix.Intensities[r, c] > maxRaw) maxRaw = matrix.Intensities[r, c];

            var colorBar = new ScottPlot.Panels.ColorBar(heatmap, ScottPlot.Edge.Right) { Label = colorBarLabel };
            var format = colorBarTickFormat ?? DefaultMetricTickFormat;
            var cbTicks = new ScottPlot.TickGenerators.NumericManual();
            cbTicks.AddMajor(0, "0");
            foreach (var n in NiceTicksUpTo(maxRaw))
                cbTicks.AddMajor(Math.Log(1 + n), format(n));
            if (maxRaw > 0)
                cbTicks.AddMajor(Math.Log(1 + maxRaw), format(maxRaw));
            colorBar.Axis.TickGenerator = cbTicks;
            colorBar.LabelStyle.ForeColor = chart.Plot.Axes.Bottom.TickLabelStyle.ForeColor;
            colorBar.Axis.TickLabelStyle.ForeColor = chart.Plot.Axes.Bottom.TickLabelStyle.ForeColor;
            chart.Plot.Axes.AddPanel(colorBar);

            chart.Plot.Title(title);
            chart.Plot.Axes.Title.Label.ForeColor = chart.Plot.Axes.Bottom.TickLabelStyle.ForeColor;

            chart.Refresh();
            return new FinOpsHeatmapHandle { Plottable = heatmap, ColorBar = colorBar };
        }

        private static string DefaultMetricTickFormat(double v)
            => v >= 100
                ? v.ToString("N0", CultureInfo.InvariantCulture)
                : v.ToString("N1", CultureInfo.InvariantCulture);

        /// <summary>
        /// "Nice" 1/2/5 × 10^k tick values strictly below <paramref name="max"/>, ascending. Scales to any
        /// range (unlike the query heatmap's fixed {1..10000} list), so MB footprints of any size get a
        /// readable continuous colorbar.
        /// </summary>
        private static IEnumerable<double> NiceTicksUpTo(double max)
        {
            if (max <= 0) yield break;
            double magnitude = 1;
            // Start a few orders below the max so small-range heatmaps still get interior ticks.
            while (magnitude * 10 <= max) magnitude *= 10;
            while (magnitude >= 1 && magnitude > max / 1000.0) magnitude /= 10;
            if (magnitude < 1) magnitude = 1;

            for (double scale = magnitude; scale <= max; scale *= 10)
            {
                foreach (var mult in new[] { 1.0, 2.0, 5.0 })
                {
                    double val = mult * scale;
                    if (val < max) yield return val;
                }
            }
        }
    }
}
