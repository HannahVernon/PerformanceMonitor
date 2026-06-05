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
    string FactKey,                              // "PLAN_REGRESSION" | "DB_CONFIG" | "RCSI" | "CLEAR_PLAN" | "FILE_AUTOGROWTH_PERCENT" — handler-registry key
    string Action,                              // "force" (plan regression) | "set" (db config) | "clear" (clear cached plan). Un-apply derives "unforce".
    IReadOnlyList<ForcePlanTarget> Targets,      // force-plan targets (empty list for DB_CONFIG / CLEAR_PLAN)
    IReadOnlyList<DbConfigTarget>? DbConfigTargets = null,  // DB-config targets (null for force-plan)
    RcsiInactionFigures? RcsiFigures = null,     // B3 Phase 3: RCSI risk-of-not-changing figures carried on the persisted action
    IReadOnlyList<ClearPlanTarget>? ClearPlanTargets = null, // clear-cached-plan targets (null for the other fact keys)
    ClearPlanFigures? ClearPlanFigures = null,   // clear-cached-plan risk-of-not-changing figures carried on the persisted action
    IReadOnlyList<FileGrowthTarget>? FileGrowthTargets = null); // WS3: percent-autogrowth files — advise/copy-paste ONLY (no registered handler, no Apply)

/// <summary>
/// The risk-of-NOT-changing monitoring figures for a destructive CLEAR_PLAN action
/// (clear cached plan via DBCC FREEPROCCACHE), captured at
/// <see cref="FactRemediation.BuildClearPlanAction"/> time when the finding (and its
/// <c>abnormal_cpu_plans</c> drill-down enrichment) IS available, and CARRIED on the
/// persisted <see cref="RemediationAction"/> so the informed-consent dialog renders the
/// REAL figures at apply time — when only the persisted action survives (the UI apply
/// call site has no finding). <see cref="FactRiskDisclosure.GetForAction"/> reads these
/// in preference to the (often-null at apply time) finding.
///
/// <para>
/// These are DISPLAY/disclosure values only — never an execution input. They mirror the
/// §2 detector enrichment fields: current vs baseline per-exec CPU (ms), the anomaly
/// ratio, the query's window CPU share (%), and whether PLAN_REGRESSION /
/// PARAMETER_SENSITIVITY co-fired (which steer the honest tool-choice disclosure).
/// </para>
/// </summary>
public sealed record ClearPlanFigures(
    double CurrentCpuPerExecMs,
    double BaselineCpuPerExecMs,
    double AnomalyRatio,
    int CpuPercent,
    bool PlanRegressionCoFired,
    bool ParameterSensitivityCoFired);

/// <summary>
/// One clear-cached-plan target: a (database, query_hash) pair. The
/// <see cref="QueryHash"/> (the stable cross-collection key, <c>binary(8)</c> rendered
/// as a hex string like <c>0x...</c>) is the ONLY execution input — the executor
/// re-resolves the current cached <c>plan_handle(s)</c> for it LIVE at apply time
/// (the snapshot <see cref="LatestPlanHandle"/> is display only and is NEVER fed to
/// DBCC). A <c>query_hash</c> is not unique to one logical query, so the live resolve
/// can return several handles spanning distinct databases — disclosed per-handle (M-2).
/// The remaining members are carried for the confirm dialog / disclosure only.
/// </summary>
public sealed record ClearPlanTarget(
    string Database,                            // user DB name (display; the gate is server-scoped, not per-DB)
    string QueryHash,                          // stable key, hex "0x..." (binary(8)); the only execution input
    double CurrentCpuPerExecMs = 0,            // display only
    double BaselineCpuPerExecMs = 0,           // display only
    double AnomalyRatio = 0,                   // display only
    string? LatestPlanHandle = null);          // display only — the apply path re-resolves live

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
/// One percent-autogrowth file target (WS3): a large data/log file set to grow in
/// PERCENTAGE steps, which on a big file is a single huge allocation that stalls
/// writes. This is an ADVISORY/copy-paste payload only — there is NO registered
/// handler for the "FILE_AUTOGROWTH_PERCENT" fact key, so it never produces an Apply
/// button (<see cref="DbConfigTarget"/> by contrast drives the always-safe Apply). All
/// members are display/copy-paste inputs: the reader renders one
/// <c>ALTER DATABASE [db] MODIFY FILE (NAME = [logical], FILEGROWTH = NMB);</c> per
/// target. <see cref="RecommendedGrowthMb"/> is the fixed-MB step suggested from the
/// observed file size (a starting point, not a prescription).
/// </summary>
public sealed record FileGrowthTarget(
    string Database,                            // user DB name (bracketed by the renderer; never executed)
    string LogicalFileName,                     // sys.database_files.name (bracketed by the renderer)
    double CurrentSizeMb,                       // total_size_mb — display only
    int CurrentGrowthPercent,                   // growth_pct — display only
    int RecommendedGrowthMb);                   // suggested fixed-MB FILEGROWTH (size-tiered)

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
