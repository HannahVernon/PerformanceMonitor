/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PerformanceMonitor.Analysis;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Canonical three-band severity for the viewer's Recommendations surface — the viewer-local
/// mirror of Lite's <c>LiteRecommendationSeverity</c> (the viewer reads only the engine producer;
/// there is no legacy critical_issues store here). The engine scores findings on a
/// <c>double</c> (0-~2.0); that scale maps onto this enum so one list sorts and renders
/// consistently. The ordinals are deliberately Critical &gt; Warning &gt; Info so a descending
/// enum sort matches a descending severity sort.
/// </summary>
public enum RecommendationSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>
/// Which top-level state the Recommendations surface is in. The tab swaps a single visible region
/// per state. Mirrors Lite's <c>LiteRecommendationsState</c> minus <c>InsufficientData</c>: that
/// state is reachable in Lite ONLY through its in-app "Generate now" (which calls the engine and
/// reads back its insufficient-data determination). The Darling service runs analysis on its own
/// 30-minute cadence, so the viewer has no engine call and cannot distinguish "collecting" from
/// "all clear" — a read with zero findings is simply <see cref="Empty"/>. See the header comment on
/// the Recommendations tab in MainWindow for the Generate-now -> cadence-note deviation.
/// </summary>
public enum RecommendationsState
{
    /// <summary>A read is in flight.</summary>
    Loading,

    /// <summary>The read completed and produced zero recommendations — the all-clear.</summary>
    Empty,

    /// <summary>The read completed with one or more recommendations to render.</summary>
    Loaded
}

/// <summary>
/// A single advise-only recommendation row — the viewer-local mirror of Lite's
/// <see cref="PerformanceMonitorLite.Analysis.Recommendations.LiteRecommendationItem"/> shape
/// (referenced here only in prose; the viewer does not depend on the Lite assembly). A plain DTO
/// (no WPF dependency) built from a persisted <see cref="AnalysisFinding"/> via a
/// <see cref="ViewerFindingRow"/>.
///
/// <para>
/// The viewer is ADVISE-ONLY (Postgres reads only; SQL-side remediation is Dashboard-only per
/// project scope). A card offers the diagnosis, copy-paste T-SQL when the persisted remediation
/// action carries one, and an "Ask AI" MCP prompt — never an in-app Apply, and (mirroring the
/// re-skin brief) no mute affordance (mute lives on the Alert History surface).
/// </para>
/// </summary>
public sealed class RecommendationItem
{
    /// <summary>The three-band severity used for sorting and the badge glyph/colour.</summary>
    public RecommendationSeverity Severity { get; init; }

    /// <summary>The raw engine severity (0-~2.0), the secondary sort within a band.</summary>
    public double RawSeverity { get; init; }

    /// <summary>The affected database, or null for a server-scoped finding.</summary>
    public string? Database { get; init; }

    /// <summary>The one-line card heading.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Operator-facing advice prose, composed from <see cref="FactAdvice.GetComposedForFinding"/>
    /// (remediation line + investigation line). Settable so the co-fired cross-reference can be
    /// appended after mapping (mirrors Lite's reader).
    /// </summary>
    public string? AdviceText { get; set; }

    /// <summary>
    /// Copy-paste-ready T-SQL derived from the finding's PERSISTED
    /// <see cref="AnalysisFinding.Remediation"/> action — the same read-back source the Dashboard's
    /// RecommendationsReader uses (BuildCopyPasteFromAction). Null when the action carries no
    /// copy-paste target. NOTE: the viewer reads persisted findings, whose ephemeral drill-down is
    /// NOT stored, so <see cref="FactRemediation.GenerateForFinding"/> (Lite's live-path source)
    /// returns null here; the persisted action is the authoritative copy-paste source on read.
    /// </summary>
    public string? CopyPasteSql { get; init; }

