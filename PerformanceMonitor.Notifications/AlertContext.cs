/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Text.Json;
using PerformanceMonitor.Analysis;

namespace PerformanceMonitor.Notifications;

/// <summary>
/// Optional detail context attached to alert emails.
/// Populated from blocking/deadlock detail queries at alert time.
/// </summary>
public class AlertContext
{
    public List<AlertDetailItem> Details { get; set; } = new();
    public string? AttachmentXml { get; set; }
    public string? AttachmentFileName { get; set; }
}

/// <summary>
/// A single detail item (e.g., one blocking chain or one deadlock participant).
/// </summary>
public class AlertDetailItem
{
    public string Heading { get; set; } = "";
    public List<(string Label, string Value)> Fields { get; set; } = new();

    /// <summary>
    /// Multi-paragraph prose for this item (advice Investigation / Remediation).
    /// When non-null, renderers emit this as flowing paragraph text rather than
    /// label/value rows.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    /// Marks this item as a copy-paste code block. Renderers emit a monospace
    /// &lt;pre&gt; (HTML) / fenced (plain text) / Consolas TextBox with a copy
    /// button (dialog). Webhooks render only the heading + a "see email or in-app
    /// dialog for the copy-paste T-SQL" hint when this flag is true.
    /// </summary>
    public bool IsCodeBlock { get; set; }

    /// <summary>
    /// Structured, typed remediation payload that lets the in-app dialog drive a
    /// parameterised Apply from this code block (PLAN_REGRESSION force-plan in
    /// v1). Null for every item that is not an applicable remediation T-SQL block
    /// — and null for legacy persisted contexts written before this field existed,
    /// which is the no-Apply-button case.
    /// </summary>
    public RemediationAction? Remediation { get; set; }
}

/// <summary>
/// Serialization DTO for persisting <see cref="AlertContext"/> as JSON.
/// <see cref="AlertDetailItem.Fields"/> is a <c>List&lt;(string,string)&gt;</c>
/// ValueTuple, which System.Text.Json will not round-trip (tuple elements are
/// fields, not properties); these DTOs name every member explicitly so the
/// persisted context survives the round-trip into the in-app dialog.
/// <see cref="AlertContext.AttachmentXml"/>/<see cref="AlertContext.AttachmentFileName"/>
/// are deliberately not persisted (the dialog has no attachment surface).
/// </summary>
public record AlertContextDto(List<AlertDetailItemDto> Details);
public record AlertDetailItemDto(string Heading, List<FieldDto> Fields, string? Body, bool IsCodeBlock, RemediationActionDto? Remediation = null);
public record FieldDto(string Label, string Value);

/// <summary>
/// JSON mirror of <see cref="RemediationAction"/> / <see cref="ForcePlanTarget"/>
/// (PerformanceMonitor.Analysis). The trailing optional member on
/// <see cref="AlertDetailItemDto"/> plus the reference-type nullability here make
/// the round-trip backward-compatible: legacy contextJson with no Remediation
/// property deserializes the field to null.
/// </summary>
public record RemediationActionDto(
    string FactKey,
    string Action,
    List<ForcePlanTargetDto> Targets,
    List<DbConfigTargetDto>? DbConfigTargets = null,
    RcsiInactionFiguresDto? RcsiFigures = null,
    List<ClearPlanTargetDto>? ClearPlanTargets = null,
    ClearPlanFiguresDto? ClearPlanFigures = null,
    List<FileGrowthTargetDto>? FileGrowthTargets = null,
    List<RcsiTargetDto>? RcsiTargets = null);

/// <summary>
/// JSON mirror of <see cref="RcsiTarget"/>. The per-database RCSI targets are carried on a
/// DB_CONFIG action PURELY so the Recommendations reader can fan per-db RCSI cards on read
/// (the drill-down is ephemeral); they are never executed from the DB_CONFIG action itself.
/// The trailing optional <c>RcsiTargets</c> member on <see cref="RemediationActionDto"/> keeps
/// the round-trip backward-compatible: legacy/non-DB_CONFIG contextJson without it deserializes
/// to null.
/// </summary>
public record RcsiTargetDto(
    string Database,
    RcsiInactionFiguresDto Figures);

/// <summary>
/// JSON mirror of <see cref="ClearPlanTarget"/> (clear-cached-plan, PR-B). The
/// <c>QueryHash</c> is the only execution input; the remaining members are display/
/// disclosure only. The trailing optional <c>ClearPlanTargets</c> member on
/// <see cref="RemediationActionDto"/> keeps the round-trip backward-compatible: legacy/
/// non-CLEAR_PLAN contextJson without it deserializes to null.
/// </summary>
public record ClearPlanTargetDto(
    string Database,
    string QueryHash,
    double CurrentCpuPerExecMs,
    double BaselineCpuPerExecMs,
    double AnomalyRatio,
    string? LatestPlanHandle);

