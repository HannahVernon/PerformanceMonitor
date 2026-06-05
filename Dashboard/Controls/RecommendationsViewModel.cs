/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using PerformanceMonitorDashboard.Services.Recommendations;

namespace PerformanceMonitorDashboard.Controls
{
    /// <summary>
    /// Which top-level state the Recommendations surface is in. The control swaps a single
    /// visible region per state; the view-model picks exactly one from the load inputs so the
    /// selection is unit-testable without WPF (WS1b-1).
    /// </summary>
    public enum RecommendationsState
    {
        /// <summary>A read is in flight — show the spinner.</summary>
        Loading,

        /// <summary>
        /// The engine has not yet collected its minimum history window
        /// (<c>AnalysisService.MinimumDataHours</c>); recommendations are not meaningful yet.
        /// </summary>
        InsufficientData,

        /// <summary>The read completed and produced zero recommendations — the all-clear.</summary>
        Empty,

        /// <summary>The read completed with one or more recommendations to render.</summary>
        Loaded
    }

    /// <summary>
    /// A single recommendation rendered as a card. A plain DTO (no WPF dependency) wrapping a
    /// <see cref="RecommendationItem"/> plus the pre-computed display/visibility flags the XAML
    /// binds to, so the Apply/Mute visibility rules and the copy-paste presence are unit-testable
    /// (WS1b-1). The action wiring (Apply/Mute/Generate) is WS1b-2 — here the buttons render but
    /// are inert (see <see cref="ActionsDisabledReason"/>).
    /// </summary>
    public sealed class RecommendationCardViewModel
    {
        public RecommendationCardViewModel(RecommendationItem item)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
        }

        /// <summary>The underlying unified recommendation row.</summary>
        public RecommendationItem Item { get; }

        /// <summary>The three-band severity (drives the badge glyph + colour).</summary>
        public CanonicalSeverity Severity => Item.CanonicalSeverity;

        /// <summary>Short uppercase severity label for the badge, e.g. "CRITICAL".</summary>
        public string SeverityLabel => Item.CanonicalSeverity switch
        {
            CanonicalSeverity.Critical => "CRITICAL",
            CanonicalSeverity.Warning => "WARNING",
            _ => "INFO"
        };

        /// <summary>A glyph for the badge (Segoe MDL2 Assets code point) per severity.</summary>
        public string SeverityGlyph => Item.CanonicalSeverity switch
        {
            CanonicalSeverity.Critical => "", // error / critical
            CanonicalSeverity.Warning => "",  // warning triangle
            _ => ""                            // info
        };

        /// <summary>The card heading.</summary>
        public string Title => Item.Title;

        /// <summary>
        /// The affected database wrapped in brackets for display, or empty for a server-scoped
        /// rec (so the database line collapses).
        /// </summary>
        public string DatabaseBracketed =>
            string.IsNullOrEmpty(Item.Database) ? string.Empty : $"[{Item.Database}]";

        /// <summary>Whether a database line should be shown at all.</summary>
        public bool HasDatabase => !string.IsNullOrEmpty(Item.Database);

        /// <summary>The operator-facing advice prose.</summary>
        public string? AdviceText => Item.AdviceText;

        /// <summary>Whether there is advice prose to render.</summary>
        public bool HasAdvice => !string.IsNullOrEmpty(Item.AdviceText);

        /// <summary>The copy-paste-ready T-SQL, if any.</summary>
        public string? CopyPasteSql => Item.CopyPasteSql;

        /// <summary>
        /// Whether the "Show T-SQL" expander + Copy button should appear (only when there is SQL).
        /// </summary>
        public bool HasSql => !string.IsNullOrEmpty(Item.CopyPasteSql);

        /// <summary>
        /// Whether the Apply button is shown for this card. Apply is engine-only AND requires a
        /// built, persisted <see cref="RecommendationItem.Remediation"/> action — legacy rows and
        /// engine rows with no executable shape never show it. (Mirrors the
        /// <c>Remediation != null</c> rule the alert path uses.) The button is rendered DISABLED in
        /// WS1b-1; the action is wired in WS1b-2.
        /// </summary>
        public bool ShowApply => Item.Remediation != null;

        /// <summary>
        /// Whether the Mute button is shown. Mute is an engine-only concept (the legacy
        /// <c>config.critical_issues</c> store has no mute), so it shows only for
        /// <see cref="RecommendationSource.Engine"/> rows. Rendered DISABLED in WS1b-1.
        /// </summary>
        public bool ShowMute => Item.Source == RecommendationSource.Engine;

