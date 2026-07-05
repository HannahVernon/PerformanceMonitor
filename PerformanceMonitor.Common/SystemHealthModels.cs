/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;

namespace PerformanceMonitor.Common
{
    /*
     * App-agnostic (Common) record types for the six unique system_health warning categories, shredded
     * from the raw system_health event XML captured by SystemHealthEventsCollector (Stage 1). Each record
     * mirrors, column-for-column, the target-table shape Erik's sp_HealthParser logs (the *_SchedulerIssues,
     * *_SevereErrors, *_MemoryConditions, *_MemoryBroker, *_MemoryNodeOOM and *_SignificantWaits tables) so
     * the monitor-side C# parse (SystemHealthParser) stays a faithful port of the T-SQL xpath extraction.
     *
     * Types mirror the sp_HealthParser .value() extraction types (bigint -> long, integer -> int, bit ->
     * bool, numeric(38,0) -> decimal), NOT the display-formatted table columns (e.g. sp_HealthParser stores
     * the already-bigint durations as nvarchar(30) only to strip trailing decimals at render time; here they
     * stay long so a consumer keeps full numeric fidelity). Every field is nullable: a raw event may omit any
     * data node, and this is a pure shred with NO warning/severity/duration filtering applied — those WHERE
     * predicates are an analysis/display concern (Stage 2b), and every column needed to re-apply them
     * (severity, duration, wait_type, status, notification, lastNotification) is preserved.
     */

    /// <summary>
    /// One scheduler-monitor warning row, shredded from a
    /// <c>scheduler_monitor_system_health_ring_buffer_recorded</c> event. Mirrors sp_HealthParser's
    /// <c>*_SchedulerIssues</c> table (non-yielding schedulers, deadlocked schedulers).
    /// </summary>
    public sealed record SchedulerIssueRecord
    {
        /// <summary>Event <c>@timestamp</c>, parsed as UTC (sp_HealthParser converts to server-local at render; kept UTC here).</summary>
        public DateTime? EventTime { get; init; }

        /// <summary>xpath <c>data[@name="scheduler_id"]/value</c>.</summary>
        public int? SchedulerId { get; init; }

        /// <summary>xpath <c>data[@name="cpu_id"]/value</c>.</summary>
        public int? CpuId { get; init; }

        /// <summary>xpath <c>data[@name="status"]/text</c> (the friendly enum text, e.g. WARNING).</summary>
        public string? Status { get; init; }

        /// <summary>xpath <c>data[@name="is_online"]/value</c> (bit).</summary>
        public bool? IsOnline { get; init; }

        /// <summary>xpath <c>data[@name="is_runnable"]/value</c> (bit).</summary>
        public bool? IsRunnable { get; init; }

        /// <summary>xpath <c>data[@name="is_running"]/value</c> (bit).</summary>
        public bool? IsRunning { get; init; }

        /// <summary>xpath <c>data[@name="non_yielding_time"]/value</c> (bigint; the table column is named *_ms).</summary>
        public long? NonYieldingTimeMs { get; init; }

        /// <summary>xpath <c>data[@name="thread_quantum"]/value</c> (bigint; the table column is named *_ms).</summary>
        public long? ThreadQuantumMs { get; init; }
    }

    /// <summary>
    /// One severe-error row, shredded from an <c>error_reported</c> event. Mirrors sp_HealthParser's
    /// <c>*_SevereErrors</c> table. sp_HealthParser surfaces only severity &gt;= 16 (ignoring 17830/18056);
    /// that filtering is NOT applied here (it is a Stage 2b concern), so <see cref="Severity"/> is preserved
    /// for the consumer to re-apply.
    /// </summary>
    public sealed record SevereErrorRecord
    {
        /// <summary>Event <c>@timestamp</c>, parsed as UTC.</summary>
        public DateTime? EventTime { get; init; }

        /// <summary>xpath <c>data[@name="error_number"]/value</c>.</summary>
        public int? ErrorNumber { get; init; }