    /// <summary>
    /// The finding's incident id — the group key the surface collapses cards under so related
    /// findings render as one report. Empty for findings analyzed before incident_id existed; the
    /// view-model treats an empty id as a standalone single-card incident.
    /// </summary>
    public string IncidentId { get; init; } = string.Empty;

    /// <summary>The monitored server's display name (for the Ask-AI prompt).</summary>
    public string ServerName { get; init; } = string.Empty;

    /// <summary>UTC start of the finding's time window (analysis TimeRangeStart), or null.</summary>
    public DateTime? WindowStartUtc { get; init; }

    /// <summary>UTC end of the finding's time window (analysis TimeRangeEnd), or null.</summary>
    public DateTime? WindowEndUtc { get; init; }
}

/// <summary>
/// A single recommendation rendered as a card. A plain DTO wrapping a <see cref="RecommendationItem"/>
/// plus the pre-computed display/visibility flags the XAML binds to, so the affordance model and the
/// Ask-AI prompt are unit-testable. Mirrors Lite's <c>LiteRecommendationCardViewModel</c>: ADVISE-ONLY
/// (no Apply, no mute); every card offers "Ask AI", and cards whose persisted action carries a
/// copy-paste statement also offer "Copy fix".
/// </summary>
public sealed class RecommendationCardViewModel
{
    private readonly int _utcOffsetMinutes;

    public RecommendationCardViewModel(RecommendationItem item, int utcOffsetMinutes = 0)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _utcOffsetMinutes = utcOffsetMinutes;
    }

    /// <summary>The underlying advise-only recommendation row.</summary>
    public RecommendationItem Item { get; }

    /// <summary>The three-band severity (drives the badge glyph + colour).</summary>
    public RecommendationSeverity Severity => Item.Severity;

    /// <summary>Short uppercase severity label for the badge, e.g. "CRITICAL".</summary>
    public string SeverityLabel => Item.Severity switch
    {
        RecommendationSeverity.Critical => "CRITICAL",
        RecommendationSeverity.Warning => "WARNING",
        _ => "INFO"
    };

    /// <summary>A glyph for the badge (Segoe MDL2 Assets code point) per severity — Lite's glyphs.</summary>
    public string SeverityGlyph => Item.Severity switch
    {
        RecommendationSeverity.Critical => "", // error / critical
        RecommendationSeverity.Warning => "",  // warning triangle
        _ => ""                                 // info
    };

    /// <summary>The card heading.</summary>
    public string Title => Item.Title;

    /// <summary>
    /// The affected database wrapped in brackets for display, or empty for a server-scoped finding
    /// (so the database line collapses).
    /// </summary>
    public string DatabaseBracketed =>
        string.IsNullOrEmpty(Item.Database) ? string.Empty : $"[{Item.Database}]";

    /// <summary>Whether a database line should be shown at all.</summary>
    public bool HasDatabase => !string.IsNullOrEmpty(Item.Database);

    /// <summary>The operator-facing advice prose.</summary>
    public string? AdviceText => Item.AdviceText;

    /// <summary>Whether there is advice prose to render.</summary>
    public bool HasAdvice => !string.IsNullOrEmpty(Item.AdviceText);

    /// <summary>The copy-paste-ready fix T-SQL, if any.</summary>
    public string? CopyPasteSql => Item.CopyPasteSql;

    /// <summary>Whether "Copy fix" is shown — only when a copy-paste statement exists.</summary>
    public bool ShowCopyFix => !string.IsNullOrEmpty(Item.CopyPasteSql);

    /// <summary>
    /// Whether "Ask AI" is shown. Shown for every card — the Darling service exposes the same MCP
    /// investigation tools the prompt references, so any finding can be handed to AI.
    /// </summary>
    public bool ShowAskAi => true;

    /// <summary>
    /// The MCP investigation prompt copied to the clipboard by "Ask AI". The window is rendered in
    /// the viewer machine's local time (UTC window + offset) for operator legibility.
    /// </summary>
    public string AskAiPrompt
    {
        get
        {
            var (from, to) = LocalWindow();
            return RecommendationsViewModel.BuildAskAiPrompt(Item.ServerName, Title, from, to);
        }
    }

    /// <summary>
    /// The finding window converted to local time via the passed offset, with a sensible fallback
    /// when the producer carried no window (a 2h band ending "now"). Tests pass offset 0 (UTC) for
    /// determinism; the tab passes the viewer machine's local offset.
    /// </summary>
    private (DateTime From, DateTime To) LocalWindow()
    {
        if (Item.WindowStartUtc is { } su && Item.WindowEndUtc is { } eu)
            return (su.AddMinutes(_utcOffsetMinutes), eu.AddMinutes(_utcOffsetMinutes));

        var now = DateTime.UtcNow.AddMinutes(_utcOffsetMinutes);
        return (now.AddHours(-2), now);
    }
}

