/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using PerformanceMonitor.Common;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Tests for the shared <see cref="SystemHealthParser"/> — the monitor-side C# port of Erik's
/// <c>sp_HealthParser</c> per-category xpath shred. Each of the six warning categories is asserted
/// column-by-column against a representative captured event (Fixtures/SystemHealth/*.xml, shaped to
/// sp_HealthParser's xpath + the MS event schema), plus dispatch, robustness, the non-RESOURCE
/// sp_server_diagnostics no-op, and the sql_text control-character cleanup. Tested once in Common (not
/// duplicated per app). No database.
/// </summary>
public class SystemHealthParserTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SystemHealth", name));

    private static readonly DateTime SchedulerTime = new(2026, 7, 5, 11, 58, 0, 123, DateTimeKind.Utc);
    private static readonly DateTime ErrorTime = new(2026, 7, 5, 12, 0, 5, 500, DateTimeKind.Utc);
    private static readonly DateTime ResourceTime = new(2026, 7, 5, 12, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BrokerTime = new(2026, 7, 5, 12, 2, 10, 250, DateTimeKind.Utc);
    private static readonly DateTime OomTime = new(2026, 7, 5, 12, 3, 20, 750, DateTimeKind.Utc);
    private static readonly DateTime WaitTime = new(2026, 7, 5, 12, 4, 30, 900, DateTimeKind.Utc);

    // ── SchedulerIssues (scheduler_monitor_system_health_ring_buffer_recorded) ──

    [Fact]
    public void SchedulerIssue_ShredsEveryColumn()
    {
        var r = SystemHealthParser.ParseSchedulerIssue(LoadFixture("scheduler_monitor.xml"));

        Assert.NotNull(r);
        Assert.Equal(SchedulerTime, r!.EventTime);
        Assert.Equal(10, r.SchedulerId);
        Assert.Equal(3, r.CpuId);
        Assert.Equal("WARNING", r.Status);          // data[@name="status"]/text, NOT the numeric value
        Assert.True(r.IsOnline);
        Assert.False(r.IsRunnable);
        Assert.True(r.IsRunning);
        Assert.Equal(4500L, r.NonYieldingTimeMs);    // data[@name="non_yielding_time"] (no _ms suffix in XML)
        Assert.Equal(4L, r.ThreadQuantumMs);         // data[@name="thread_quantum"]
    }

    // ── SevereErrors (error_reported) ──

    [Fact]
    public void SevereError_ShredsEveryColumn()
    {
        var r = SystemHealthParser.ParseSevereError(LoadFixture("error_reported.xml"));

        Assert.NotNull(r);
        Assert.Equal(ErrorTime, r!.EventTime);
        Assert.Equal(823, r.ErrorNumber);
        Assert.Equal(24, r.Severity);
        Assert.Equal(2, r.State);
        Assert.Equal(
            "The operating system returned error 21 to SQL Server during a read at offset 0x00000c60000 in file 'D:\\data\\prod.mdf'.",
            r.Message);
        Assert.Equal(6, r.DatabaseId);
        // DB_NAME(database_id) is a server-side resolution a DB-free shred cannot do — always null here.
        Assert.Null(r.DatabaseName);
    }

    // ── MemoryConditions (sp_server_diagnostics_component_result / RESOURCE) ──

    [Fact]
    public void MemoryConditions_ShredsResourceAttributesAndReportName()
    {
        var r = SystemHealthParser.ParseMemoryConditions(LoadFixture("sp_server_diagnostics_resource.xml"));

        Assert.NotNull(r);
        Assert.Equal(ResourceTime, r!.EventTime);
        Assert.Equal("RESOURCE_MEMPHYSICAL_HIGH", r.LastNotification);
        Assert.Equal(0L, r.OutOfMemoryExceptions);
        Assert.False(r.IsAnyPoolOutOfMemory);
        Assert.Equal(0L, r.ProcessOutOfMemoryPeriod);
        Assert.Equal("Process/System Counts", r.Name);   // first memoryReport/@name
    }

    [Fact]
    public void MemoryConditions_ByteEntries_DivideBy1024Cubed()
    {
        var r = SystemHealthParser.ParseMemoryConditions(LoadFixture("sp_server_diagnostics_resource.xml"))!;

        Assert.Equal(8L, r.AvailablePhysicalMemoryGb);   // 8589934592 B / 1024^3
        Assert.Equal(16L, r.AvailableVirtualMemoryGb);   // 17179869184 B / 1024^3
        Assert.Equal(4L, r.AvailablePagingFileGb);       // 4294967296 B / 1024^3
        Assert.Equal(2L, r.WorkingSetGb);                // 2147483648 B / 1024^3
    }

    [Fact]
    public void MemoryConditions_KbEntries_DivideBy1024Squared_Truncating()
    {
        var r = SystemHealthParser.ParseMemoryConditions(LoadFixture("sp_server_diagnostics_resource.xml"))!;

        Assert.Equal(8L, r.VmReservedGb);                // 8388608 KB / 1024^2
        Assert.Equal(4L, r.VmCommittedGb);               // 4194304 KB / 1024^2
        Assert.Equal(6L, r.TargetCommittedGb);           // 6291456 KB / 1024^2
        Assert.Equal(4L, r.CurrentCommittedGb);          // 4194304 KB / 1024^2
        // Sub-GB KB values truncate to 0, exactly like sp_HealthParser's bigint integer division.
        Assert.Equal(0L, r.EmergencyMemoryGb);           // 1024 KB / 1024^2 -> 0
        Assert.Equal(0L, r.EmergencyMemoryInUseGb);      // 16 KB / 1024^2 -> 0
    }

    [Fact]
    public void MemoryConditions_RawEntries_NotConverted()
    {
        var r = SystemHealthParser.ParseMemoryConditions(LoadFixture("sp_server_diagnostics_resource.xml"))!;

        Assert.Equal(99L, r.PercentOfCommittedMemoryInWs);
        Assert.Equal(123456L, r.PageFaults);
        Assert.Equal(1L, r.SystemPhysicalMemoryHigh);
        Assert.Equal(0L, r.SystemPhysicalMemoryLow);
        Assert.Equal(0L, r.ProcessPhysicalMemoryLow);
        Assert.Equal(0L, r.ProcessVirtualMemoryLow);
        Assert.Equal(0L, r.LockedPagesAllocated);
        Assert.Equal(0L, r.LargePagesAllocated);
        Assert.Equal(524288L, r.PagesAllocated);
        Assert.Equal(0L, r.PagesReserved);
        Assert.Equal(1024L, r.PagesFree);
        Assert.Equal(200000L, r.PagesInUse);
        Assert.Equal(700000L, r.PageAllocPotential);
        Assert.Equal(0L, r.NumaGrowthPhase);
        Assert.Equal(0L, r.LastOomFactor);
        Assert.Equal(0L, r.LastOsError);
    }

    [Fact]
    public void MemoryConditions_NonResourceComponent_YieldsNoRecord()
    {
        // A QUERY_PROCESSING sp_server_diagnostics component carries no <resource> node — no memory
        // conditions to report (sp_HealthParser's CROSS APPLY /event/data/value/resource returns nothing).
        var r = SystemHealthParser.ParseMemoryConditions(LoadFixture("sp_server_diagnostics_query_processing.xml"));

        Assert.Null(r);
    }

    // ── MemoryBroker (memory_broker_ring_buffer_recorded) ──

    [Fact]
    public void MemoryBroker_ShredsEveryColumn()
    {
        var r = SystemHealthParser.ParseMemoryBroker(LoadFixture("memory_broker.xml"));

        Assert.NotNull(r);
        Assert.Equal(BrokerTime, r!.EventTime);
        Assert.Equal(1L, r.BrokerId);                    // data[@name="id"]
        Assert.Equal(0L, r.PoolMetadataId);
        Assert.Equal(30000L, r.DeltaTime);
        Assert.Equal(85L, r.MemoryRatio);
        Assert.Equal(1048576L, r.NewTarget);
        Assert.Equal(2097152L, r.Overall);
        Assert.Equal(512L, r.Rate);
        Assert.Equal(900000L, r.CurrentlyPredicated);
        Assert.Equal(1000000L, r.CurrentlyAllocated);
        Assert.Equal(1100000L, r.PreviouslyAllocated);
        Assert.Equal("MEMORYBROKER_FOR_CACHE", r.Broker);
        Assert.Equal("RESOURCE_MEMPHYSICAL_HIGH", r.Notification);
    }

    // ── MemoryNodeOOM (memory_node_oom_ring_buffer_recorded) ──

    [Fact]
    public void MemoryNodeOom_ShredsEveryColumn()
    {
        var r = SystemHealthParser.ParseMemoryNodeOom(LoadFixture("memory_node_oom.xml"));

        Assert.NotNull(r);
        Assert.Equal(OomTime, r!.EventTime);
        Assert.Equal(0L, r.NodeId);                      // data[@name="id"]
        Assert.Equal(0L, r.MemoryNodeId);
        Assert.Equal(72L, r.MemoryUtilizationPct);
        Assert.Equal(16777216L, r.TotalPhysicalMemoryKb);
        Assert.Equal(4194304L, r.AvailablePhysicalMemoryKb);
        Assert.Equal(25165824L, r.TotalPageFileKb);
        Assert.Equal(8388608L, r.AvailablePageFileKb);
        Assert.Equal(137438953472L, r.TotalVirtualAddressSpaceKb);   // > int32 — proves long handling
        Assert.Equal(137000000000L, r.AvailableVirtualAddressSpaceKb);
        Assert.Equal(12582912L, r.TargetKb);
        Assert.Equal(1048576L, r.ReservedKb);
        Assert.Equal(10485760L, r.CommittedKb);
        Assert.Equal(2048m, r.SharedCommittedKb);        // numeric(38,0) -> decimal
        Assert.Equal(0L, r.AweKb);
        Assert.Equal(9437184L, r.PagesKb);
        Assert.Equal("NUMA_NODE_UNAVAILABLE", r.FailureType);   // data[@name="failure"]/text
        Assert.Equal(2L, r.FailureValue);                        // data[@name="failure"]/value
        Assert.Equal(1L, r.Resources);
        Assert.Equal("FACTORY_PROCESS", r.FactorText);           // data[@name="factor"]/text
        Assert.Equal(3L, r.FactorValue);                         // data[@name="factor"]/value
        Assert.Equal(0L, r.LastError);
        Assert.Equal(0L, r.PoolMetadataId);
        // is_* flags kept as raw text (nvarchar(10)), exactly as sp_HealthParser stores them.
        Assert.Equal("true", r.IsProcessInJob);
        Assert.Equal("false", r.IsSystemPhysicalMemoryHigh);
        Assert.Equal("true", r.IsSystemPhysicalMemoryLow);
        Assert.Equal("false", r.IsProcessPhysicalMemoryLow);
        Assert.Equal("false", r.IsProcessVirtualMemoryLow);
    }

    // ── SignificantWaits (wait_info) ──

    [Fact]
    public void SignificantWait_ShredsEveryColumn()
    {
        var r = SystemHealthParser.ParseSignificantWait(LoadFixture("wait_info.xml"));

        Assert.NotNull(r);
        Assert.Equal(WaitTime, r!.EventTime);
        Assert.Equal("PAGEIOLATCH_SH", r.WaitType);      // data[@name="wait_type"]/text
        Assert.Equal(1500L, r.DurationMs);
        Assert.Equal(12L, r.SignalDurationMs);
        Assert.Equal("7:1:12345", r.WaitResource);
        Assert.Equal(57, r.SessionId);                   // action[@name="session_id"]
        Assert.Equal("SELECT * FROM dbo.big_table WHERE id = @p1;", r.QueryText);  // action[@name="sql_text"]
    }

    [Fact]
    public void SignificantWait_CleansControlCharactersButKeepsTabCrLf()
    {
        // Real sql_text carries control chars (SQL Server serializes them as &#xNN; references). The parser
        // tolerates them (CheckCharacters=false) and CleanSqlText replaces every control char EXCEPT tab
        // (9), LF (10) and CR (13) with '?', exactly as sp_HealthParser's query_text REPLACE chain does.
        const string xml =
            "<event name=\"wait_info\" package=\"sqlserver\" timestamp=\"2026-07-05T12:04:30.900Z\">" +
            "<data name=\"duration\"><value>800</value></data>" +
            "<action name=\"sql_text\" package=\"sqlserver\"><value>SELECT&#x01;1&#x1f;FROM&#x09;t&#x0d;&#x0a;GO</value></action>" +
            "</event>";

        var r = SystemHealthParser.ParseSignificantWait(xml);

        Assert.NotNull(r);
        Assert.Equal("SELECT?1?FROM\tt\r\nGO", r!.QueryText);
    }

    // ── dispatch (ParseEvents / ParseInto) ──

    [Fact]
    public void ParseEvents_DispatchesEachEventToItsCategory()
    {
        var events = new (string?, string?)[]
        {
            (SystemHealthParser.SchedulerMonitorEvent, LoadFixture("scheduler_monitor.xml")),
            (SystemHealthParser.ErrorReportedEvent, LoadFixture("error_reported.xml")),
            (SystemHealthParser.SpServerDiagnosticsEvent, LoadFixture("sp_server_diagnostics_resource.xml")),
            (SystemHealthParser.SpServerDiagnosticsEvent, LoadFixture("sp_server_diagnostics_query_processing.xml")),
            (SystemHealthParser.MemoryBrokerEvent, LoadFixture("memory_broker.xml")),
            (SystemHealthParser.MemoryNodeOomEvent, LoadFixture("memory_node_oom.xml")),
            (SystemHealthParser.WaitInfoEvent, LoadFixture("wait_info.xml")),
        };

        var result = SystemHealthParser.ParseEvents(events);

        Assert.Single(result.SchedulerIssues);
        Assert.Single(result.SevereErrors);
        Assert.Single(result.MemoryBroker);
        Assert.Single(result.MemoryNodeOom);
        Assert.Single(result.SignificantWaits);
        // Two sp_server_diagnostics events in, but only the RESOURCE one yields a memory-conditions row.
        Assert.Single(result.MemoryConditions);
        Assert.Equal(10, result.SchedulerIssues[0].SchedulerId);
        Assert.Equal(823, result.SevereErrors[0].ErrorNumber);
    }

    [Fact]
    public void ParseEvents_IgnoresUnknownEventTypes()
    {
        var events = new (string?, string?)[]
        {
            ("xml_deadlock_report", "<event name=\"xml_deadlock_report\"><data name=\"x\"><value>1</value></data></event>"),
            ("connectivity_ring_buffer_recorded", "<event name=\"connectivity_ring_buffer_recorded\" />"),
        };

        var result = SystemHealthParser.ParseEvents(events);

        Assert.Empty(result.SchedulerIssues);
        Assert.Empty(result.SevereErrors);
        Assert.Empty(result.MemoryConditions);
        Assert.Empty(result.MemoryBroker);
        Assert.Empty(result.MemoryNodeOom);
        Assert.Empty(result.SignificantWaits);
    }

    [Fact]
    public void ParseInto_FallsBackToEventNameWhenEventTypeMissing()
    {
        // event_type null -> dispatch on the XML's own @name attribute.
        var result = new SystemHealthParseResult();

        SystemHealthParser.ParseInto(null, LoadFixture("wait_info.xml"), result);

        Assert.Single(result.SignificantWaits);
        Assert.Equal("PAGEIOLATCH_SH", result.SignificantWaits[0].WaitType);
    }

    // ── robustness (mirrors DeadlockGraphParser's never-throw contract) ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<event name=\"wait_info\"")]   // truncated / not well-formed
    [InlineData("not xml at all")]
    public void Parse_MalformedOrEmpty_ReturnsNullNeverThrows(string? xml)
    {
        Assert.Null(SystemHealthParser.ParseSchedulerIssue(xml));
        Assert.Null(SystemHealthParser.ParseSevereError(xml));
        Assert.Null(SystemHealthParser.ParseMemoryConditions(xml));
        Assert.Null(SystemHealthParser.ParseMemoryBroker(xml));
        Assert.Null(SystemHealthParser.ParseMemoryNodeOom(xml));
        Assert.Null(SystemHealthParser.ParseSignificantWait(xml));
    }

    [Fact]
    public void Parse_WellFormedButMissingDataNodes_YieldsRecordWithNullFields()
    {
        // A valid <event> with none of the expected data nodes still shreds (pure port of the CROSS APPLY
        // //event, which emits one row per event before any WHERE filter) — every field simply comes back null.
        var r = SystemHealthParser.ParseMemoryBroker("<event name=\"memory_broker_ring_buffer_recorded\" />");

        Assert.NotNull(r);
        Assert.Null(r!.EventTime);
        Assert.Null(r.BrokerId);
        Assert.Null(r.Broker);
        Assert.Null(r.Notification);
    }

    [Fact]
    public void ParseEvents_NullSequence_ReturnsEmptyResult()
    {
        var result = SystemHealthParser.ParseEvents(null!);

        Assert.Empty(result.SchedulerIssues);
        Assert.Empty(result.SignificantWaits);
    }
}