        /// <summary>xpath <c>data[@name="severity"]/value</c>.</summary>
        public int? Severity { get; init; }

        /// <summary>xpath <c>data[@name="state"]/value</c>.</summary>
        public int? State { get; init; }

        /// <summary>xpath <c>data[@name="message"]/value</c>.</summary>
        public string? Message { get; init; }

        /// <summary>
        /// The database name for <see cref="DatabaseId"/>. sp_HealthParser resolves this server-side via
        /// <c>DB_NAME(database_id)</c>; a DB-free parse-on-read shred cannot resolve a dbid to a name (the raw
        /// event carries only the id), so this is always null from the pure parser and is left for a later,
        /// connection-bearing stage to fill. Preserved (not cut) so the column set matches sp_HealthParser.
        /// </summary>
        public string? DatabaseName { get; init; }

        /// <summary>xpath <c>data[@name="database_id"]/value</c>.</summary>
        public int? DatabaseId { get; init; }
    }

    /// <summary>
    /// One memory-conditions snapshot, shredded from the RESOURCE component of an
    /// <c>sp_server_diagnostics_component_result</c> event (the <c>&lt;resource&gt;</c> node's attributes plus
    /// its <c>memoryReport</c> entries). Mirrors sp_HealthParser's <c>*_MemoryConditions</c> table. The
    /// <c>*_gb</c> fields are integer-divided exactly as sp_HealthParser does: byte-valued entries by
    /// 1024^3, KB-valued Memory-Manager entries by 1024^2.
    /// </summary>
    public sealed record MemoryConditionsRecord
    {
        /// <summary>Event <c>@timestamp</c>, parsed as UTC.</summary>
        public DateTime? EventTime { get; init; }

        /// <summary><c>&lt;resource&gt;</c> attribute <c>@lastNotification</c> (e.g. RESOURCE_MEMPHYSICAL_HIGH/LOW).</summary>
        public string? LastNotification { get; init; }

        /// <summary><c>&lt;resource&gt;</c> attribute <c>@outOfMemoryExceptions</c>.</summary>
        public long? OutOfMemoryExceptions { get; init; }

        /// <summary><c>&lt;resource&gt;</c> attribute <c>@isAnyPoolOutOfMemory</c> (bit).</summary>
        public bool? IsAnyPoolOutOfMemory { get; init; }

        /// <summary><c>&lt;resource&gt;</c> attribute <c>@processOutOfMemoryPeriod</c>.</summary>
        public long? ProcessOutOfMemoryPeriod { get; init; }

        /// <summary>First <c>memoryReport/@name</c> (e.g. "Process/System Counts").</summary>
        public string? Name { get; init; }

        /// <summary>memoryReport entry "Available Physical Memory" (bytes), / 1024^3 = GB.</summary>
        public long? AvailablePhysicalMemoryGb { get; init; }

        /// <summary>memoryReport entry "Available Virtual Memory" (bytes), / 1024^3 = GB.</summary>
        public long? AvailableVirtualMemoryGb { get; init; }

        /// <summary>memoryReport entry "Available Paging File" (bytes), / 1024^3 = GB.</summary>
        public long? AvailablePagingFileGb { get; init; }

        /// <summary>memoryReport entry "Working Set" (bytes), / 1024^3 = GB.</summary>
        public long? WorkingSetGb { get; init; }

        /// <summary>memoryReport entry "Percent of Committed Memory in WS" (raw).</summary>
        public long? PercentOfCommittedMemoryInWs { get; init; }

        /// <summary>memoryReport entry "Page Faults" (raw).</summary>
        public long? PageFaults { get; init; }

        /// <summary>memoryReport entry "System physical memory high" (raw).</summary>
        public long? SystemPhysicalMemoryHigh { get; init; }

        /// <summary>memoryReport entry "System physical memory low" (raw).</summary>
        public long? SystemPhysicalMemoryLow { get; init; }

        /// <summary>memoryReport entry "Process physical memory low" (raw).</summary>
        public long? ProcessPhysicalMemoryLow { get; init; }

