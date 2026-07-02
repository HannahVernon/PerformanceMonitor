/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PerformanceMonitor.Darling.Service;

var builder = Host.CreateApplicationBuilder(args);

/* Windows-service lifetime is a no-op when run from a console, so the same exe
   serves interactive debugging and `sc create` installation (plan HP7/Phase 8). */
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PerformanceMonitor Darling";
});

builder.Services.AddHostedService<DarlingWorker>();

builder.Build().Run();
