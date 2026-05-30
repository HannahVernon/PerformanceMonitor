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
    string FactKey,                              // e.g. "PLAN_REGRESSION" — handler-registry key
    string Action,                              // "force" (v1). Un-apply derives "unforce".
    IReadOnlyList<ForcePlanTarget> Targets);

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