        /// <summary>memoryReport entry "Process virtual memory low" (raw).</summary>
        public long? ProcessVirtualMemoryLow { get; init; }

        /// <summary>memoryReport entry "VM Reserved" (KB), / 1024^2 = GB.</summary>
        public long? VmReservedGb { get; init; }

        /// <summary>memoryReport entry "VM Committed" (KB), / 1024^2 = GB.</summary>
        public long? VmCommittedGb { get; init; }

        /// <summary>memoryReport entry "Locked Pages Allocated" (raw).</summary>
        public long? LockedPagesAllocated { get; init; }

        /// <summary>memoryReport entry "Large Pages Allocated" (raw).</summary>
        public long? LargePagesAllocated { get; init; }

        /// <summary>memoryReport entry "Emergency Memory" (KB), / 1024^2 = GB.</summary>
        public long? EmergencyMemoryGb { get; init; }

        /// <summary>memoryReport entry "Emergency Memory In Use" (KB), / 1024^2 = GB.</summary>
        public long? EmergencyMemoryInUseGb { get; init; }

        /// <summary>memoryReport entry "Target Committed" (KB), / 1024^2 = GB.</summary>
        public long? TargetCommittedGb { get; init; }

        /// <summary>memoryReport entry "Current Committed" (KB), / 1024^2 = GB.</summary>
        public long? CurrentCommittedGb { get; init; }

        /// <summary>memoryReport entry "Pages Allocated" (raw).</summary>
        public long? PagesAllocated { get; init; }

        /// <summary>memoryReport entry "Pages Reserved" (raw).</summary>
        public long? PagesReserved { get; init; }

        /// <summary>memoryReport entry "Pages Free" (raw).</summary>
        public long? PagesFree { get; init; }

        /// <summary>memoryReport entry "Pages In Use" (raw).</summary>
        public long? PagesInUse { get; init; }

        /// <summary>memoryReport entry "Page Alloc Potential" (raw).</summary>
        public long? PageAllocPotential { get; init; }

        /// <summary>memoryReport entry "NUMA Growth Phase" (raw).</summary>
        public long? NumaGrowthPhase { get; init; }

        /// <summary>memoryReport entry "Last OOM Factor" (raw).</summary>
        public long? LastOomFactor { get; init; }

        /// <summary>memoryReport entry "Last OS Error" (raw).</summary>
        public long? LastOsError { get; init; }
    }

    /// <summary>
    /// One memory-broker ratio-change row, shredded from a <c>memory_broker_ring_buffer_recorded</c> event.
    /// Mirrors sp_HealthParser's <c>*_MemoryBroker</c> table. <see cref="BrokerId"/> is xpath
    /// <c>data[@name="id"]</c> (the event names it just "id").
    /// </summary>
    public sealed record MemoryBrokerRecord
    {
        /// <summary>Event <c>@timestamp</c>, parsed as UTC.</summary>
        public DateTime? EventTime { get; init; }

        /// <summary>xpath <c>data[@name="id"]/value</c>.</summary>
        public long? BrokerId { get; init; }

        /// <summary>xpath <c>data[@name="pool_metadata_id"]/value</c>.</summary>
        public long? PoolMetadataId { get; init; }

        /// <summary>xpath <c>data[@name="delta_time"]/value</c>.</summary>
        public long? DeltaTime { get; init; }

        /// <summary>xpath <c>data[@name="memory_ratio"]/value</c>.</summary>
        public long? MemoryRatio { get; init; }

        /// <summary>xpath <c>data[@name="new_target"]/value</c>.</summary>
        public long? NewTarget { get; init; }

        /// <summary>xpath <c>data[@name="overall"]/value</c>.</summary>
        public long? Overall { get; init; }

        /// <summary>xpath <c>data[@name="rate"]/value</c>.</summary>
        public long? Rate { get; init; }

        /// <summary>xpath <c>data[@name="currently_predicated"]/value</c>.</summary>
        public long? CurrentlyPredicated { get; init; }

