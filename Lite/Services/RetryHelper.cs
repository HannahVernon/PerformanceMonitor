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

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Provides retry logic for transient SQL Server failures.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// SQL Server error numbers considered transient and retryable.
    /// </summary>
    private static readonly HashSet<int> TransientErrorNumbers = new()
    {
        -2,     // Timeout
        -1,     // General network error
        2,      // Timeout (alternative)
        53,     // Named pipe / network not found
        233,    // Connection closed
        10053,  // Transport-level error (connection was forcibly closed)
        10054,  // Connection reset by peer
        10060,  // Connection timed out
        10061,  // Connection refused
        40143,  // Azure SQL transient
        40197,  // Azure SQL transient - service encountered an error
        40501,  // Azure SQL - service is currently busy
        40613,  // Azure SQL - database not currently available
        49918,  // Azure SQL - not enough resources to process request
        49919,  // Azure SQL - cannot process create/update request
        49920   // Azure SQL - cannot process request (too many operations)
    };

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