        /// <summary>
        /// The tooltip shown on the (disabled) Apply/Mute buttons in WS1b-1. The action wiring
        /// lands in WS1b-2; until then the buttons are present-but-inert so the approved layout is
        /// fully reviewable without any half-working behaviour.
        /// </summary>
        public string ActionsDisabledReason => RecommendationsViewModel.ActionsDisabledTooltip;
    }

    /// <summary>
    /// A severity group (Critical / Warning / Info) of cards, rendered as a collapsible section.
    /// Plain DTO so the grouping is unit-testable (WS1b-1).
    /// </summary>
    public sealed class RecommendationSectionViewModel
    {
        public RecommendationSectionViewModel(
            CanonicalSeverity severity, IReadOnlyList<RecommendationCardViewModel> cards, bool expanded)
        {
            Severity = severity;
            Cards = cards ?? throw new ArgumentNullException(nameof(cards));
            IsExpanded = expanded;
        }

        /// <summary>The severity this section groups.</summary>
        public CanonicalSeverity Severity { get; }

        /// <summary>The cards in this section, in the order the reader returned them.</summary>
        public IReadOnlyList<RecommendationCardViewModel> Cards { get; }

        /// <summary>How many cards the section holds.</summary>
        public int Count => Cards.Count;

        /// <summary>Whether the section's expander starts expanded.</summary>
        public bool IsExpanded { get; }

        /// <summary>The header label, e.g. "Critical (3)".</summary>
        public string Header => Severity switch
        {
            CanonicalSeverity.Critical => $"Critical ({Count})",
            CanonicalSeverity.Warning => $"Warning ({Count})",
            _ => $"Info ({Count})"
        };
    }

    /// <summary>
    /// The pure, WPF-free core of the Recommendations surface (WS1b-1). Groups a flat
    /// <see cref="RecommendationItem"/> list (already de-duped + severity-sorted by
    /// <c>RecommendationsReader</c>) into the Critical / Warning / Info sections the control
    /// renders, and selects the single top-level <see cref="RecommendationsState"/>. Keeping the
    /// grouping + state logic here (rather than in code-behind) makes it directly unit-testable.
    ///
    /// <para>
    /// This is a snapshot view-model: each load builds a fresh instance and the control reassigns
    /// its bound collections, so there is no <c>INotifyPropertyChanged</c> surface to reason about.
    /// </para>
    /// </summary>
    public sealed class RecommendationsViewModel
    {
        /// <summary>
        /// Tooltip on the disabled Apply/Mute buttons until WS1b-2 wires their actions.
        /// </summary>
        public const string ActionsDisabledTooltip = "Available in the next update";

        /// <summary>The severity sections, Critical → Warning → Info, omitting empty ones.</summary>
        public IReadOnlyList<RecommendationSectionViewModel> Sections { get; }

        /// <summary>The selected top-level state.</summary>
        public RecommendationsState State { get; }

        /// <summary>
        /// The insufficient-data message to show in the <see cref="RecommendationsState.InsufficientData"/>
        /// state (the engine's own <c>InsufficientDataMessage</c>, or a default).
        /// </summary>
        public string InsufficientDataMessage { get; }

        /// <summary>Total card count across all sections.</summary>
        public int TotalCount => Sections.Sum(s => s.Count);

        private RecommendationsViewModel(
            IReadOnlyList<RecommendationSectionViewModel> sections,
            RecommendationsState state,
            string insufficientDataMessage)
        {
            Sections = sections;
            State = state;
            InsufficientDataMessage = insufficientDataMessage;
        }

        /// <summary>The default insufficient-data prose when the engine supplied none.</summary>
        public const string DefaultInsufficientDataMessage =
            "Collecting data — recommendations appear after the engine has at least 24 hours of history. " +
            "Keep the collector running and check back later.";

        /// <summary>
        /// Builds the loading-state view-model (no data yet, read in flight).
        /// </summary>
        public static RecommendationsViewModel Loading() =>
            new(Array.Empty<RecommendationSectionViewModel>(), RecommendationsState.Loading, string.Empty);

        /// <summary>
        /// Builds the insufficient-data-state view-model from the engine's message (or the default
        /// when it is null/blank).
        /// </summary>
        public static RecommendationsViewModel InsufficientData(string? engineMessage) =>
            new(
                Array.Empty<RecommendationSectionViewModel>(),
                RecommendationsState.InsufficientData,
                string.IsNullOrWhiteSpace(engineMessage) ? DefaultInsufficientDataMessage : engineMessage!);

        /// <summary>
        /// Builds a loaded/empty view-model from the reader's flat, already-sorted list. Groups by
        /// <see cref="CanonicalSeverity"/> into Critical / Warning / Info sections (empty sections
        /// are omitted), preserving the reader's intra-severity order. Critical and Warning start
        /// expanded; Info starts collapsed. The state is <see cref="RecommendationsState.Empty"/>
        /// when the list is empty, else <see cref="RecommendationsState.Loaded"/>.
        /// </summary>
        public static RecommendationsViewModel FromItems(IEnumerable<RecommendationItem> items)
        {
            var list = items as IReadOnlyList<RecommendationItem> ?? items?.ToList()
                       ?? (IReadOnlyList<RecommendationItem>)Array.Empty<RecommendationItem>();

            if (list.Count == 0)
                return new(Array.Empty<RecommendationSectionViewModel>(), RecommendationsState.Empty, string.Empty);

            var sections = new List<RecommendationSectionViewModel>(3);
            // Fixed display order Critical → Warning → Info, regardless of which bands are present.
            AppendSection(sections, list, CanonicalSeverity.Critical, expanded: true);
            AppendSection(sections, list, CanonicalSeverity.Warning, expanded: true);
            AppendSection(sections, list, CanonicalSeverity.Info, expanded: false);

            return new(sections, RecommendationsState.Loaded, string.Empty);
        }

        private static void AppendSection(
            List<RecommendationSectionViewModel> sections,
            IReadOnlyList<RecommendationItem> all,
            CanonicalSeverity severity,
            bool expanded)
        {
            // Preserve the reader's order within the band (it is already severity-desc sorted).
            var cards = all
                .Where(i => i.CanonicalSeverity == severity)
                .Select(i => new RecommendationCardViewModel(i))
                .ToList();

            if (cards.Count > 0)
                sections.Add(new RecommendationSectionViewModel(severity, cards, expanded));
        }
    }
}
