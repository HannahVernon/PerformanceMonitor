/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Darling.Analysis;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// One Recommendations-tab list row: a persisted <see cref="AnalysisFinding"/> banded and
/// titled the way Lite's recommendations reader maps it (same severity cutoffs, same
/// headline-or-root-fact-key title, same display sort), carrying the underlying finding for
/// the detail pane. Advise-only — the viewer renders the stored remediation action as
/// read-only JSON; there is no Apply here.
/// </summary>
public sealed class ViewerFindingRow
{
    public required string SeverityBand { get; init; }

    public required double RawSeverity { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required string DatabaseName { get; init; }

    public required AnalysisFinding Finding { get; init; }

    /// <summary>The batch's analysis time in the viewer machine's local time.</summary>
    public DateTime AnalysisTimeLocal => ViewerDataService.ToLocalTime(Finding.AnalysisTime);
}

public sealed partial class ViewerDataService
{
    private PgFindingStore? _findingStore;

    private static readonly JsonSerializerOptions s_indentedJson = new() { WriteIndented = true };

    /// <summary>
    /// The latest analysis run's findings for one server, mapped to display rows. Reads
    /// through the shared <see cref="PgFindingStore"/> (its <c>GetLatestFindingsSql</c> —
    /// the MAX(analysis_time) batch, severity descending, including each row's persisted
    /// <c>remediation_action_json</c>), so the viewer binds the exact store contract the
    /// analysis pipeline writes. Note the store's read degrades to an empty list on error
    /// (its twins' discipline); the tab then shows the analysis-cadence empty state.
    /// </summary>
    public async Task<List<ViewerFindingRow>> GetLatestFindingsAsync(int serverId)
    {
        _findingStore ??= new PgFindingStore(_dataSource);
        var findings = await _findingStore.GetLatestFindingsAsync(serverId);
        return MapFindings(findings);
    }

    /// <summary>
    /// Maps stored findings to display rows and applies Lite's deterministic display order:
    /// severity band descending, then raw severity descending, then database
    /// (case-insensitive), then title. Pure over the input — unit-testable without a store.
    /// </summary>
    public static List<ViewerFindingRow> MapFindings(IReadOnlyList<AnalysisFinding> findings)
    {
        if (findings is null)
        {
            throw new ArgumentNullException(nameof(findings));
        }

        var rows = new List<ViewerFindingRow>(findings.Count);
        foreach (var finding in findings)
        {
            if (finding is null)
            {
                continue;
            }

            rows.Add(new ViewerFindingRow
            {
                SeverityBand = SeverityBand(finding.Severity),
                RawSeverity = finding.Severity,
                Title = FindingTitle(finding),
                Category = finding.Category,
                DatabaseName = finding.DatabaseName ?? "",
                Finding = finding,
            });
        }

        rows.Sort(CompareForDisplay);
        return rows;
    }

    /// <summary>
    /// The engine's double severity (0–~2.0) mapped onto the canonical three bands with the
    /// SAME cutoffs both apps' recommendation readers use: &gt;= 1.5 CRITICAL,
    /// &gt;= 0.75 WARNING, else INFO. Uppercase to match the Collection Health status
    /// strings, so one <see cref="StatusToBrushConverter"/> colors both grids.
    /// </summary>
    public static string SeverityBand(double severity)
    {
        if (severity >= 1.5)
        {
            return "CRITICAL";
        }

        if (severity >= 0.75)
        {
            return "WARNING";
        }

        return "INFO";
    }

    /// <summary>
    /// The list title: the composed advice headline (value-stated advice frozen into
    /// StoryText at analysis time, static block fallback), falling back to the root fact key —
    /// Lite's reader's title mapping.
    /// </summary>
    public static string FindingTitle(AnalysisFinding finding)
    {
        if (finding is null)
        {
            throw new ArgumentNullException(nameof(finding));
        }

        var advice = FactAdvice.GetComposedForFinding(finding);
        return !string.IsNullOrEmpty(advice?.Headline) ? advice!.Headline : finding.RootFactKey;
    }

    /// <summary>
    /// The detail pane's text for one finding: the composed story (headline, investigation,
    /// remediation via <see cref="FactAdvice.GetForFinding"/>, which also derives any
    /// copy-paste T-SQL the stored row supports), then the persisted remediation action
    /// pretty-printed as read-only JSON. No Apply in the viewer — the action is disclosure.
    /// </summary>
    public static string ComposeFindingDetailText(AnalysisFinding finding)
    {
        if (finding is null)
        {
            throw new ArgumentNullException(nameof(finding));
        }

        var advice = FactAdvice.GetForFinding(finding);
        var sb = new StringBuilder();

        sb.AppendLine(!string.IsNullOrEmpty(advice?.Headline) ? advice!.Headline : finding.RootFactKey);
        sb.AppendLine();

        if (advice is null)
        {
            sb.AppendLine($"No advice text is available for fact key '{finding.RootFactKey}'.");
            sb.AppendLine();
        }
        else
        {
            if (!string.IsNullOrEmpty(advice.Investigation))
            {
                sb.AppendLine("Investigation:");
                sb.AppendLine(advice.Investigation);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(advice.Remediation))
            {
                sb.AppendLine("Remediation:");
                sb.AppendLine(advice.Remediation);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(advice.RemediationTsql))
            {
                sb.AppendLine("Suggested T-SQL (copy/paste):");
                sb.AppendLine(advice.RemediationTsql);
                sb.AppendLine();
            }
        }

        var remediationJson = PrettyPrintRemediation(finding.Remediation);
        if (remediationJson is not null)
        {
            sb.AppendLine("Stored remediation action (read-only):");
            sb.AppendLine(remediationJson);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Pretty-prints a finding's persisted remediation action through the SAME serializer
    /// that wrote the <c>remediation_action_json</c> column
    /// (<see cref="AlertContextSerializer.SerializeAction"/>), re-indented for reading.
    /// Null when the finding carries no action.
    /// </summary>
    public static string? PrettyPrintRemediation(RemediationAction? action)
    {
        var json = AlertContextSerializer.SerializeAction(action);
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, s_indentedJson);
    }

    /// <summary>
    /// Lite's recommendations display order: band descending, raw severity descending,
    /// database (ordinal, case-insensitive), then title — deterministic regardless of
    /// input order.
    /// </summary>
    private static int CompareForDisplay(ViewerFindingRow x, ViewerFindingRow y)
    {
        int bySeverityBand = BandRank(y.SeverityBand).CompareTo(BandRank(x.SeverityBand));
        if (bySeverityBand != 0)
        {
            return bySeverityBand;
        }

        int byRaw = y.RawSeverity.CompareTo(x.RawSeverity);
        if (byRaw != 0)
        {
            return byRaw;
        }

        int byDb = string.Compare(x.DatabaseName, y.DatabaseName, StringComparison.OrdinalIgnoreCase);
        if (byDb != 0)
        {
            return byDb;
        }

        return string.Compare(x.Title, y.Title, StringComparison.Ordinal);
    }

    private static int BandRank(string band) => band switch
    {
        "CRITICAL" => 2,
        "WARNING" => 1,
        _ => 0,
    };
}