/// <summary>
/// An incident group of cards, rendered as a collapsible section. Plain DTO so the grouping is
/// unit-testable. Mirrors Lite's <c>LiteRecommendationSectionViewModel</c>.
/// </summary>
public sealed class RecommendationSectionViewModel
{
    public RecommendationSectionViewModel(
        RecommendationSeverity severity, IReadOnlyList<RecommendationCardViewModel> cards, bool expanded,
        string header)
    {
        Severity = severity;
        Cards = cards ?? throw new ArgumentNullException(nameof(cards));
        IsExpanded = expanded;
        Header = header;
    }

    /// <summary>The severity of the section's primary (highest) finding.</summary>
    public RecommendationSeverity Severity { get; }

    /// <summary>The cards in this section, in the reader's severity-desc order.</summary>
    public IReadOnlyList<RecommendationCardViewModel> Cards { get; }

    /// <summary>How many cards the section holds.</summary>
    public int Count => Cards.Count;

    /// <summary>Whether the section's expander starts expanded.</summary>
    public bool IsExpanded { get; }

    /// <summary>The header label: primary finding + count (when &gt; 1) + severity.</summary>
    public string Header { get; }
}

/// <summary>
/// The pure, WPF-free core of the viewer's Recommendations surface. Maps the persisted
/// <see cref="ViewerFindingRow"/> list (already severity-sorted by
/// <see cref="ViewerDataService.MapFindings"/>) to advise-only items, appends the co-fired
/// cross-reference, and groups by INCIDENT into the collapsible sections the tab renders. Keeping
/// the mapping + grouping here (rather than in code-behind) makes it directly unit-testable.
/// Mirrors Lite's <c>LiteRecommendationsReader</c> (per-finding mapping) + <c>LiteRecommendationsViewModel</c>
/// (grouping), reconciled to the viewer's persisted-finding read.
///
/// <para>
/// This is a snapshot view-model: each load builds a fresh instance and the tab reassigns its bound
/// collections, so there is no <c>INotifyPropertyChanged</c> surface to reason about.
/// </para>
/// </summary>
public sealed class RecommendationsViewModel
{
    /// <summary>The incident sections, in severity order, or empty.</summary>
    public IReadOnlyList<RecommendationSectionViewModel> Sections { get; }

    /// <summary>The selected top-level state.</summary>
    public RecommendationsState State { get; }

    /// <summary>Total card count across all sections.</summary>
    public int TotalCount => Sections.Sum(s => s.Count);

    private RecommendationsViewModel(IReadOnlyList<RecommendationSectionViewModel> sections, RecommendationsState state)
    {
        Sections = sections;
        State = state;
    }

    /// <summary>Builds the loading-state view-model (no data yet, read in flight).</summary>
    public static RecommendationsViewModel Loading() =>
        new(Array.Empty<RecommendationSectionViewModel>(), RecommendationsState.Loading);