        /// <summary>xpath <c>data[@name="currently_allocated"]/value</c>.</summary>
        public long? CurrentlyAllocated { get; init; }

        /// <summary>xpath <c>data[@name="previously_allocated"]/value</c>.</summary>
        public long? PreviouslyAllocated { get; init; }

        /// <summary>xpath <c>data[@name="broker"]/value</c> (the broker name string, e.g. MEMORYBROKER_FOR_CACHE).</summary>
        public string? Broker { get; init; }

        /// <summary>xpath <c>data[@name="notification"]/value</c> (e.g. RESOURCE_MEMPHYSICAL_HIGH/LOW).</summary>
        public string? Notification { get; init; }
    }

    /// <summary>
    /// One memory-node out-of-memory row, shredded from a <c>memory_node_oom_ring_buffer_recorded</c> event.
    /// Mirrors sp_HealthParser's <c>*_MemoryNodeOOM</c> table. <see cref="NodeId"/> is xpath
    /// <c>data[@name="id"]</c>. The <c>is_*</c> flags are kept as their raw text ("true"/"false") exactly as
    /// sp_HealthParser stores them (nvarchar(10)), not coerced to bool.
    /// </summary>
    public sealed record MemoryNodeOomRecord
    {
        /// <summary>Event <c>@timestamp</c>, parsed as UTC.</summary>
        public DateTime? EventTime { get; init; }

        /// <summary>xpath <c>data[@name="id"]/value</c>.</summary>
        public long? NodeId { get; init; }

        /// <summary>xpath <c>data[@name="memory_node_id"]/value</c>.</summary>
        public long? MemoryNodeId { get; init; }

        /// <summary>xpath <c>data[@name="memory_utilization_pct"]/value</c>.</summary>
        public long? MemoryUtilizationPct { get; init; }

        /// <summary>xpath <c>data[@name="total_physical_memory_kb"]/value</c>.</summary>
        public long? TotalPhysicalMemoryKb { get; init; }

        /// <summary>xpath <c>data[@name="available_physical_memory_kb"]/value</c>.</summary>
        public long? AvailablePhysicalMemoryKb { get; init; }

        /// <summary>xpath <c>data[@name="total_page_file_kb"]/value</c>.</summary>
        public long? TotalPageFileKb { get; init; }

        /// <summary>xpath <c>data[@name="available_page_file_kb"]/value</c>.</summary>
        public long? AvailablePageFileKb { get; init; }

        /// <summary>xpath <c>data[@name="total_virtual_address_space_kb"]/value</c>.</summary>
        public long? TotalVirtualAddressSpaceKb { get; init; }

        /// <summary>xpath <c>data[@name="available_virtual_address_space_kb"]/value</c>.</summary>
        public long? AvailableVirtualAddressSpaceKb { get; init; }

        /// <summary>xpath <c>data[@name="target_kb"]/value</c>.</summary>
        public long? TargetKb { get; init; }

        /// <summary>xpath <c>data[@name="reserved_kb"]/value</c>.</summary>
        public long? ReservedKb { get; init; }

        /// <summary>xpath <c>data[@name="committed_kb"]/value</c>.</summary>
        public long? CommittedKb { get; init; }

        /// <summary>xpath <c>data[@name="shared_committed_kb"]/value</c> (numeric(38,0) -&gt; decimal).</summary>
        public decimal? SharedCommittedKb { get; init; }

        /// <summary>xpath <c>data[@name="awe_kb"]/value</c>.</summary>
        public long? AweKb { get; init; }

        /// <summary>xpath <c>data[@name="pages_kb"]/value</c>.</summary>
        public long? PagesKb { get; init; }

        /// <summary>xpath <c>data[@name="failure"]/text</c> (the failure enum's friendly text).</summary>
        public string? FailureType { get; init; }

        /// <summary>xpath <c>data[@name="failure"]/value</c> (the failure enum's numeric value).</summary>
        public long? FailureValue { get; init; }

        /// <summary>xpath <c>data[@name="resources"]/value</c>.</summary>
        public long? Resources { get; init; }

