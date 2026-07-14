/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Common;

/// <summary>
/// One parsed process inside a deadlock graph — the raw, host-neutral fields produced by the shared
/// sp_BlitzLock-style graph walk (<see cref="DeadlockGraphProcessParser"/>). Both apps' grid rows derive
/// from this: Lite's <c>DeadlockProcessDetail</c> and the Darling viewer's <c>DeadlockProcessDetail</c>
/// each add ONLY their host-specific, display-time getters (per-server <c>ServerTimeHelper</c> vs the
/// viewer's <c>ViewerTimeHelper</c>) on top of these settable properties, so the ~145-line XML walk that
/// fills them lives once. WPF-free by design (this is the shared leaf project); no display strings here.
/// </summary>
public class DeadlockProcessInfo
{
    public DateTime? DeadlockTime { get; set; }
    public bool IsVictim { get; set; }
    public string ProcessId { get; set; } = "";
    public int Spid { get; set; }
    public string DatabaseName { get; set; } = "";
    public string SqlText { get; set; } = "";
    public string WaitResource { get; set; } = "";
    public long WaitTime { get; set; }
    public string LockMode { get; set; } = "";
    public string IsolationLevel { get; set; } = "";
    public long LogUsed { get; set; }
    public int TransactionCount { get; set; }
    public string ClientApp { get; set; } = "";
    public string HostName { get; set; } = "";
    public string LoginName { get; set; } = "";
    public string Status { get; set; } = "";
    public string DeadlockGraphXml { get; set; } = "";
    public bool HasDeadlockXml => !string.IsNullOrEmpty(DeadlockGraphXml);

    /// <summary>
    /// The deadlock's BEST-EFFORT victim plan (deadlocks.victim_query_plan_xml, #1368 / V7 — resolved at
    /// collection time from the victim's sql_handle, only when the host sets CapturePlanXml; often NULL,
    /// always NULL under Lite). The Darling viewer threads it onto every parsed detail; Lite leaves it null.
    /// </summary>
    public string? VictimQueryPlanXml { get; set; }

    /* New fields from sp_BlitzLock analysis */
    public string DeadlockType { get; set; } = "";
    public string ObjectNames { get; set; } = "";
    public string ProcName { get; set; } = "";
    public string OwnerMode { get; set; } = "";
    public string WaiterMode { get; set; } = "";
    public string TransactionName { get; set; } = "";
    public int Priority { get; set; }
    public DateTime? LastTranStarted { get; set; }
    public DateTime? LastBatchStarted { get; set; }
    public DateTime? LastBatchCompleted { get; set; }
}
