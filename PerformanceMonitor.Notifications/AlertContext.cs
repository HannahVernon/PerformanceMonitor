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
public record RemediationActionDto(string FactKey, string Action, List<ForcePlanTargetDto> Targets);
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

        return new RemediationActionDto(action.FactKey, action.Action, targets);
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

        return new RemediationAction(dto.FactKey, dto.Action, targets);
    }
}
