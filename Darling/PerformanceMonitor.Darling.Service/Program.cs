/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Service.Mcp;

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

/* CLI verb: validate darling.json and probe every configured server (reachability + permission pre-flight),
   reusing the same DarlingServerConnector probe the test_connect command runs. Optional second arg = an
   explicit config path (else the usual DARLING_CONFIG / next-to-binary resolution). Exit 0 iff all pass. */
if (args.Length > 0 && DarlingCliCommands.IsValidateConfigVerb(args[0]))
{
    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.ValidateConfigAsync(configPath, Console.Out, Console.Error, CancellationToken.None);
}

/* CLI verb: print a paste-ready remote-viewer connection string + the server TLS cert for the opt-in store
   network endpoint (darling-network-endpoints D8). It DPAPI-decrypts the network role's credential, so it is
   Windows-only (same guard shape as --encrypt-password). Optional second arg = an explicit config path. */
if (args.Length > 0 && DarlingCliCommands.IsPrintViewerConnectionVerb(args[0]))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--print-viewer-connection requires Windows (DPAPI).");
        return 1;
    }

    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.PrintViewerConnectionAsync(configPath, Console.Out, Console.Error, CancellationToken.None);
}

var builder = Host.CreateApplicationBuilder(args);

/* Windows-service lifetime is a no-op when run from a console, so the same exe
   serves interactive debugging and `sc create` installation (plan HP7/Phase 8). */
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PerformanceMonitor Darling";
});

builder.Services.AddHostedService<DarlingWorker>();

/* AN4: the analysis MCP tools over Streamable HTTP — registered always, self-gating on
   darling.json's mcp.enabled (default OFF), so Program.cs stays config-free like the worker. */
builder.Services.AddHostedService<DarlingMcpHostService>();

builder.Build().Run();
return 0;
