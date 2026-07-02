/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PerformanceMonitor.Darling.Service;

/* CLI verb: encrypt a SQL-auth password for darling.json. Reads from stdin (not an argument,
   so the plaintext never lands in shell history) and prints the DPAPI-LocalMachine blob. */
if (args.Length > 0 && string.Equals(args[0], "--encrypt-password", StringComparison.OrdinalIgnoreCase))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--encrypt-password requires Windows (DPAPI).");
        return 1;
    }

    Console.Error.Write("Password: ");
    var plaintext = Console.ReadLine();
    if (string.IsNullOrEmpty(plaintext))
    {
        Console.Error.WriteLine("No password read from stdin.");
        return 1;
    }

    Console.WriteLine(DarlingSecrets.Protect(plaintext));
    Console.Error.WriteLine("Paste the line above into the server's \"encryptedPassword\" in darling.json.");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

/* Windows-service lifetime is a no-op when run from a console, so the same exe
   serves interactive debugging and `sc create` installation (plan HP7/Phase 8). */
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PerformanceMonitor Darling";
});

builder.Services.AddHostedService<DarlingWorker>();

builder.Build().Run();
return 0;
