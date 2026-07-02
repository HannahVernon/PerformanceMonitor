/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// M0 scaffold worker: proves the Worker host + Windows-service lifetime wiring builds and runs.
/// The collection loop replaces this once the shared collector definitions
/// (PerformanceMonitor.Collectors) land — see the headless plan, v5.1 build order.
/// </summary>
public sealed class DarlingWorker : BackgroundService
{
    private readonly ILogger<DarlingWorker> _logger;

    public DarlingWorker(ILogger<DarlingWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PerformanceMonitor Darling service scaffold started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            _logger.LogDebug("Darling scaffold heartbeat");
        }
    }
}