/// <summary>
/// JSON mirror of <see cref="ClearPlanFigures"/> (clear-cached-plan, PR-B). Carried on
/// the persisted CLEAR_PLAN action so the informed-consent dialog shows the REAL anomaly
/// figures (incl. the window CPU%, LOW-1) at apply time, when the UI apply call site has
/// no finding. Trailing optional → backward-compatible.
/// </summary>
public record ClearPlanFiguresDto(
    double CurrentCpuPerExecMs,
    double BaselineCpuPerExecMs,
    double AnomalyRatio,
    int CpuPercent,
    bool PlanRegressionCoFired,
    bool ParameterSensitivityCoFired);

/// <summary>
/// JSON mirror of <see cref="RcsiInactionFigures"/> (B3 Phase 3). Carried on the
/// persisted RCSI action so the informed-consent dialog shows the REAL blocking/
/// deadlock/reader-writer figures at apply time (the UI apply call site has no
/// finding). The trailing optional member on <see cref="RemediationActionDto"/>
/// keeps the round-trip backward-compatible: legacy/non-RCSI contextJson without it
/// deserializes to null.
/// </summary>
public record RcsiInactionFiguresDto(
    int BlockingEvents,
    int Deadlocks,
    int? ReaderWriterPct);
public record ForcePlanTargetDto(
    string Database,
    long QueryId,
    long PlanId,
    string? BestPlanHash,
    string? LatestPlanHash,
    double LatestCpuPerExecUs,
    double BestCpuPerExecUs,
    double RegressionFactor);

/// <summary>
/// JSON mirror of <see cref="DbConfigTarget"/>. <see cref="Setting"/> is persisted
/// as the enum's int value. The trailing optional <c>DbConfigTargets</c> member on
/// <see cref="RemediationActionDto"/> keeps the round-trip backward-compatible:
/// legacy contextJson without it deserializes to null.
/// </summary>
public record DbConfigTargetDto(
    string Database,
    int Setting,
    string? CurrentValue);

/// <summary>
/// JSON mirror of <see cref="FileGrowthTarget"/> (WS3 percent-autogrowth advisory). The
/// trailing optional <c>FileGrowthTargets</c> member on <see cref="RemediationActionDto"/>
/// keeps the round-trip backward-compatible: legacy/non-autogrowth contextJson without it
/// deserializes to null. Carried so the copy-paste MODIFY FILE statements survive the
/// persisted-action round-trip the Recommendations reader renders from (the drill-down is
/// ephemeral). Advisory only — there is no handler, so it never drives Apply.
/// </summary>
public record FileGrowthTargetDto(
    string Database,
    string LogicalFileName,
    double CurrentSizeMb,
    int CurrentGrowthPercent,
    int RecommendedGrowthMb);

/// <summary>
/// Maps <see cref="AlertContext"/> to/from the <see cref="AlertContextDto"/> JSON projection
/// persisted alongside the flat detail_text. Centralizes the DTO mapping so the persistence
/// write (EmailAlertService) and the dialog read (AlertDetailWindow) cannot drift.
/// </summary>
public static class AlertContextSerializer
{
    public static string Serialize(AlertContext context)
    {
        var dto = new AlertContextDto(
            context.Details.ConvertAll(d => new AlertDetailItemDto(
                d.Heading,
                d.Fields.ConvertAll(f => new FieldDto(f.Label, f.Value)),
                d.Body,
                d.IsCodeBlock,
                ToDto(d.Remediation))));
        return JsonSerializer.Serialize(dto);
    }

    /// <summary>
    /// Serializes a single <see cref="RemediationAction"/> to JSON for persistence on a
    /// finding row (recommendations rebuild D2). Reuses the SAME private
    /// <see cref="ToDto(RemediationAction?)"/> projection the alert-context write already
    /// uses, so a finding's persisted action round-trips byte-identically to one carried
    /// in an alert's ContextJson (incl. RcsiInactionFigures / ClearPlanFigures / all
    /// target lists). Returns null when the action is null.
    /// </summary>
    public static string? SerializeAction(RemediationAction? action)
    {
        if (action is null)
            return null;
        return JsonSerializer.Serialize(ToDto(action));
    }