    /// <summary>
    /// Builds a loaded/empty view-model from the persisted finding rows. Maps each row to an
    /// advise-only item, appends the co-fired cross-reference, and groups by incident. The state is
    /// <see cref="RecommendationsState.Empty"/> when there are no rows, else
    /// <see cref="RecommendationsState.Loaded"/>. <paramref name="utcOffsetMinutes"/> is carried onto
    /// each card for the Ask-AI prompt's window. The rows arrive pre-sorted (severity band desc, raw
    /// desc, database, title) from the read, and grouping preserves that order.
    /// </summary>
    public static RecommendationsViewModel FromFindings(
        IReadOnlyList<ViewerFindingRow> rows, string serverName, int utcOffsetMinutes = 0)
    {
        if (rows is null || rows.Count == 0)
            return new(Array.Empty<RecommendationSectionViewModel>(), RecommendationsState.Empty);

        var items = new List<RecommendationItem>(rows.Count);
        foreach (var row in rows)
        {
            if (row is null)
                continue;
            items.Add(MapItem(row, serverName));
        }

        if (items.Count == 0)
            return new(Array.Empty<RecommendationSectionViewModel>(), RecommendationsState.Empty);

        AppendCoFired(items);
        return new(GroupByIncident(items, utcOffsetMinutes), RecommendationsState.Loaded);
    }

    /// <summary>
    /// Maps one persisted finding row to an advise-only <see cref="RecommendationItem"/>. Reuses the
    /// row's already-computed Title / RawSeverity / DatabaseName (so the card banding and title match
    /// the grid mapping exactly), and derives the advice prose + copy-paste SQL + incident/window from
    /// the carried <see cref="AnalysisFinding"/>. Advice comes from <see cref="FactAdvice"/> for the
    /// root fact key; copy-paste SQL comes from the PERSISTED remediation action
    /// (<see cref="BuildCopyPasteSql"/>). Exposed <c>internal</c> for tests.
    /// </summary>
    internal static RecommendationItem MapItem(ViewerFindingRow row, string serverName)
    {
        var finding = row.Finding;
        var advice = FactAdvice.GetComposedForFinding(finding);

        return new RecommendationItem
        {
            Severity = ToSeverity(row.RawSeverity),
            RawSeverity = row.RawSeverity,
            Database = string.IsNullOrEmpty(row.DatabaseName) ? null : row.DatabaseName,
            Title = row.Title,
            AdviceText = ComposeAdvice(advice),
            CopyPasteSql = BuildCopyPasteSql(finding.Remediation),
            IncidentId = finding.IncidentId,
            ServerName = serverName ?? string.Empty,
            WindowStartUtc = AsUtc(finding.TimeRangeStart),
            WindowEndUtc = AsUtc(finding.TimeRangeEnd)
        };
    }

    /// <summary>
    /// Maps an engine finding's <c>double</c> severity onto the canonical band with the SAME cutoffs
    /// both apps' recommendation readers use (and <see cref="ViewerDataService.SeverityBand"/>):
    /// &gt;= 1.5 Critical, &gt;= 0.75 Warning, else Info.
    /// </summary>
    internal static RecommendationSeverity ToSeverity(double severity)
    {
        if (severity >= 1.5)
            return RecommendationSeverity.Critical;
        if (severity >= 0.75)
            return RecommendationSeverity.Warning;
        return RecommendationSeverity.Info;
    }

