/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Text.Json;

namespace PerformanceMonitorLite.Mcp;

/// <summary>
/// Shared helpers for MCP tools.
/// </summary>
internal static class McpHelpers
{
    /// <summary>
    /// Maximum hours of history allowed (7 days).
    /// </summary>
    public const int MaxHoursBack = 168;

    /// <summary>
    /// Maximum rows/items to return.
    /// </summary>
    public const int MaxTop = 1000;

    /// <summary>
    /// Shared JSON serializer options with indented formatting.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Truncates a string to the specified maximum length, adding a truncation suffix.
    /// </summary>
    public static string? Truncate(string? value, int maxLength)
    {
        if (value == null || value.Length <= maxLength) return value;
        return value[..maxLength] + "... (truncated)";
    }

    /// <summary>
    /// Validates hours_back parameter. Returns null if valid, error message if invalid.
    /// </summary>
    public static string? ValidateHoursBack(int hoursBack)
    {
        if (hoursBack <= 0)
            return $"Invalid hours_back value '{hoursBack}'. Must be a positive integer (1-{MaxHoursBack}).";
        if (hoursBack > MaxHoursBack)
            return $"hours_back value '{hoursBack}' exceeds maximum of {MaxHoursBack} hours (7 days). Use a smaller value.";
        return null;
    }

    /// <summary>
    /// Validates top/limit parameter. Returns null if valid, error message if invalid.
    /// </summary>
    public static string? ValidateTop(int top, string paramName = "limit")
    {
        if (top <= 0)
            return $"Invalid {paramName} value '{top}'. Must be a positive integer (1-{MaxTop}).";
        if (top > MaxTop)
            return $"{paramName} value '{top}' exceeds maximum of {MaxTop}. Use a smaller value.";
        return null;
    }

    /// <summary>
    /// Formats an exception as a user-friendly error message.
    /// </summary>
    public static string FormatError(string operation, Exception ex)
    {
        return $"Error during {operation}: {ex.Message}";
    }
}
