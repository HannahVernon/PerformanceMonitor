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
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard.Controls;
using PerformanceMonitorDashboard.Services.Recommendations;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// WS1b-1 coverage for the PURE, WPF-free Recommendations view-model: grouping a flat
/// (already de-duped + severity-sorted) <see cref="RecommendationItem"/> list into the
/// Critical / Warning / Info collapsible sections; selecting the single top-level
/// <see cref="RecommendationsState"/> (loading / insufficient-data / empty / loaded); and the
/// per-card Apply/Mute visibility predicates. The XAML rendering itself is not unit-testable
/// (it needs a running Dashboard — see the PR's visual-verification note).
/// </summary>
public class RecommendationsViewModelTests
{
    // ---- builders ---------------------------------------------------------

    private static RemediationAction DbConfigAction(DbConfigSetting setting, string db = "db1") =>
        new(
            FactKey: "DB_CONFIG",
            Action: "set",
            Targets: Array.Empty<ForcePlanTarget>(),
            DbConfigTargets: new[] { new DbConfigTarget(db, setting) });

    private static RecommendationItem Item(
        CanonicalSeverity severity,
        RecommendationSource source = RecommendationSource.Engine,
        RemediationAction? remediation = null,
        string? sql = null,
        string title = "rec",
        string? db = null,
        double rawSeverity = 0.3,
        string? advice = null,
        string? storyHash = "hash")
    {
        return new RecommendationItem
        {
            CanonicalSeverity = severity,
            RawSeverity = rawSeverity,
            Source = source,
            Remediation = remediation,
            CopyPasteSql = sql,
            Title = title,
            Database = db,
            AdviceText = advice,
            StoryPathHash = source == RecommendationSource.Engine ? storyHash : null
        };
    }

    // ---- state selection --------------------------------------------------

    [Fact]
    public void Loading_SelectsLoadingState()
    {
        var vm = RecommendationsViewModel.Loading();

        Assert.Equal(RecommendationsState.Loading, vm.State);
        Assert.Empty(vm.Sections);
    }

    [Fact]
    public void FromItems_EmptyList_SelectsEmptyState()
    {
        var vm = RecommendationsViewModel.FromItems(Array.Empty<RecommendationItem>());

        Assert.Equal(RecommendationsState.Empty, vm.State);
        Assert.Empty(vm.Sections);
        Assert.Equal(0, vm.TotalCount);
    }

    [Fact]
    public void FromItems_NonEmptyList_SelectsLoadedState()
    {
        var vm = RecommendationsViewModel.FromItems(new[] { Item(CanonicalSeverity.Warning) });

        Assert.Equal(RecommendationsState.Loaded, vm.State);
        Assert.Equal(1, vm.TotalCount);
    }

