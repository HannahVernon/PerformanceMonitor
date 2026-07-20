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
/// #1535 regression guard: the connectivity probe's edition-detection query must classify a server
/// (engine edition, version, RDS, msdb access) from permission-free scalars ONLY, never from
/// <c>sys.dm_os_sys_info</c>. On Azure SQL DB that DMV requires VIEW DATABASE STATE; when a
/// monitoring login lacked it, the old combined query threw and left EngineEdition = 0, so
/// <c>IsAzureSqlDb</c> read false and every collector ran its on-prem shape (erroring on
/// dm_os_sys_memory / sys.traces / dm_xe_session_targets / msdb). This pins the split so detection
/// can never re-couple to the DMV; <c>sqlserver_start_time</c> (the one column that needs the DMV)
/// lives in its own best-effort read.
/// </summary>
public class ServerManagerDetectionQueryTests
{
    [Fact]
    public void DetectionQuery_DoesNotDependOnDmOsSysInfo()
    {
        Assert.DoesNotContain("dm_os_sys_info", ServerManager.DetectionQueryText, StringComparison.Ordinal);
        // sqlserver_start_time is a column of that DMV, so it must not ride the detection query either.
        Assert.DoesNotContain("sqlserver_start_time", ServerManager.DetectionQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectionQuery_DetectsEngineEditionViaServerProperty()
    {
        Assert.Contains("SERVERPROPERTY('EngineEdition')", ServerManager.DetectionQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public void StartTimeQuery_IsolatesTheOnlyReadThatNeedsDmOsSysInfo()
    {
        Assert.Contains("sqlserver_start_time", ServerManager.ServerStartTimeQueryText, StringComparison.Ordinal);
        Assert.Contains("sys.dm_os_sys_info", ServerManager.ServerStartTimeQueryText, StringComparison.Ordinal);
    }
}
