/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1535 regression guard: the FinOps live server-inventory query must read edition/version/storage
/// from permission-free scalars and OBJECT_ID-guarded dynamic SQL only, never from
/// <c>sys.dm_os_sys_info</c>. On Azure SQL DB that DMV requires VIEW DATABASE STATE; when a
/// monitoring login lacked it the old combined query threw and the server lost its ENTIRE inventory
/// row (edition, version, storage — everything). This pins the split so inventory can never
/// re-couple to the DMV, and the hardware facts (the one part that needs it) stay in an isolated
/// best-effort read.
/// </summary>
public class FinOpsInventoryQueryTests
{
    [Fact]
    public void InventoryQuery_DoesNotDependOnDmOsSysInfo()
    {
        Assert.DoesNotContain("dm_os_sys_info", LocalDataService.InventoryQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareQuery_IsolatesTheDmvRead()
    {
        Assert.Contains("sys.dm_os_sys_info", LocalDataService.HardwareQueryText, StringComparison.Ordinal);
        // The hardware-only columns must ride the isolated read, not the inventory query.
        Assert.Contains("cpu_count", LocalDataService.HardwareQueryText, StringComparison.Ordinal);
        Assert.Contains("sqlserver_start_time", LocalDataService.HardwareQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryQuery_StillDetectsEditionAndStaysAzureSafeForStorage()
    {
        // Edition/version classification survives on permission-free scalars.
        Assert.Contains("SERVERPROPERTY('EngineEdition')", LocalDataService.InventoryQueryText, StringComparison.Ordinal);
        // Storage stays Azure-safe: the engine-edition branch reads sys.database_files on Azure SQL DB
        // (sys.master_files does not exist there).
        Assert.Contains("sys.database_files", LocalDataService.InventoryQueryText, StringComparison.Ordinal);
    }
}
