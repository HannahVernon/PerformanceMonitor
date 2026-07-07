/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the System Events tab (system_health parity, Stage 2b) against the Darling store contract, no live
/// Postgres. The Common <see cref="SystemHealthParser"/> shred is already tested; these cover the two things
/// Stage 2b adds: (1) the data-service reads — the load-bearing SQL clauses and column parity against the
/// generated collector DDL — and (2) <see cref="SystemEventSignificance"/>, the per-category
/// "significant / warning" filter that re-applies sp_HealthParser's WHERE predicates at the product's
/// canonical parameters (@warnings_only = 1, @wait_duration_ms = 500), plus the Severe-Errors database_id
/// resolution and the machine-local event-time projection.
/// </summary>
public sealed class ViewerSystemEventsTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SystemHealth", name));

    // ── Reads: SQL clause pins + column parity ──

    [Fact]
    public void SystemHealthEventsByTypeSql_ReadsViewByServerTypeAndEventTimeWindow()
    {
        var sql = ViewerDataService.SystemHealthEventsByTypeSql;

        Assert.Contains("FROM v_system_health_events", sql, StringComparison.Ordinal);
        Assert.Contains("event_xml", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        /* Windows on event_time (the XE @timestamp — the event's real time), NOT collection_time, and on the
           category's own event_type ($4), newest first. */
        Assert.Contains("event_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("event_time <= $3", sql, StringComparison.Ordinal);
        Assert.Contains("event_type = $4", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY event_time DESC", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseNameMapSql_LatestNamePerId_FromSizeStatsView()
    {
        var sql = ViewerDataService.DatabaseNameMapSql;

        Assert.Contains("DISTINCT ON (database_id)", sql, StringComparison.Ordinal);
        Assert.Contains("FROM v_database_size_stats", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY database_id, collection_time DESC", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ViewerDataService.SystemHealthEventsByTypeSql))]
    [InlineData(nameof(ViewerDataService.DatabaseNameMapSql))]
    public void SystemEventsReads_PgDialect_PositionalParams_NoTSqlNamedParams(string sqlName)
    {
        var sql = (string)typeof(ViewerDataService).GetField(sqlName)!.GetValue(null)!;

        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("now(", sql.ToLowerInvariant());
        Assert.Contains("$1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemHealthEventsRead_ReferencesColumnsThatExistInTheGeneratedTable()
    {
        var ddl = PgSchemaGenerator.CreateTable(SystemHealthEventsCollector.Instance);
        Assert.Equal("system_health_events", SystemHealthEventsCollector.Instance.TargetTable);

        foreach (var col in new[] { "event_xml", "event_type", "event_time", "collection_time", "server_id" })
        {
            Assert.Contains(col, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DatabaseNameMapRead_ReferencesColumnsThatExistInTheGeneratedTable()
    {
        var ddl = PgSchemaGenerator.CreateTable(DatabaseSizeStatsCollector.Instance);
        Assert.Equal("database_size_stats", DatabaseSizeStatsCollector.Instance.TargetTable);

        /* database_size_stats is the mapping source precisely because it carries BOTH id and name. */
        foreach (var col in new[] { "database_id", "database_name", "collection_time", "server_id" })
        {
            Assert.Contains(col, ddl, StringComparison.Ordinal);
        }
    }

    // ── Significance: Scheduler Issues (status = WARNING) ──

    [Theory]
    [InlineData("WARNING", true)]
    [InlineData("OK", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SchedulerIssue_SignificantOnlyWhenWarning(string? status, bool expected) =>
        Assert.Equal(expected, SystemEventSignificance.IsSignificant(new SchedulerIssueRecord { Status = status }));

    [Fact]
    public void SchedulerIssue_Fixture_IsSignificant()
    {
        var record = SystemHealthParser.ParseSchedulerIssue(LoadFixture("scheduler_monitor.xml"))!;
        Assert.Equal("WARNING", record.Status);
        Assert.True(SystemEventSignificance.IsSignificant(record));
    }

    // ── Significance: Severe Errors (severity >= 19, excluding 17830 / 18056) ──

    [Theory]
    [InlineData(24, 823, true)]     // fixture case
    [InlineData(19, 50000, true)]   // boundary — 19 is kept
    [InlineData(18, 50000, false)]  // below the warnings_only cutoff
    [InlineData(16, 50000, false)]  // the always-on floor, but not "warning"
    [InlineData(25, 17830, false)]  // severe but on the ignore list
    [InlineData(20, 18056, false)]  // severe but on the ignore list
    public void SevereError_SignificantByCutoffAndIgnoreList(int severity, int errorNumber, bool expected) =>
        Assert.Equal(expected, SystemEventSignificance.IsSignificant(
            new SevereErrorRecord { Severity = severity, ErrorNumber = errorNumber }));

    [Fact]
    public void SevereError_NullSeverity_IsNotSignificant() =>
        Assert.False(SystemEventSignificance.IsSignificant(new SevereErrorRecord { Severity = null, ErrorNumber = 823 }));

    [Fact]
    public void SevereError_HighSeverity_NullErrorNumber_IsSignificant() =>
        Assert.True(SystemEventSignificance.IsSignificant(new SevereErrorRecord { Severity = 20, ErrorNumber = null }));

    [Fact]
    public void SevereError_Fixture_IsSignificant()
    {
        var record = SystemHealthParser.ParseSevereError(LoadFixture("error_reported.xml"))!;
        Assert.True(SystemEventSignificance.IsSignificant(record));
    }

    // ── Significance: Memory Conditions / Memory Broker (RESOURCE_MEMPHYSICAL_LOW) ──

    [Theory]
    [InlineData("RESOURCE_MEMPHYSICAL_LOW", true)]
    [InlineData("RESOURCE_MEMPHYSICAL_HIGH", false)]
    [InlineData(null, false)]
    public void MemoryConditions_SignificantOnlyWhenLow(string? lastNotification, bool expected) =>
        Assert.Equal(expected, SystemEventSignificance.IsSignificant(
            new MemoryConditionsRecord { LastNotification = lastNotification }));

    [Theory]
    [InlineData("RESOURCE_MEMPHYSICAL_LOW", true)]
    [InlineData("RESOURCE_MEMPHYSICAL_HIGH", false)]
    [InlineData(null, false)]
    public void MemoryBroker_SignificantOnlyWhenLow(string? notification, bool expected) =>
        Assert.Equal(expected, SystemEventSignificance.IsSignificant(
            new MemoryBrokerRecord { Notification = notification }));

    [Fact]
    public void MemoryConditions_HighFixture_IsNotSignificant()
    {
        // The representative fixture is RESOURCE_MEMPHYSICAL_HIGH, so the warnings_only filter drops it.
        var record = SystemHealthParser.ParseMemoryConditions(LoadFixture("sp_server_diagnostics_resource.xml"))!;
        Assert.Equal("RESOURCE_MEMPHYSICAL_HIGH", record.LastNotification);
        Assert.False(SystemEventSignificance.IsSignificant(record));
    }

    [Fact]
    public void MemoryBroker_HighFixture_IsNotSignificant()
    {
        var record = SystemHealthParser.ParseMemoryBroker(LoadFixture("memory_broker.xml"))!;
        Assert.Equal("RESOURCE_MEMPHYSICAL_HIGH", record.Notification);
        Assert.False(SystemEventSignificance.IsSignificant(record));
    }

    // ── Significance: Memory Node OOM (never filtered) ──

    [Fact]
    public void MemoryNodeOom_AlwaysSignificant_EvenEmpty() =>
        Assert.True(SystemEventSignificance.IsSignificant(new MemoryNodeOomRecord()));

    [Fact]
    public void MemoryNodeOom_Fixture_IsSignificant()
    {
        var record = SystemHealthParser.ParseMemoryNodeOom(LoadFixture("memory_node_oom.xml"))!;
        Assert.True(SystemEventSignificance.IsSignificant(record));
    }

    // ── Significance: Significant Waits (session, non-BACKUP, >= 500 ms, not on ignore list) ──

    [Fact]
    public void SignificantWait_Fixture_IsSignificant()
    {
        var record = SystemHealthParser.ParseSignificantWait(LoadFixture("wait_info.xml"))!;
        Assert.True(SystemEventSignificance.IsSignificant(record));
    }

    private static SignificantWaitRecord Wait(
        string? waitType = "PAGEIOLATCH_SH", long? duration = 1500, int? session = 57, string? sql = "SELECT 1") =>
        new() { WaitType = waitType, DurationMs = duration, SessionId = session, QueryText = sql };

    [Fact]
    public void SignificantWait_DurationBoundary_500Kept_499Dropped()
    {
        Assert.True(SystemEventSignificance.IsSignificant(Wait(duration: 500)));
        Assert.False(SystemEventSignificance.IsSignificant(Wait(duration: 499)));
        Assert.False(SystemEventSignificance.IsSignificant(Wait(duration: null)));
    }

    [Fact]
    public void SignificantWait_SystemSession0_IsDropped() =>
        Assert.False(SystemEventSignificance.IsSignificant(Wait(session: 0)));

    [Fact]
    public void SignificantWait_NoStatementOrBackup_IsDropped()
    {
        Assert.False(SystemEventSignificance.IsSignificant(Wait(sql: null)));
        Assert.False(SystemEventSignificance.IsSignificant(Wait(sql: "")));
        Assert.False(SystemEventSignificance.IsSignificant(Wait(sql: "BACKUP DATABASE foo TO DISK = 'x'")));
        Assert.False(SystemEventSignificance.IsSignificant(Wait(sql: "backup log bar")));  // case-insensitive
    }

    [Fact]
    public void SignificantWait_IgnoredWaitType_IsDropped()
    {
        // A long wait on an idle/background type on the ignore list is not significant, even at 1000 ms.
        Assert.False(SystemEventSignificance.IsSignificant(Wait(waitType: "CHECKPOINT_QUEUE", duration: 1000)));
        Assert.False(SystemEventSignificance.IsSignificant(Wait(waitType: "checkpoint_queue", duration: 1000))); // case-insensitive
    }

    [Fact]
    public void IgnoredWaitTypes_ContainsKnownIdleWaits_NotRealWaits()
    {
        Assert.Contains("CHECKPOINT_QUEUE", SystemEventSignificance.IgnoredWaitTypes);
        Assert.Contains("LAZYWRITER_SLEEP", SystemEventSignificance.IgnoredWaitTypes);
        Assert.Contains("XE_TIMER_EVENT", SystemEventSignificance.IgnoredWaitTypes);
        /* Real, actionable waits must NOT be on the ignore list. */
        Assert.DoesNotContain("PAGEIOLATCH_SH", SystemEventSignificance.IgnoredWaitTypes);
        Assert.DoesNotContain("LCK_M_X", SystemEventSignificance.IgnoredWaitTypes);
        Assert.DoesNotContain("CXPACKET", SystemEventSignificance.IgnoredWaitTypes);
    }

    // ── Significance: CPU Tasks (state = WARNING and pendingTasks >= 10) ──

    [Theory]
    [InlineData("WARNING", 10, true)]     // boundary — 10 is kept
    [InlineData("WARNING", 15, true)]     // fixture case
    [InlineData("WARNING", 9, false)]     // below the pending-task threshold
    [InlineData("WARNING", null, false)]  // no pending tasks -> dropped (exist-predicate fails)
    [InlineData("CLEAN", 100, false)]     // not a warning, even with many pending tasks
    [InlineData("HAS_ISSUES", 100, false)]
    [InlineData(null, 100, false)]
    public void CpuTasks_SignificantOnlyWhenWarningAndPendingAtThreshold(string? state, int? pendingTasks, bool expected) =>
        Assert.Equal(expected, SystemEventSignificance.IsSignificant(
            new CpuTasksRecord { State = state, PendingTasks = pendingTasks }));

    [Fact]
    public void CpuTasks_WarningFixture_IsSignificant()
    {
        var record = SystemHealthParser.ParseCpuTasks(LoadFixture("sp_server_diagnostics_query_processing_warning.xml"))!;
        Assert.True(SystemEventSignificance.IsSignificant(record));
    }

    [Fact]
    public void CpuTasks_CleanFixture_IsNotSignificant()
    {
        // The shared clean QUERY_PROCESSING fixture is CLEAN with zero pending tasks — the warnings filter drops it.
        var record = SystemHealthParser.ParseCpuTasks(LoadFixture("sp_server_diagnostics_query_processing.xml"))!;
        Assert.Equal("CLEAN", record.State);
        Assert.False(SystemEventSignificance.IsSignificant(record));
    }

    // ── Significance: I/O Issues (state = WARNING) ──

    [Theory]
    [InlineData("WARNING", true)]
    [InlineData("CLEAN", false)]
    [InlineData("HAS_ISSUES", false)]
    [InlineData(null, false)]
    public void IoIssues_SignificantOnlyWhenWarning(string? state, bool expected) =>
        Assert.Equal(expected, SystemEventSignificance.IsSignificant(new IoIssuesRecord { State = state }));

    [Fact]
    public void IoIssues_WarningFixture_AllRowsSignificant()
    {
        var rows = SystemHealthParser.ParseIoIssues(LoadFixture("sp_server_diagnostics_io_subsystem.xml"));
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(SystemEventSignificance.IsSignificant(r)));
    }

    // ── System Health (Corruption + Contention counter charts — NO significance filter) ──

    [Fact]
    public void SystemHealth_Fixture_ShredsBothCorruptionAndContentionCounters()
    {
        // The ONE SYSTEM-component record feeds BOTH chart sub-tabs: the Corruption Events charts read the
        // bad-page / dump / access-violation columns; the Contention Events charts read the non-yielding /
        // latch / spinlock / CPU columns. Pin that every column both tabs plot is present off one shred.
        var r = SystemHealthParser.ParseSystemHealth(LoadFixture("sp_server_diagnostics_system.xml"))!;

        // Corruption Events columns.
        Assert.Equal(6L, r.BadPagesDetected);
        Assert.Equal(2L, r.IntervalDumpRequests);
        Assert.Equal(1L, r.IsAccessViolationOccurred);
        Assert.Equal(3L, r.WriteAccessViolationCount);
        // Contention Events columns.
        Assert.Equal(4L, r.NonYieldingTasksReported);
        Assert.Equal(7L, r.LatchWarnings);
        Assert.Equal("SOS_CACHESTORE", r.SickSpinlockType);
        Assert.Equal(12345L, r.SpinlockBackoffs);
        Assert.Equal(65L, r.SystemCpuUtilization);
        Assert.Equal(40L, r.SqlCpuUtilization);
    }

    [Fact]
    public void SystemHealth_CleanSnapshot_IsStillKept_NoWarningsOnlyGate()
    {
        // Unlike the grid categories, the Corruption / Contention charts plot EVERY collected snapshot over
        // time — there is no significance / warnings-only filter (GetSystemHealthAsync keeps every SYSTEM
        // record with a timestamp). The representative fixture is a CLEAN snapshot, and it is a real record
        // (not dropped), so the counter charts render it. This is the design contrast with the filtered grids.
        var r = SystemHealthParser.ParseSystemHealth(LoadFixture("sp_server_diagnostics_system.xml"));

        Assert.NotNull(r);
        Assert.Equal("CLEAN", r!.State);
        Assert.True(r.EventTime.HasValue);   // GetSystemHealthAsync requires a timestamp to sit on the time axis
    }

    [Fact]
    public void SystemHealth_NonSystemComponent_YieldsNothing()
    {
        // The SYSTEM read shares the sp_server_diagnostics feed with Memory Conditions / CPU Tasks / I/O
        // Issues; only the SYSTEM component contributes a record (the others parse to null, so the charts
        // never plot a RESOURCE / QUERY_PROCESSING / IO_SUBSYSTEM snapshot).
        Assert.Null(SystemHealthParser.ParseSystemHealth(LoadFixture("sp_server_diagnostics_resource.xml")));
        Assert.Null(SystemHealthParser.ParseSystemHealth(LoadFixture("sp_server_diagnostics_query_processing.xml")));
        Assert.Null(SystemHealthParser.ParseSystemHealth(LoadFixture("sp_server_diagnostics_io_subsystem.xml")));
    }

    // ── End-to-end: shred the fixtures then apply the filters (the data-service path's logic) ──

    [Fact]
    public void ShredThenFilter_KeepsOnlySignificantRows_AcrossAllCategories()
    {
        var events = new (string?, string?)[]
        {
            (SystemHealthParser.SchedulerMonitorEvent, LoadFixture("scheduler_monitor.xml")),
            (SystemHealthParser.ErrorReportedEvent, LoadFixture("error_reported.xml")),
            (SystemHealthParser.SpServerDiagnosticsEvent, LoadFixture("sp_server_diagnostics_system.xml")),
            (SystemHealthParser.SpServerDiagnosticsEvent, LoadFixture("sp_server_diagnostics_resource.xml")),
            (SystemHealthParser.SpServerDiagnosticsEvent, LoadFixture("sp_server_diagnostics_query_processing.xml")),
            (SystemHealthParser.SpServerDiagnosticsEvent, LoadFixture("sp_server_diagnostics_query_processing_warning.xml")),
            (SystemHealthParser.SpServerDiagnosticsEvent, LoadFixture("sp_server_diagnostics_io_subsystem.xml")),
            (SystemHealthParser.MemoryBrokerEvent, LoadFixture("memory_broker.xml")),
            (SystemHealthParser.MemoryNodeOomEvent, LoadFixture("memory_node_oom.xml")),
            (SystemHealthParser.WaitInfoEvent, LoadFixture("wait_info.xml")),
        };

        var result = SystemHealthParser.ParseEvents(events);

        // Scheduler (WARNING), Severe Error (sev 24), Memory Node OOM (ungated), and the wait all pass.
        Assert.Single(result.SchedulerIssues.Where(SystemEventSignificance.IsSignificant));
        Assert.Single(result.SevereErrors.Where(SystemEventSignificance.IsSignificant));
        Assert.Single(result.MemoryNodeOom.Where(SystemEventSignificance.IsSignificant));
        Assert.Single(result.SignificantWaits.Where(SystemEventSignificance.IsSignificant));
        // The memory-conditions / broker fixtures are RESOURCE_MEMPHYSICAL_HIGH — the warnings filter drops them.
        Assert.Empty(result.MemoryConditions.Where(SystemEventSignificance.IsSignificant));
        Assert.Empty(result.MemoryBroker.Where(SystemEventSignificance.IsSignificant));
        // CPU: the WARNING+15-pending event passes; the CLEAN one is dropped. I/O: both WARNING file rows pass.
        Assert.Single(result.CpuTasks.Where(SystemEventSignificance.IsSignificant));
        Assert.Equal(2, result.IoIssues.Count(SystemEventSignificance.IsSignificant));
        // System Health (Corruption + Contention charts) has NO significance filter — the CLEAN SYSTEM
        // snapshot is kept, so the counter charts plot it (contrast with the dropped HIGH memory rows above).
        Assert.Single(result.SystemHealth);
    }

    // ── Severe-Errors database_id resolution ──

    [Fact]
    public void ResolveDatabaseName_MappedId_ReturnsName()
    {
        var map = new Dictionary<int, string> { [5] = "AdventureWorks", [1] = "master" };
        Assert.Equal("AdventureWorks", ViewerDataService.ResolveDatabaseName(5, map));
        Assert.Equal("master", ViewerDataService.ResolveDatabaseName(1, map));
    }

    [Fact]
    public void ResolveDatabaseName_NoContext_ZeroOrNull_IsBlank()
    {
        var map = new Dictionary<int, string> { [5] = "AdventureWorks" };
        Assert.Equal(string.Empty, ViewerDataService.ResolveDatabaseName(0, map));
        Assert.Equal(string.Empty, ViewerDataService.ResolveDatabaseName(null, map));
    }

    [Fact]
    public void ResolveDatabaseName_RealButUnmappedId_SurfacesTheRawId_NotBlank()
    {
        var map = new Dictionary<int, string> { [5] = "AdventureWorks" };
        // A database dropped before the latest size-stats snapshot: surface the id rather than silently blank.
        Assert.Equal("database_id 7", ViewerDataService.ResolveDatabaseName(7, map));
    }

    // ── Row projection: machine-local event time + resolved DB passthrough ──

    [Fact]
    public void Rows_EventTimeLocal_RendersTheUtcTimestampAsMachineLocal()
    {
        var utc = new DateTime(2026, 7, 5, 12, 0, 5, DateTimeKind.Utc);
        var expected = ViewerTimeHelper.ForDisplay(utc).ToString("yyyy-MM-dd HH:mm:ss");

        Assert.Equal(expected, new SchedulerIssueRow(new SchedulerIssueRecord { EventTime = utc }).EventTimeLocal);
        Assert.Equal(expected, new SignificantWaitRow(new SignificantWaitRecord { EventTime = utc }).EventTimeLocal);
    }

    [Fact]
    public void Rows_NullEventTime_RendersEmpty() =>
        Assert.Equal(string.Empty, new MemoryBrokerRow(new MemoryBrokerRecord { EventTime = null }).EventTimeLocal);

    [Fact]
    public void SevereErrorRow_CarriesResolvedDatabaseName()
    {
        var record = new SevereErrorRecord { ErrorNumber = 823, Severity = 24, DatabaseId = 5 };
        var row = new SevereErrorRow(record, "AdventureWorks");

        Assert.Equal("AdventureWorks", row.DatabaseName);
        Assert.Equal(5, row.DatabaseId);
        Assert.Equal(823, row.ErrorNumber);
    }

    [Fact]
    public void CpuTasksRow_And_IoIssuesRow_ProjectRecordFields()
    {
        var utc = new DateTime(2026, 7, 5, 12, 6, 0, DateTimeKind.Utc);
        var expected = ViewerTimeHelper.ForDisplay(utc).ToString("yyyy-MM-dd HH:mm:ss");

        var cpu = new CpuTasksRow(new CpuTasksRecord
            { EventTime = utc, State = "WARNING", PendingTasks = 15, DidBlockingOccur = true });
        Assert.Equal(expected, cpu.EventTimeLocal);
        Assert.Equal("WARNING", cpu.State);
        Assert.Equal(15L, cpu.PendingTasks);
        Assert.True(cpu.DidBlockingOccur);

        var io = new IoIssuesRow(new IoIssuesRecord
            { EventTime = utc, State = "WARNING", LongestPendingRequestsDurationMs = 1500, LongestPendingRequestsFilePath = @"D:\data\prod.mdf" });
        Assert.Equal(expected, io.EventTimeLocal);
        Assert.Equal("WARNING", io.State);
        Assert.Equal(1500L, io.LongestPendingRequestsDurationMs);
        Assert.Equal(@"D:\data\prod.mdf", io.LongestPendingRequestsFilePath);
    }
}
