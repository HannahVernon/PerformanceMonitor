/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Ui
{
    /// <summary>
    /// Turns a 0..1 heat intensity into a themed cell-background brush for the Locking &amp; Contention
    /// grid (#1138 §3B). The <c>ConverterParameter</c> names the wait category whose hue to use — its color
    /// comes from <see cref="ChartPalette.WaitColor"/> (the shared wait taxonomy) so the four wait-type
    /// columns read as their kind (Lock / Buffer Latch / Buffer IO), and the hue is alpha-scaled by the
    /// intensity so a quiet cell fades to the theme background and the numeric value stays readable in every
    /// theme. The light/CoolBreeze hue override is honored via <see cref="ThemeManager.HasLightBackground"/>.
    /// Shared in .Ui so both apps color cells identically.
    /// </summary>
    public sealed class HeatIntensityToBrushConverter : IValueConverter
    {
        /// <summary>Singleton — the converter is stateless, so one instance serves every cell in both apps.</summary>
        public static readonly HeatIntensityToBrushConverter Instance = new();

        // Max alpha at full intensity. Deliberately below 255 so the warm hue tints rather than masks the
        // cell, keeping the right-aligned value text legible on dark and light themes alike.
        private const double MaxAlpha = 200.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double intensity = value switch
            {
                double d => d,
                float f => f,
                _ => 0.0
            };
            if (double.IsNaN(intensity) || intensity <= 0)
                return Brushes.Transparent;
            if (intensity > 1) intensity = 1;

            var category = parameter as string ?? "Others";
            var hex = ChartPalette.WaitColor(category, ThemeManager.HasLightBackground);

            try
            {
                var baseColor = (Color)ColorConverter.ConvertFromString(hex);
                var a = (byte)Math.Round(intensity * MaxAlpha);
                var brush = new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
                brush.Freeze();
                return brush;
            }
            catch
            {
                return Brushes.Transparent;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