        /// <summary>xpath <c>data[@name="factor"]/text</c> (the factor enum's friendly text).</summary>
        public string? FactorText { get; init; }

        /// <summary>xpath <c>data[@name="factor"]/value</c> (the factor enum's numeric value).</summary>
        public long? FactorValue { get; init; }

        /// <summary>xpath <c>data[@name="last_error"]/value</c>.</summary>
        public long? LastError { get; init; }

        /// <summary>xpath <c>data[@name="pool_metadata_id"]/value</c>.</summary>
        public long? PoolMetadataId { get; init; }

        /// <summary>xpath <c>data[@name="is_process_in_job"]/value</c> (raw "true"/"false" text).</summary>
        public string? IsProcessInJob { get; init; }

        /// <summary>xpath <c>data[@name="is_system_physical_memory_high"]/value</c> (raw text).</summary>
        public string? IsSystemPhysicalMemoryHigh { get; init; }

        /// <summary>xpath <c>data[@name="is_system_physical_memory_low"]/value</c> (raw text).</summary>
        public string? IsSystemPhysicalMemoryLow { get; init; }

        /// <summary>xpath <c>data[@name="is_process_physical_memory_low"]/value</c> (raw text).</summary>
        public string? IsProcessPhysicalMemoryLow { get; init; }

        /// <summary>xpath <c>data[@name="is_process_virtual_memory_low"]/value</c> (raw text).</summary>
        public string? IsProcessVirtualMemoryLow { get; init; }
    }

    /// <summary>
    /// One significant-wait row, shredded from a <c>wait_info</c> event. Mirrors sp_HealthParser's
    /// <c>*_SignificantWaits</c> table. sp_HealthParser filters these hard (session_id &lt;&gt; 0, sql_text
    /// present and not a BACKUP, duration &gt;= @wait_duration_ms, wait_type not in its ignore list); NONE of
    /// that filtering is applied here — every field needed to re-apply it downstream is preserved.
    /// </summary>
    public sealed record SignificantWaitRecord
    {
        /// <summary>Event <c>@timestamp</c>, parsed as UTC.</summary>
        public DateTime? EventTime { get; init; }

        /// <summary>xpath <c>data[@name="wait_type"]/text</c> (the friendly wait-type name).</summary>
        public string? WaitType { get; init; }

        /// <summary>xpath <c>data[@name="duration"]/value</c> (bigint).</summary>
        public long? DurationMs { get; init; }

        /// <summary>xpath <c>data[@name="signal_duration"]/value</c> (bigint).</summary>
        public long? SignalDurationMs { get; init; }

        /// <summary>xpath <c>data[@name="wait_resource"]/value</c>.</summary>
        public string? WaitResource { get; init; }

        /// <summary>
        /// The waiting statement: action <c>sql_text</c>, with control characters (except tab/CR/LF) replaced
        /// by '?' exactly as sp_HealthParser's query_text cleanup does before it is wrapped as XML.
        /// </summary>
        public string? QueryText { get; init; }

        /// <summary>xpath action <c>session_id</c>.</summary>
        public int? SessionId { get; init; }
    }

    /// <summary>
    /// The result of shredding a batch of raw system_health events: one list per unique warning category.
    /// Populated by <see cref="SystemHealthParser.ParseEvents"/>. Each event contributes at most one record
    /// to exactly one list (an <c>sp_server_diagnostics_component_result</c> event that is not the RESOURCE
    /// component contributes none).
    /// </summary>
    public sealed class SystemHealthParseResult
    {
        public List<SchedulerIssueRecord> SchedulerIssues { get; } = new();
        public List<SevereErrorRecord> SevereErrors { get; } = new();
        public List<MemoryConditionsRecord> MemoryConditions { get; } = new();
        public List<MemoryBrokerRecord> MemoryBroker { get; } = new();
        public List<MemoryNodeOomRecord> MemoryNodeOom { get; } = new();
        public List<SignificantWaitRecord> SignificantWaits { get; } = new();
    }
}
