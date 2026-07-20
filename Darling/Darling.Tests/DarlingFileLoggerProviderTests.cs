/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1581 field finding 1: the rolling file log must fail LOUDLY, not silently. On the field box the log
/// directory turned unwritable (an ACL artifact) and <see cref="DarlingFileLoggerProvider"/> went silent — the
/// flush catch swallowed every write, and nobody learned until they went to tail it. The provider now surfaces a
/// file-logging failure ONCE through an injectable sink (production: a best-effort Windows Event Log Warning),
/// latched so a persistently-broken log emits a single event, not one per 5s flush. These pins drive the latch
/// through the injected sink and the internal <see cref="DarlingFileLoggerProvider.Flush"/> seam — no real Event
/// Log needed.
/// </summary>
public sealed class DarlingFileLoggerProviderTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "darling-filelog-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            else if (File.Exists(_tempRoot))
            {
                File.Delete(_tempRoot);
            }
        }
        catch
        {
            /* Best-effort test cleanup. */
        }
    }

    /// <summary>
    /// A healthy provider — a writable log directory, every flush succeeds — NEVER surfaces the failure
    /// fallback. The sink must stay untouched across construction and repeated flushes so a working box is never
    /// spuriously paged about its own logging.
    /// </summary>
    [Fact]
    public void HealthyProvider_NeverSurfacesFailure()
    {
        var reports = new ConcurrentQueue<string>();
        var logDir = Path.Combine(_tempRoot, "logs");

        using var provider = new DarlingFileLoggerProvider(logDir, reports.Enqueue);
        var logger = provider.CreateLogger("Test");

        for (var i = 0; i < 5; i++)
        {
            logger.LogInformation("healthy line {Index}", i);
            provider.Flush();
        }

        Assert.Empty(reports);
        /* Sanity: the flushes actually wrote — otherwise "never surfaced" would be a vacuous pass. */
        Assert.True(File.Exists(provider.CurrentLogFile()), "a healthy provider must have written its log file");
    }

    /// <summary>
    /// A constructor that cannot create/enable the log directory surfaces the failure EXACTLY once, and the
    /// disabled provider never surfaces it again on later flushes. The directory is unmakeable because its parent
    /// is a FILE — a deterministic, cross-platform <see cref="Directory.CreateDirectory(string)"/> failure.
    /// </summary>
    [Fact]
    public void ConstructorDirectoryFailure_SurfacesOnce()
    {
        var reports = new ConcurrentQueue<string>();
        Directory.CreateDirectory(_tempRoot);
        var blocker = Path.Combine(_tempRoot, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var unmakeableDir = Path.Combine(blocker, "logs");   /* parent is a file → CreateDirectory throws */

        using var provider = new DarlingFileLoggerProvider(unmakeableDir, reports.Enqueue);
        var logger = provider.CreateLogger("Test");

        /* A disabled provider drops enqueues and short-circuits flush, so later flushes must not add a second
           report. */
        for (var i = 0; i < 3; i++)
        {
            logger.LogInformation("line {Index}", i);
            provider.Flush();
        }

        Assert.Single(reports);
        Assert.True(reports.TryPeek(out var message), "the constructor failure must have surfaced a message");
        Assert.Contains("File logging is disabled", message);
    }

    /// <summary>
    /// The core latch pin: a provider that constructed healthy but whose directory then turned unwritable (the
    /// field's mid-run ACL artifact) surfaces the failure ONCE across MANY failing flushes — not one event per 5s
    /// flush. Each iteration enqueues a fresh line so every flush genuinely attempts (and fails) a write; the
    /// latch, not an emptied buffer, is what bounds the report to one.
    /// </summary>
    [Fact]
    public void RepeatedFlushFailures_SurfaceAtMostOnce()
    {
        var reports = new ConcurrentQueue<string>();
        var logDir = Path.Combine(_tempRoot, "logs");

        using var provider = new DarlingFileLoggerProvider(logDir, reports.Enqueue);
        var logger = provider.CreateLogger("Test");

        /* Break the directory the provider enabled against: replace it with a FILE so every subsequent
           File.AppendAllText(CurrentLogFile()) throws (its parent is no longer a directory). */
        Directory.Delete(logDir, recursive: true);
        File.WriteAllText(logDir, "now a file");

        for (var i = 0; i < 6; i++)
        {
            logger.LogInformation("failing line {Index}", i);
            provider.Flush();
        }

        Assert.Single(reports);
    }
}