    /// <summary>
    /// Composes the advice prose from an <see cref="AdviceBlock"/>: the remediation line, with the
    /// investigation line appended when present. Null when no advice matched the root fact key.
    /// Mirrors Lite's / the Dashboard's ComposeEngineAdvice.
    /// </summary>
    internal static string? ComposeAdvice(AdviceBlock? advice)
    {
        if (advice is null)
            return null;

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(advice.Remediation))
            sb.Append(advice.Remediation);
        if (!string.IsNullOrEmpty(advice.Investigation))
        {
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(advice.Investigation);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Rebuilds the copy-paste T-SQL for a card from the finding's PERSISTED
    /// <see cref="RemediationAction"/> — the viewer reads persisted findings, whose ephemeral
    /// drill-down is NOT stored (so <see cref="FactRemediation.GenerateForFinding"/> returns null on
    /// read), and the built action IS persisted. This mirrors the Dashboard RecommendationsReader's
    /// read-back derivation (BuildCopyPasteFromAction) over the three target shapes that round-trip a
    /// copy-paste statement: percent-autogrowth MODIFY FILE (via the shared
    /// <see cref="FactRemediation.BuildModifyFileStatement"/>), the SQL Server-suggested missing-index
    /// CREATE, and the safe DB-config ALTER DATABASE ... SET. Returns null for any other action
    /// (force-plan / clear-plan / server-config / none), matching the Dashboard's 1:1 read path.
    /// </summary>
    internal static string? BuildCopyPasteSql(RemediationAction? action)
    {
        if (action is null)
            return null;

        // Percent-autogrowth: one MODIFY FILE per file, via the shared renderer so the copy-paste is
        // byte-identical to the drill-down's alter_statement. (Target lists are mutually exclusive.)
        if (action.FileGrowthTargets is { Count: > 0 } fileTargets)
        {
            var sb = new StringBuilder();
            foreach (var target in fileTargets)
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(FactRemediation.BuildModifyFileStatement(
                    target.Database, target.LogicalFileName, target.RecommendedGrowthMb));
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        // Missing index: the SQL Server-suggested CREATE statements, verbatim, one per line.
        if (action.MissingIndexTargets is { Count: > 0 } indexTargets)
        {
            var sb = new StringBuilder();
            foreach (var target in indexTargets)
            {
                if (string.IsNullOrWhiteSpace(target.CreateStatement))
                    continue;
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(target.CreateStatement);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        // Safe DB-config: one ALTER DATABASE ... SET per (database, setting) target.
        if (action.DbConfigTargets is { Count: > 0 } dbTargets)
        {
            var sb = new StringBuilder();
            foreach (var target in dbTargets)
            {
                var setClause = SetClauseFor(target.Setting);
                if (setClause is null)
                    continue;
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append($"ALTER DATABASE {QuoteName(target.Database)} {setClause};");
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        return null;
    }

    /// <summary>The SET-clause literal for a safe DB-config setting; null for the unmapped/destructive ones.</summary>
    private static string? SetClauseFor(DbConfigSetting setting) => setting switch
    {
        DbConfigSetting.AutoShrinkOff => "SET AUTO_SHRINK OFF",
        DbConfigSetting.AutoCloseOff => "SET AUTO_CLOSE OFF",
        DbConfigSetting.PageVerifyChecksum => "SET PAGE_VERIFY CHECKSUM",
        DbConfigSetting.ReadCommittedSnapshotOn => "SET READ_COMMITTED_SNAPSHOT ON",
        _ => null
    };

    /// <summary>QUOTENAME-equivalent: bracket an identifier, doubling any embedded close-bracket.</summary>
    private static string QuoteName(string identifier) =>
        "[" + (identifier ?? string.Empty).Replace("]", "]]") + "]";

    /// <summary>
    /// Appends a "what else fired in this analysis window" cross-reference to each card's advice text
    /// (correlate-and-focus slice 1) so related findings link to each other. Render-time, not frozen;
    /// no-op for a single card. Mirrors Lite's / the Dashboard's reader so all surfaces cross-reference
    /// the same way.
    /// </summary>
    internal static void AppendCoFired(List<RecommendationItem> items)
    {
        if (items.Count <= 1)
            return;

        var windowTitles = new List<(string Title, double Severity)>(items.Count);
        foreach (var it in items)
            windowTitles.Add((it.Title, it.RawSeverity));

        foreach (var it in items)
        {
            var line = CoFiredSummary.Line(CoFiredSummary.OtherTitles(it.Title, windowTitles));
            if (line is null)
                continue;
            it.AdviceText = string.IsNullOrEmpty(it.AdviceText) ? line : it.AdviceText + " " + line;
        }
    }

    /// <summary>
    /// Groups the severity-sorted items into one collapsible section per INCIDENT: cards sharing an
    /// <see cref="RecommendationItem.IncidentId"/> form one group headed by their primary
    /// (highest-severity) finding, so related findings read as one report. A finding with no incident
    /// id is its own single-card group. Groups appear in severity order (the input is severity-desc
    /// sorted). A group expands unless it is Info-only. Mirrors Lite's GroupByIncident.
    /// </summary>
    private static List<RecommendationSectionViewModel> GroupByIncident(
        IReadOnlyList<RecommendationItem> list, int utcOffsetMinutes)
    {
        var order = new List<string>();
        var buckets = new Dictionary<string, List<RecommendationItem>>(StringComparer.Ordinal);
        var soloCount = 0;
        foreach (var item in list)
        {
            var key = string.IsNullOrEmpty(item.IncidentId) ? "__solo_" + soloCount++ : item.IncidentId;
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<RecommendationItem>();
                buckets[key] = bucket;
                order.Add(key);
            }
            bucket.Add(item);
        }

        var sections = new List<RecommendationSectionViewModel>(order.Count);
        foreach (var key in order)
            sections.Add(BuildIncidentSection(buckets[key], utcOffsetMinutes));
        return sections;
    }

    /// <summary>
    /// Builds one incident section from its cards (kept in the reader's severity-desc order). The
    /// header names the primary (first) finding, the finding count, and the incident severity; the
    /// section expands unless the incident is Info-only. Mirrors Lite's BuildIncidentSection.
    /// </summary>
    private static RecommendationSectionViewModel BuildIncidentSection(
        IReadOnlyList<RecommendationItem> incidentItems, int utcOffsetMinutes)
    {
        var cards = incidentItems
            .Select(i => new RecommendationCardViewModel(i, utcOffsetMinutes))
            .ToList();
        var primary = cards[0]; // severity-desc sorted -> the first card is the incident primary
        var severity = primary.Severity;
        var label = severity switch
        {
            RecommendationSeverity.Critical => "CRITICAL",
            RecommendationSeverity.Warning => "WARNING",
            _ => "INFO"
        };
        var header = cards.Count > 1
            ? $"{primary.Title} · {cards.Count} findings · {label}"
            : $"{primary.Title} · {label}";
        return new RecommendationSectionViewModel(
            severity, cards, expanded: severity != RecommendationSeverity.Info, header);
    }

    /// <summary>
    /// Builds the MCP investigation prompt "Ask AI" copies to the clipboard for a finding. Pure (no
    /// WPF / clock) so the interpolation is unit-testable. The window times are formatted in whatever
    /// timezone the caller passed (the card passes viewer-local). Ported verbatim from Lite's prompt
    /// (RecommendationsTab AskAi_Click -> LiteRecommendationsViewModel.BuildAskAiPrompt).
    /// </summary>
    public static string BuildAskAiPrompt(string serverName, string title, DateTime from, DateTime to)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "Using the PerformanceMonitor MCP tools, investigate this finding on server \"{0}\": " +
            "\"{1}\". It was flagged around {2:yyyy-MM-dd HH:mm}–{3:HH:mm}. Call analyze_server / " +
            "get_analysis_findings and the relevant wait/blocking/memory tools, then tell me the " +
            "likely cause and what to do.",
            serverName, title, from, to);
    }

    /// <summary>
    /// Stamps a nullable engine timestamp as UTC (the pipeline records TimeRangeStart/End in UTC; the
    /// PG read-back tags them Utc, but re-stamp defensively). Null passes through.
    /// </summary>
    private static DateTime? AsUtc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}
