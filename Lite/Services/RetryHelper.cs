/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Common;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Provides retry logic for transient SQL Server failures.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// SQL Server error numbers considered transient and retryable. Owned by the shared
    /// <see cref="SqlErrorClassification"/> so Lite and Darling cannot drift on it, and so it stays
    /// provably disjoint from the errors treated as permanent verdicts — an error that means both
    /// "retry this" and "give up forever" is exactly what caused issue #1506.
    /// </summary>
    internal static IReadOnlySet<int> TransientErrorNumbers => SqlErrorClassification.TransientErrorNumbers;

    /// <summary>
    /// Default maximum number of retry attempts.
    /// </summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>
    /// Executes an async operation with retry logic for transient SQL errors.
    /// Uses exponential backoff: 1s, 2s, 4s.
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger? logger = null,
        string? operationName = null,
        int maxRetries = DefaultMaxRetries,
        CancellationToken cancellationToken = default)
    {
        var lastException = (Exception?)null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await operation();
            }
            catch (SqlException ex) when (attempt < maxRetries && IsTransient(ex))
            {
                lastException = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));

                logger?.LogWarning(
                    "Transient SQL error (#{ErrorNumber}) on attempt {Attempt}/{MaxRetries} for '{Operation}': {Message}. Retrying in {Delay}s",
                    ex.Number, attempt + 1, maxRetries + 1, operationName ?? "unknown", ex.Message, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException!;
    }

    /// <summary>
    /// Executes an async operation (no return value) with retry logic.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        ILogger? logger = null,
        string? operationName = null,
        int maxRetries = DefaultMaxRetries,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return 0;
        }, logger, operationName, maxRetries, cancellationToken);
    }

    /// <summary>
    /// Determines if a SqlException represents a transient error that can be retried.
    /// </summary>
    public static bool IsTransient(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (TransientErrorNumbers.Contains(error.Number))
            {
                return true;
            }
        }

        return false;
    }
}
