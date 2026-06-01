/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// Structured, typed remediation payload persisted alongside the rendered
/// preview SQL so an Apply action executes from typed parameters, never by
/// parsing the preview text. Built by <see cref="FactRemediation.BuildAction"/>
/// from the same drill-down extraction that renders the preview, and round-trips
/// through the alert context (PerformanceMonitor.Notifications) so the in-app
/// dialog can drive a structured, parameterised execution.
///
/// <para>
/// This is data only — it carries no execution authority. The privileged
/// execution path (Dashboard) re-derives its own gate (permission, freshness,
/// correct-database) immediately before each mutation and never trusts these
/// values as anything but typed inputs.
/// </para>
/// </summary>
public sealed record RemediationAction(
    string FactKey,                              // "PLAN_REGRESSION" | "DB_CONFIG" | "RCSI" — handler-registry key
    string Action,                              // "force" (plan regression) | "set" (db config). Un-apply derives "unforce".
    IReadOnlyList<ForcePlanTarget> Targets,      // force-plan targets (empty list for DB_CONFIG)
    IReadOnlyList<DbConfigTarget>? DbConfigTargets = null,  // DB-config targets (null for force-plan)
    RcsiInactionFigures? RcsiFigures = null);    // B3 Phase 3: RCSI risk-of-not-changing figures carried on the persisted action

/// <summary>
/// The risk-of-NOT-changing monitoring figures for a destructive RCSI action
/// (B3 Phase 3), captured at <see cref="FactRemediation.BuildRcsiAction"/> time when
/// the finding (and its drill-down enrichment) IS available, and CARRIED on the
/// persisted <see cref="RemediationAction"/> so the informed-consent dialog renders
/// the REAL figures at apply time — when only the persisted action survives (the UI
/// apply call site has no finding). <see cref="FactRiskDisclosure.GetForAction"/>
/// reads these in preference to the (often-null at apply time) finding, falling back
/// to the weak-case baseline only when they are genuinely absent.
///
/// <para>
/// These are DISPLAY/disclosure values only — never an execution input. The mirror
/// the §3.3 drill-down enrichment fields: per-database blocked-process event count,
/// deadlock count, and reader-vs-writer share (0–100, null when no reader/writer
/// blocked-process rows were captured).
/// </para>
/// </summary>
public sealed record RcsiInactionFigures(
    int BlockingEvents,
    int Deadlocks,
    int? ReaderWriterPct);

/// <summary>
/// The fixed, hardcoded set of database settings B3 can apply. This enum — NOT any
/// data/operator-supplied string — selects the SET clause literal in the executor
/// (see DatabaseService.Remediation DB-config arm). The first three are ALWAYS-SAFE
/// online metadata changes (Phase 2). <see cref="ReadCommittedSnapshotOn"/> (Phase 3)
/// is DESTRUCTIVE — it is routed through the distinct "RCSI" fact key + RcsiHandler
/// (IsDestructive == true) behind the informed-consent gate, NEVER through the
/// always-safe DbConfigHandler.
/// </summary>
public enum DbConfigSetting
{
    /// <summary>ALTER DATABASE [db] SET AUTO_SHRINK OFF;</summary>
    AutoShrinkOff,

    /// <summary>ALTER DATABASE [db] SET AUTO_CLOSE OFF;</summary>
    AutoCloseOff,

    /// <summary>ALTER DATABASE [db] SET PAGE_VERIFY CHECKSUM;</summary>
    PageVerifyChecksum,

    /// <summary>
    /// ALTER DATABASE [db] SET READ_COMMITTED_SNAPSHOT ON; — DESTRUCTIVE (B3 Phase 3).
    /// Takes a brief exclusive DB lock to enable, adds tempdb version-store load, and
    /// changes reader/writer concurrency semantics. Routed only through the "RCSI"
    /// fact key + RcsiHandler behind the informed-consent gate.
    /// </summary>
    ReadCommittedSnapshotOn
}

/// <summary>
/// One always-safe database-config target: a (database, setting) pair. Each maps
/// 1:1 onto an independent ALTER DATABASE statement, an audit row, and a confirm
/// row. <see cref="Database"/> is validated non-empty by the extractor and
/// re-validated against sys.databases at apply time; <see cref="Setting"/> is the
/// hardcoded-literal selector (NOT free text). <see cref="CurrentValue"/> is a
/// possibly-stale display/audit prior-value snapshot ONLY — it is never an
/// execution input (the live apply-time sys.databases read drives the skip).
/// </summary>
public sealed record DbConfigTarget(
    string Database,
    DbConfigSetting Setting,
    string? CurrentValue = null);

/// <summary>
/// One force-plan target. <see cref="Database"/>, <see cref="QueryId"/> and
/// <see cref="PlanId"/> are the only execution inputs (database is applied solely
/// as the connection's InitialCatalog; the IDs are passed as typed BigInt
/// parameters to sys.sp_query_store_force_plan). The remaining members are
/// carried for the freshness re-check and operator display only — they are NOT
/// execution inputs.
/// </summary>
public sealed record ForcePlanTarget(
    string Database,                            // user DB name (validated non-empty by the extractor)
    long QueryId,                              // > 0
    long PlanId,                               // > 0  (== best_plan_id from the finding)
    string? BestPlanHash = null,               // display / freshness only
    string? LatestPlanHash = null,             // display / freshness only
    double LatestCpuPerExecUs = 0,             // display only (renders the preview comment)
    double BestCpuPerExecUs = 0,               // display only (renders the preview comment)
    double RegressionFactor = 0);              // display only (surfaced in the confirm modal)