    [Fact]
    public void InsufficientData_WithEngineMessage_UsesIt()
    {
        var vm = RecommendationsViewModel.InsufficientData("need 1.0 days, have 0.5 days");

        Assert.Equal(RecommendationsState.InsufficientData, vm.State);
        Assert.Equal("need 1.0 days, have 0.5 days", vm.InsufficientDataMessage);
        Assert.Empty(vm.Sections);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InsufficientData_WithBlankMessage_FallsBackToDefault(string? engineMessage)
    {
        var vm = RecommendationsViewModel.InsufficientData(engineMessage);

        Assert.Equal(RecommendationsState.InsufficientData, vm.State);
        Assert.Equal(RecommendationsViewModel.DefaultInsufficientDataMessage, vm.InsufficientDataMessage);
    }

    // ---- grouping ---------------------------------------------------------

    [Fact]
    public void FromItems_GroupsBySeverity_IntoThreeSectionsInFixedOrder()
    {
        var items = new[]
        {
            Item(CanonicalSeverity.Info, title: "i1"),
            Item(CanonicalSeverity.Critical, title: "c1"),
            Item(CanonicalSeverity.Warning, title: "w1"),
            Item(CanonicalSeverity.Critical, title: "c2"),
        };

        var vm = RecommendationsViewModel.FromItems(items);

        // Fixed display order Critical -> Warning -> Info, regardless of input order.
        Assert.Equal(3, vm.Sections.Count);
        Assert.Equal(CanonicalSeverity.Critical, vm.Sections[0].Severity);
        Assert.Equal(CanonicalSeverity.Warning, vm.Sections[1].Severity);
        Assert.Equal(CanonicalSeverity.Info, vm.Sections[2].Severity);

        Assert.Equal(2, vm.Sections[0].Count);
        Assert.Equal(1, vm.Sections[1].Count);
        Assert.Equal(1, vm.Sections[2].Count);
        Assert.Equal(4, vm.TotalCount);
    }

    [Fact]
    public void FromItems_OmitsEmptySeveritySections()
    {
        var items = new[]
        {
            Item(CanonicalSeverity.Warning, title: "w1"),
            Item(CanonicalSeverity.Warning, title: "w2"),
        };

        var vm = RecommendationsViewModel.FromItems(items);

        Assert.Single(vm.Sections);
        Assert.Equal(CanonicalSeverity.Warning, vm.Sections[0].Severity);
        Assert.Equal(2, vm.Sections[0].Count);
    }

    [Fact]
    public void FromItems_PreservesReaderOrderWithinASection()
    {
        // The reader returns severity-desc; within a band the order must be preserved as-is.
        var items = new[]
        {
            Item(CanonicalSeverity.Critical, title: "first", rawSeverity: 1.9),
            Item(CanonicalSeverity.Critical, title: "second", rawSeverity: 1.6),
            Item(CanonicalSeverity.Critical, title: "third", rawSeverity: 1.51),
        };

        var vm = RecommendationsViewModel.FromItems(items);

        var titles = vm.Sections[0].Cards.Select(c => c.Title).ToArray();
        Assert.Equal(new[] { "first", "second", "third" }, titles);
    }

    [Fact]
    public void FromItems_CriticalAndWarningExpanded_InfoCollapsed_ByDefault()
    {
        var items = new[]
        {
            Item(CanonicalSeverity.Critical, title: "c"),
            Item(CanonicalSeverity.Warning, title: "w"),
            Item(CanonicalSeverity.Info, title: "i"),
        };

        var vm = RecommendationsViewModel.FromItems(items);

        Assert.True(vm.Sections.Single(s => s.Severity == CanonicalSeverity.Critical).IsExpanded);
        Assert.True(vm.Sections.Single(s => s.Severity == CanonicalSeverity.Warning).IsExpanded);
        Assert.False(vm.Sections.Single(s => s.Severity == CanonicalSeverity.Info).IsExpanded);
    }

    [Fact]
    public void SectionHeader_IncludesCount()
    {
        var items = new[]
        {
            Item(CanonicalSeverity.Critical, title: "c1"),
            Item(CanonicalSeverity.Critical, title: "c2"),
            Item(CanonicalSeverity.Critical, title: "c3"),
        };

        var vm = RecommendationsViewModel.FromItems(items);

        Assert.Equal("Critical (3)", vm.Sections[0].Header);
    }

    // ---- Apply visibility (Remediation != null) ---------------------------

    [Fact]
    public void ShowApply_True_OnlyWhenRemediationPresent()
    {
        var withAction = new RecommendationCardViewModel(
            Item(CanonicalSeverity.Warning, remediation: DbConfigAction(DbConfigSetting.AutoShrinkOff)));
        var withoutAction = new RecommendationCardViewModel(
            Item(CanonicalSeverity.Warning, remediation: null));

        Assert.True(withAction.ShowApply);
        Assert.False(withoutAction.ShowApply);
    }

    [Fact]
    public void ShowApply_False_ForLegacyRow()
    {
        // Legacy rows never carry a built action, so Apply is never shown.
        var legacy = new RecommendationCardViewModel(
            Item(CanonicalSeverity.Critical, source: RecommendationSource.Legacy, remediation: null));

        Assert.False(legacy.ShowApply);
    }

    // ---- Mute visibility (Source == Engine) -------------------------------

    [Fact]
    public void ShowMute_True_ForEngineRow()
    {
        var engine = new RecommendationCardViewModel(
            Item(CanonicalSeverity.Warning, source: RecommendationSource.Engine));

        Assert.True(engine.ShowMute);
    }

    [Fact]
    public void ShowMute_False_ForLegacyRow()
    {
        var legacy = new RecommendationCardViewModel(
            Item(CanonicalSeverity.Warning, source: RecommendationSource.Legacy));

        Assert.False(legacy.ShowMute);
    }

    [Fact]
    public void EngineRowWithoutAction_ShowsMuteButNotApply()
    {
        // A CPU/wait engine finding with no executable shape: Mute (engine) yes, Apply no.
        var card = new RecommendationCardViewModel(
            Item(CanonicalSeverity.Critical, source: RecommendationSource.Engine, remediation: null));

        Assert.True(card.ShowMute);
        Assert.False(card.ShowApply);
    }

    // ---- card display flags ----------------------------------------------

    [Fact]
    public void Card_HasSql_TracksCopyPastePresence()
    {
        var withSql = new RecommendationCardViewModel(
            Item(CanonicalSeverity.Warning, sql: "ALTER DATABASE [db1] SET AUTO_SHRINK OFF;"));
        var withoutSql = new RecommendationCardViewModel(
            Item(CanonicalSeverity.Warning, sql: null));

        Assert.True(withSql.HasSql);
        Assert.False(withoutSql.HasSql);
    }

    [Fact]
    public void Card_DatabaseBracketed_WrapsOrCollapses()
    {
        var withDb = new RecommendationCardViewModel(Item(CanonicalSeverity.Warning, db: "Sales"));
        var serverScoped = new RecommendationCardViewModel(Item(CanonicalSeverity.Warning, db: null));

        Assert.True(withDb.HasDatabase);
        Assert.Equal("[Sales]", withDb.DatabaseBracketed);

        Assert.False(serverScoped.HasDatabase);
        Assert.Equal(string.Empty, serverScoped.DatabaseBracketed);
    }

    [Theory]
    [InlineData(CanonicalSeverity.Critical, "CRITICAL")]
    [InlineData(CanonicalSeverity.Warning, "WARNING")]
    [InlineData(CanonicalSeverity.Info, "INFO")]
    public void Card_SeverityLabel_MatchesBand(CanonicalSeverity severity, string expected)
    {
        var card = new RecommendationCardViewModel(Item(severity));
        Assert.Equal(expected, card.SeverityLabel);
    }

    [Fact]
    public void Card_ActionsDisabledReason_IsTheNextUpdateTooltip()
    {
        var card = new RecommendationCardViewModel(Item(CanonicalSeverity.Warning));
        Assert.Equal(RecommendationsViewModel.ActionsDisabledTooltip, card.ActionsDisabledReason);
    }
}
