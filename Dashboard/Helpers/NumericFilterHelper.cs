/*
 * SQL Server Performance Monitor Dashboard
 *
 * Helper for parsing and evaluating numeric filter expressions
 * Supports: >, <, >=, <=, ranges (100-200), and exact values
 */

using System;
using System.Globalization;

namespace PerformanceMonitorDashboard.Helpers
{
    public static class NumericFilterHelper
    {
        public static bool MatchesFilter(object? value, string? filterText)
        {
            if (value == null || string.IsNullOrWhiteSpace(filterText))
                return true;

            filterText = filterText.Trim();

            // Try to convert the value to decimal
            if (!TryConvertToDecimal(value, out decimal numericValue))
                return true; // If can't convert, don't filter out

            // Check for range: "100-200" or "100..200"
            if (filterText.Contains('-', StringComparison.Ordinal) && !filterText.StartsWith('-'))
            {
                return EvaluateRange(numericValue, filterText);
            }
            else if (filterText.Contains("..", StringComparison.Ordinal))
            {
                return EvaluateRange(numericValue, filterText.Replace("..", "-", StringComparison.Ordinal));
            }
            // Check for >=
            else if (filterText.StartsWith(">=", StringComparison.Ordinal))
            {
                if (decimal.TryParse(filterText.Substring(2).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal threshold))
                    return numericValue >= threshold;
            }
            // Check for <=
            else if (filterText.StartsWith("<=", StringComparison.Ordinal))
            {
                if (decimal.TryParse(filterText.Substring(2).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal threshold))
                    return numericValue <= threshold;
            }
            // Check for >
            else if (filterText.StartsWith('>'))
            {
                if (decimal.TryParse(filterText.Substring(1).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal threshold))
                    return numericValue > threshold;
            }
            // Check for <
            else if (filterText.StartsWith('<'))
            {
                if (decimal.TryParse(filterText.Substring(1).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal threshold))
                    return numericValue < threshold;
            }
            // Exact match
            else
            {
                if (decimal.TryParse(filterText, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal threshold))
                    return Math.Abs(numericValue - threshold) < 0.01m; // Allow small floating point differences
            }

            return true; // If filter is invalid, don't filter out
        }

        private static bool TryConvertToDecimal(object value, out decimal result)
        {
            result = 0;

            if (value == null)
                return false;

            try
            {
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool EvaluateRange(decimal value, string rangeText)
        {
            var parts = rangeText.Split('-');
            if (parts.Length == 2)
            {
                if (decimal.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal min) &&
                    decimal.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal max))
                {
                    return value >= min && value <= max;
                }
            }
            // Handle negative numbers in range: e.g., "-100-200" means -100 to 200
            else if (parts.Length == 3 && string.IsNullOrEmpty(parts[0]))
            {
                if (decimal.TryParse("-" + parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal min) &&
                    decimal.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal max))
                {
                    return value >= min && value <= max;
                }
            }

            return true; // Invalid range, don't filter
        }
    }
}
