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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Colors the Recommendations severity bands: CRITICAL red, WARNING orange, INFO the palette's
/// light blue. It also maps the collector status vocabulary — SUCCESS green, PERMISSIONS orange
/// (shared with WARNING), ERROR red (shared with CRITICAL) — a holdover from the shell's original
/// Collection Health grid, which W1i changed to Lite's plain-text status (no color), leaving the
/// Recommendations grid the sole consumer. Anything else (SKIPPED, unknown) gets the window's
/// normal foreground. The hexes are the product's Material-300 cycling colors (ChartPalette) so
/// the grid reads like the charts. Brushes are frozen and shared across rows.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_success = Frozen("#81C784");
    private static readonly SolidColorBrush s_permissions = Frozen("#FFD54F");
    private static readonly SolidColorBrush s_error = Frozen("#E57373");
    private static readonly SolidColorBrush s_info = Frozen("#4FC3F7");
    private static readonly SolidColorBrush s_default = Frozen("#E4E6EB");

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string;
        if (string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return s_success;
        }

        if (string.Equals(status, "PERMISSIONS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "WARNING", StringComparison.OrdinalIgnoreCase))
        {
            return s_permissions;
        }

        if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "CRITICAL", StringComparison.OrdinalIgnoreCase))
        {
            return s_error;
        }

        if (string.Equals(status, "INFO", StringComparison.OrdinalIgnoreCase))
        {
            return s_info;
        }

        return s_default;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Status colors are one-way.");
}
