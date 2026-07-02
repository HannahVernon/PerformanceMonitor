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
/// Colors the Collection Health status cell text: SUCCESS green, PERMISSIONS orange, ERROR red,
/// anything else (SKIPPED, unknown) the window's normal foreground. The hexes are the product's
/// Material-300 cycling colors (green/orange/red from ChartPalette) so the grid reads like the
/// charts. Brushes are frozen and shared across rows.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_success = Frozen("#81C784");
    private static readonly SolidColorBrush s_permissions = Frozen("#FFB74D");
    private static readonly SolidColorBrush s_error = Frozen("#E57373");
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

        if (string.Equals(status, "PERMISSIONS", StringComparison.OrdinalIgnoreCase))
        {
            return s_permissions;
        }

        if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return s_error;
        }

        return s_default;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Status colors are one-way.");
}