    /// <summary>
    /// Deserializes a finding's persisted <c>remediation_action_json</c> back into a
    /// <see cref="RemediationAction"/> via the SAME private
    /// <see cref="FromDto(RemediationActionDto?)"/> the alert-context read uses. Returns
    /// null for null/blank/garbage JSON (try-catch, mirroring <see cref="TryDeserialize"/>),
    /// so a corrupt column degrades to "no Apply affordance" rather than throwing.
    /// </summary>
    public static RemediationAction? DeserializeAction(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return FromDto(JsonSerializer.Deserialize<RemediationActionDto>(json));
        }
        catch
        {
            return null;
        }
    }

    public static bool TryDeserialize(string? json, out AlertContext context)
    {
        context = new AlertContext();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var dto = JsonSerializer.Deserialize<AlertContextDto>(json);
            if (dto?.Details is null)
                return false;

            foreach (var d in dto.Details)
            {
                var item = new AlertDetailItem
                {
                    Heading = d.Heading,
                    Body = d.Body,
                    IsCodeBlock = d.IsCodeBlock,
                    Remediation = FromDto(d.Remediation)
                };
                if (d.Fields is not null)
                {
                    foreach (var f in d.Fields)
                        item.Fields.Add((f.Label, f.Value));
                }
                context.Details.Add(item);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RemediationActionDto? ToDto(RemediationAction? action)
    {
        if (action is null)
            return null;

        var targets = new List<ForcePlanTargetDto>(action.Targets.Count);
        foreach (var t in action.Targets)
        {
            targets.Add(new ForcePlanTargetDto(
                t.Database,
                t.QueryId,
                t.PlanId,
                t.BestPlanHash,
                t.LatestPlanHash,
                t.LatestCpuPerExecUs,
                t.BestCpuPerExecUs,
                t.RegressionFactor));
        }

        List<DbConfigTargetDto>? dbConfigTargets = null;
        if (action.DbConfigTargets is not null)
        {
            dbConfigTargets = new List<DbConfigTargetDto>(action.DbConfigTargets.Count);
            foreach (var t in action.DbConfigTargets)
                dbConfigTargets.Add(new DbConfigTargetDto(t.Database, (int)t.Setting, t.CurrentValue));
        }

        var rcsiFigures = action.RcsiFigures is { } f
            ? new RcsiInactionFiguresDto(f.BlockingEvents, f.Deadlocks, f.ReaderWriterPct)
            : null;

        // Clear-cached-plan (PR-B): persist the targets + carried figures so the affordance
        // survives the contextJson round-trip and the dialog shows the REAL numbers at apply.
        List<ClearPlanTargetDto>? clearPlanTargets = null;
        if (action.ClearPlanTargets is not null)
        {
            clearPlanTargets = new List<ClearPlanTargetDto>(action.ClearPlanTargets.Count);
            foreach (var t in action.ClearPlanTargets)
                clearPlanTargets.Add(new ClearPlanTargetDto(
                    t.Database, t.QueryHash, t.CurrentCpuPerExecMs, t.BaselineCpuPerExecMs,
                    t.AnomalyRatio, t.LatestPlanHandle));
        }

        var clearPlanFigures = action.ClearPlanFigures is { } cf
            ? new ClearPlanFiguresDto(cf.CurrentCpuPerExecMs, cf.BaselineCpuPerExecMs, cf.AnomalyRatio,
                cf.CpuPercent, cf.PlanRegressionCoFired, cf.ParameterSensitivityCoFired)
            : null;

        // WS3 (percent-autogrowth advisory): persist the file targets so the Recommendations
        // reader can render the copy-paste MODIFY FILE statements on read (the drill-down is
        // ephemeral). Null for every other fact key -> backward-compatible.
        List<FileGrowthTargetDto>? fileGrowthTargets = null;
        if (action.FileGrowthTargets is not null)
        {
            fileGrowthTargets = new List<FileGrowthTargetDto>(action.FileGrowthTargets.Count);
            foreach (var t in action.FileGrowthTargets)
                fileGrowthTargets.Add(new FileGrowthTargetDto(
                    t.Database, t.LogicalFileName, t.CurrentSizeMb, t.CurrentGrowthPercent, t.RecommendedGrowthMb));
        }

        // Per-db RCSI targets (carried on a DB_CONFIG action for the read-time card fan-out).
        // Persist them so the Recommendations reader can fan per-db RCSI cards after the round-
        // trip (the drill-down they came from is ephemeral). Null for every other fact key ->
        // backward-compatible. Never executed from the DB_CONFIG action.
        List<RcsiTargetDto>? rcsiTargets = null;
        if (action.RcsiTargets is not null)
        {
            rcsiTargets = new List<RcsiTargetDto>(action.RcsiTargets.Count);
            foreach (var t in action.RcsiTargets)
                rcsiTargets.Add(new RcsiTargetDto(
                    t.Database,
                    new RcsiInactionFiguresDto(t.Figures.BlockingEvents, t.Figures.Deadlocks, t.Figures.ReaderWriterPct)));
        }

        return new RemediationActionDto(action.FactKey, action.Action, targets, dbConfigTargets,
            rcsiFigures, clearPlanTargets, clearPlanFigures, fileGrowthTargets, rcsiTargets);
    }

    private static RemediationAction? FromDto(RemediationActionDto? dto)
    {
        if (dto is null)
            return null;

        var targets = new List<ForcePlanTarget>(dto.Targets?.Count ?? 0);
        if (dto.Targets is not null)
        {
            foreach (var t in dto.Targets)
            {
                targets.Add(new ForcePlanTarget(
                    t.Database,
                    t.QueryId,
                    t.PlanId,
                    t.BestPlanHash,
                    t.LatestPlanHash,
                    t.LatestCpuPerExecUs,
                    t.BestCpuPerExecUs,
                    t.RegressionFactor));
            }
        }

        // m-A: deserialize the DB-config targets and PASS them to the ctor. The
        // RemediationAction ctor's trailing DbConfigTargets defaults to null, so a
        // 3-arg call here would silently drop a DB_CONFIG action's targets on the
        // round-trip (un-applyable from any persisted context). Legacy JSON without
        // the field deserializes dto.DbConfigTargets to null -> dbConfigTargets null
        // -> backward-compatible.
        List<DbConfigTarget>? dbConfigTargets = null;
        if (dto.DbConfigTargets is not null)
        {
            dbConfigTargets = new List<DbConfigTarget>(dto.DbConfigTargets.Count);
            foreach (var t in dto.DbConfigTargets)
                dbConfigTargets.Add(new DbConfigTarget(t.Database, (DbConfigSetting)t.Setting, t.CurrentValue));
        }

        // B3 Phase 3: the RCSI risk figures must survive the round-trip so the dialog
        // shows the REAL numbers at apply time. Legacy/non-RCSI JSON without the field
        // deserializes to null -> the disclosure falls back to the finding/weak-case.
        var rcsiFigures = dto.RcsiFigures is { } f
            ? new RcsiInactionFigures(f.BlockingEvents, f.Deadlocks, f.ReaderWriterPct)
            : null;

        // Clear-cached-plan (PR-B): rebuild the targets + carried figures from the DTO and
        // PASS them to the ctor (the trailing ClearPlan* members default to null, so a short
        // call would silently drop a CLEAR_PLAN action's targets on the round-trip). Legacy
        // JSON without the fields deserializes to null → backward-compatible.
        List<ClearPlanTarget>? clearPlanTargets = null;
        if (dto.ClearPlanTargets is not null)
        {
            clearPlanTargets = new List<ClearPlanTarget>(dto.ClearPlanTargets.Count);
            foreach (var t in dto.ClearPlanTargets)
                clearPlanTargets.Add(new ClearPlanTarget(
                    t.Database, t.QueryHash, t.CurrentCpuPerExecMs, t.BaselineCpuPerExecMs,
                    t.AnomalyRatio, t.LatestPlanHandle));
        }

        var clearPlanFigures = dto.ClearPlanFigures is { } cf
            ? new ClearPlanFigures(cf.CurrentCpuPerExecMs, cf.BaselineCpuPerExecMs, cf.AnomalyRatio,
                cf.CpuPercent, cf.PlanRegressionCoFired, cf.ParameterSensitivityCoFired)
            : null;

        // WS3: rebuild the percent-autogrowth file targets from the DTO and PASS them to the
        // ctor (the trailing FileGrowthTargets member defaults to null, so a short call would
        // silently drop them on the round-trip). Legacy JSON without the field -> null.
        List<FileGrowthTarget>? fileGrowthTargets = null;
        if (dto.FileGrowthTargets is not null)
        {
            fileGrowthTargets = new List<FileGrowthTarget>(dto.FileGrowthTargets.Count);
            foreach (var t in dto.FileGrowthTargets)
                fileGrowthTargets.Add(new FileGrowthTarget(
                    t.Database, t.LogicalFileName, t.CurrentSizeMb, t.CurrentGrowthPercent, t.RecommendedGrowthMb));
        }

        // Per-db RCSI targets: rebuild from the DTO and PASS them to the ctor (the trailing
        // RcsiTargets member defaults to null, so a short call would silently drop them on the
        // round-trip — the reader would then fan no RCSI cards). Legacy JSON without the field
        // deserializes to null → backward-compatible.
        List<RcsiTarget>? rcsiTargets = null;
        if (dto.RcsiTargets is not null)
        {
            rcsiTargets = new List<RcsiTarget>(dto.RcsiTargets.Count);
            foreach (var t in dto.RcsiTargets)
                rcsiTargets.Add(new RcsiTarget(
                    t.Database,
                    new RcsiInactionFigures(t.Figures.BlockingEvents, t.Figures.Deadlocks, t.Figures.ReaderWriterPct)));
        }

        return new RemediationAction(dto.FactKey, dto.Action, targets, dbConfigTargets, rcsiFigures,
            clearPlanTargets, clearPlanFigures, fileGrowthTargets, rcsiTargets);
    }
}
