/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;

namespace PerformanceMonitorDashboard.Services.Remediation
{
    /// <summary>Outcome status for a single per-target apply/unapply attempt.</summary>
    public enum RemediationStatus
    {
        /// <summary>The mutation ran and succeeded.</summary>
        Success,

        /// <summary>
        /// No mutation needed or possible, but not an error: already forced, the
        /// plan/query is gone (stale), or Query Store is not READ_WRITE. Audited
        /// as <c>skipped</c>.
        /// </summary>
        Skipped,

        /// <summary>
        /// The gate refused the target before any mutation (e.g. the connected DB
        /// is not the intended target, or the audit table is absent on the
        /// monitoring server). Audited as <c>aborted</c>.
        /// </summary>
        Blocked,

        /// <summary>
        /// The monitoring login lacks ALTER on the target database. Fails closed
        /// with grant guidance; no mutation, no elevation prompt. Audited as
        /// <c>skipped</c>.
        /// </summary>
        PermissionDenied,

        /// <summary>The mutation was attempted and the server raised an error.</summary>
        Error
    }

    /// <summary>Advisory pre-execution disposition for the UI (display driver only).</summary>
    public enum RemediationDisposition
    {
        Ok,
        AlreadyForced,
        WarnFailing,
        BlockQueryStoreOff,
        BlockStale,
        BlockNoAlter,
        BlockWrongDatabase,
        BlockAuditTableAbsent,
        Error
    }

    /// <summary>
    /// Read-only preflight reading for one target (display driver only — never the
    /// authoritative gate; <c>ApplyAsync</c> re-derives its own gate before any
    /// mutation).
    /// </summary>
    public sealed class TargetPreflight
    {
        public string Database { get; init; } = "";
        public long QueryId { get; init; }
        public long PlanId { get; init; }
        public string? ExecutingLogin { get; init; }
        public string? CurrentDatabase { get; init; }
        public bool HasAlter { get; init; }
        public string? QueryStoreState { get; init; }
        public bool PlanPresent { get; init; }
        public bool IsForcedPlan { get; init; }
        public long ForceFailureCount { get; init; }
        public RemediationDisposition Disposition { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>Aggregate preflight result for an action (one entry per target).</summary>
    public sealed class PreflightResult
    {
        public IReadOnlyList<TargetPreflight> Targets { get; init; } = new List<TargetPreflight>();

        /// <summary>
        /// Whether <c>config.remediation_action_log</c> exists on the monitoring
        /// server. When false, every target is hard-blocked at apply time (no
        /// mutation) — the server is on pre-2.12.0 schema.
        /// </summary>
        public bool AuditTableExists { get; init; }
    }

    /// <summary>
    /// Outcome of a single executor force/unforce call. The executor runs the
    /// authoritative gate and the EXEC on ONE open connection; <see cref="GateSpid"/>
    /// and <see cref="ExecSpid"/> are the server SPID observed at the gate read and
    /// at the mutation respectively — equal SPIDs prove the gate and the mutation
    /// rode the same connection (R2-MOD-1).
    /// </summary>
    public sealed class ForcePlanOutcome
    {
        public string Database { get; init; } = "";
        public long QueryId { get; init; }
        public long PlanId { get; init; }
        public RemediationStatus Status { get; init; }

        /// <summary>True only when an EXEC actually ran and succeeded.</summary>
        public bool Forced { get; init; }
        public string? ExecutingLogin { get; init; }
        public string? Message { get; init; }
        public int? GateSpid { get; init; }
        public int? ExecSpid { get; init; }
    }

    /// <summary>Per-target outcome of an apply/unapply, including the audit disposition.</summary>
    public sealed class TargetOutcome
    {
        public string Database { get; init; } = "";
        public long QueryId { get; init; }
        public long PlanId { get; init; }
        public RemediationStatus Status { get; init; }
        public string? Message { get; init; }
        public string? ExecutingLogin { get; init; }

        /// <summary>Whether the audit row was written for this attempt.</summary>
        public bool AuditWritten { get; init; }

        /// <summary>
        /// The force succeeded but the audit INSERT failed against a present table
        /// (O3). Surfaced as a visible "applied-but-unlogged" warning; never the
        /// un-upgraded-server default (that case is hard-blocked before mutation).
        /// </summary>
        public bool AppliedButUnlogged { get; init; }
    }

    /// <summary>Aggregate apply/unapply result (one entry per target).</summary>
    public sealed class ApplyResult
    {
        public IReadOnlyList<TargetOutcome> Outcomes { get; init; } = new List<TargetOutcome>();
    }

    /// <summary>
    /// One row to write to <c>config.remediation_action_log</c>. Built by the
    /// handler for every attempt (success / skip / error / abort) and persisted on
    /// the monitoring connection by the audit writer, which fills
    /// <see cref="TargetServer"/> from the connection's DataSource when null.
    /// </summary>
    public sealed class RemediationAuditRecord
    {
        public string? OperatorIdentity { get; init; }
        public string? ExecutingLogin { get; init; }
        public string? TargetServer { get; set; }
        public string TargetDatabase { get; init; } = "";
        public string FactKey { get; init; } = "";
        public long QueryId { get; init; }
        public long PlanId { get; init; }
        public string Action { get; init; } = "";          // "force" | "unforce"
        public string? GeneratedSql { get; init; }
        public string Result { get; init; } = "";          // "success" | "skipped" | "error" | "aborted"
        public string? ErrorMessage { get; init; }
        public string? SourceAlertRef { get; init; }
    }
}
