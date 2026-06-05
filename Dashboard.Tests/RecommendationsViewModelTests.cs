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
/// <see cref="RecommendationsState"/> (loading / insufficient-data / empty / loaded); the
/// incident-vs-config affordance predicates (Open-in-Active-Queries + Ask-AI for incidents,
/// Copy-fix for config rows; Apply/Mute visibility); and the Ask-AI prompt interpolation. The
/// WPF navigation + clipboard themselves are not unit-testable (they need a running Dashboard —
/// see the PR's visual-verification note).
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
        RecommendationSetting setting = RecommendationSetting.None,
        RemediationAction? remediation = null,
        string? sql = null,
        string title = "rec",
        string? db = null,
        double rawSeverity = 0.3,
        string? advice = null,
        DateTime? windowStartUtc = null,
        DateTime? windowEndUtc = null,
        string? storyHash = "hash",
        string? storyPath = "root>leaf")
    {
        return new RecommendationItem
        {
            CanonicalSeverity = severity,
            RawSeverity = rawSeverity,
            Source = source,
            Setting = setting,
            Remediation = remediation,
            CopyPasteSql = sql,
            Title = title,
            Database = db,
            AdviceText = advice,
            WindowStartUtc = windowStartUtc,
            WindowEndUtc = windowEndUtc,
            StoryPathHash = source == RecommendationSource.Engine ? storyHash : null,
            StoryPath = source == RecommendationSource.Engine ? storyPath : null
        };
    }

    private static RecommendationCardViewModel Card(
        RecommendationItem item, string serverName = "SRV", int utcOffsetMinutes = 0) =>
        new(item, serverName, utcOffsetMinutes);

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

    [Fact]
    public void FromItems_FlowsServerNameAndOffsetOntoCards()
    {
        var item = Item(
            CanonicalSeverity.Critical,
            windowStartUtc: new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            windowEndUtc: new DateTime(2026, 6, 1, 10, 30, 0, DateTimeKind.Utc));

        var vm = RecommendationsViewModel.FromItems(new[] { item }, "PROD-SQL-01", utcOffsetMinutes: 60);
        var card = vm.Sections[0].Cards[0];

        Assert.Equal("PROD-SQL-01", card.ServerName);
        // offset 60 → window shifts +1h into server-local in the Ask-AI prompt.
        Assert.Contains("11:00", card.AskAiPrompt);
        Assert.Contains("11:30", card.AskAiPrompt);
    }

    // ---- incident vs config affordances -----------------------------------

    [Fact]
    public void IncidentRow_ShowsOpenInActiveQueriesAndAskAi_NotCopyFix()
    {
        // Setting == None => incident (CPU/memory/blocking/waits/plan-regression).
        var card = Card(Item(CanonicalSeverity.Critical, setting: RecommendationSetting.None));

        Assert.True(card.IsIncident);
        Assert.False(card.IsConfigFix);
        Assert.True(card.ShowOpenInActiveQueries);
        Assert.True(card.ShowAskAi);
        Assert.False(card.ShowCopyFix);
    }

    [Theory]
    [InlineData(RecommendationSetting.AutoShrink)]
    [InlineData(RecommendationSetting.AutoClose)]
    [InlineData(RecommendationSetting.QueryStore)]
    [InlineData(RecommendationSetting.Rcsi)]
    [InlineData(RecommendationSetting.PageVerify)]
    public void ConfigFixRow_ShowsCopyFix_NotOpenInActiveQueriesOrAskAi(RecommendationSetting setting)
    {
        // Setting != None => standing config-fix; carries an ALTER → Copy fix.
        var card = Card(Item(
            CanonicalSeverity.Warning,
            setting: setting,
            sql: "ALTER DATABASE [db1] SET AUTO_SHRINK OFF;"));

        Assert.False(card.IsIncident);
        Assert.True(card.IsConfigFix);
        Assert.False(card.ShowOpenInActiveQueries);
        Assert.False(card.ShowAskAi);
        Assert.True(card.ShowCopyFix);
    }

    [Fact]
    public void ConfigFixRow_WithoutSql_DoesNotShowCopyFix()
    {
        var card = Card(Item(CanonicalSeverity.Warning, setting: RecommendationSetting.Rcsi, sql: null));

        Assert.True(card.IsConfigFix);
        Assert.False(card.ShowCopyFix);
    }

    [Fact]
    public void IncidentRow_DoesNotShowCopyFix_EvenIfSqlSomehowPresent()
    {
        // Defensive: an incident never offers Copy fix regardless of CopyPasteSql.
        var card = Card(Item(CanonicalSeverity.Critical, setting: RecommendationSetting.None, sql: "SELECT 1;"));

        Assert.True(card.IsIncident);
        Assert.False(card.ShowCopyFix);
    }

    // ---- Apply visibility (Remediation != null) ---------------------------

    [Fact]
    public void ShowApply_True_OnlyWhenRemediationPresent()
    {
        var withAction = Card(Item(
            CanonicalSeverity.Warning,
            setting: RecommendationSetting.AutoShrink,
            remediation: DbConfigAction(DbConfigSetting.AutoShrinkOff)));
        var withoutAction = Card(Item(CanonicalSeverity.Warning, remediation: null));

        Assert.True(withAction.ShowApply);
        Assert.False(withoutAction.ShowApply);
    }

    [Fact]
    public void ShowApply_True_ForIncidentWithRemediation()
    {
        // e.g. plan-regression / clear-plan: an incident (Setting==None) that still has an action.
        var card = Card(Item(
            CanonicalSeverity.Critical,
            setting: RecommendationSetting.None,
            remediation: new RemediationAction("CLEAR_PLAN", "clear", Array.Empty<ForcePlanTarget>())));

        Assert.True(card.IsIncident);
        Assert.True(card.ShowApply);
    }

    [Fact]
    public void ShowApply_False_ForLegacyRow()
    {
        // Legacy rows never carry a built action, so Apply is never shown.
        var legacy = Card(Item(CanonicalSeverity.Critical, source: RecommendationSource.Legacy, remediation: null));

        Assert.False(legacy.ShowApply);
    }

    // ---- Mute visibility (Source == Engine) -------------------------------

    [Fact]
    public void ShowMute_True_ForEngineRow()
    {
        var engine = Card(Item(CanonicalSeverity.Warning, source: RecommendationSource.Engine));

        Assert.True(engine.ShowMute);
    }

    [Fact]
    public void ShowMute_False_ForLegacyRow()
    {
        var legacy = Card(Item(CanonicalSeverity.Warning, source: RecommendationSource.Legacy));

        Assert.False(legacy.ShowMute);
    }

    // ---- Ask-AI prompt generation -----------------------------------------

    [Fact]
    public void BuildAskAiPrompt_InterpolatesServerTitleAndWindow()
    {
        var from = new DateTime(2026, 6, 1, 14, 5, 0);
        var to = new DateTime(2026, 6, 1, 15, 30, 0);

        var prompt = RecommendationsViewModel.BuildAskAiPrompt("PROD\\SQL2022", "High CXPACKET waits", from, to);

        Assert.Contains("server \"PROD\\SQL2022\"", prompt);
        Assert.Contains("\"High CXPACKET waits\"", prompt);
        Assert.Contains("2026-06-01 14:05", prompt);
        Assert.Contains("15:30", prompt);
        Assert.Contains("PerformanceMonitor MCP tools", prompt);
        Assert.Contains("analyze_server", prompt);
        Assert.Contains("get_analysis_findings", prompt);
    }

    [Fact]
    public void Card_AskAiPrompt_UsesItemTitleAndServerName()
    {
        var item = Item(
            CanonicalSeverity.Critical,
            title: "Blocking storm",
            windowStartUtc: new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc),
            windowEndUtc: new DateTime(2026, 6, 2, 9, 15, 0, DateTimeKind.Utc));

        var card = Card(item, serverName: "SQLBOX", utcOffsetMinutes: 0);

        Assert.Contains("server \"SQLBOX\"", card.AskAiPrompt);
        Assert.Contains("\"Blocking storm\"", card.AskAiPrompt);
        Assert.Contains("2026-06-02 09:00", card.AskAiPrompt);
        Assert.Contains("09:15", card.AskAiPrompt);
    }

    [Fact]
    public void Card_AskAiPrompt_WithNoWindow_UsesNowAnchoredFallback()
    {
        // No producer window → a 2h band ending "now" (server-local). Just assert it renders a
        // well-formed prompt (no crash, contains the fixed scaffolding + the title).
        var card = Card(Item(CanonicalSeverity.Critical, title: "Memory pressure"), serverName: "S1");

        Assert.Contains("server \"S1\"", card.AskAiPrompt);
        Assert.Contains("\"Memory pressure\"", card.AskAiPrompt);
        Assert.Contains("PerformanceMonitor MCP tools", card.AskAiPrompt);
    }

    // ---- deep-link window passthrough -------------------------------------

    [Fact]
    public void Card_ExposesRawUtcWindow_ForDeepLink()
    {
        var su = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var eu = new DateTime(2026, 6, 1, 10, 30, 0, DateTimeKind.Utc);
        var card = Card(Item(CanonicalSeverity.Critical, windowStartUtc: su, windowEndUtc: eu));

        Assert.Equal(su, card.WindowStartUtc);
        Assert.Equal(eu, card.WindowEndUtc);
    }

    // ---- card display flags ----------------------------------------------

    [Fact]
    public void Card_DatabaseBracketed_WrapsOrCollapses()
    {
        var withDb = Card(Item(CanonicalSeverity.Warning, db: "Sales"));
        var serverScoped = Card(Item(CanonicalSeverity.Warning, db: null));

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
        var card = Card(Item(severity));
        Assert.Equal(expected, card.SeverityLabel);
    }

    [Fact]
    public void EngineCard_ExposesMuteKeyInputs_HashAndPath()
    {
        // The Mute handler keys on the underlying item's StoryPathHash (the mute key) and carries
        // StoryPath (the operator-facing label). Both must round-trip through the card for engine rows.
        var card = Card(Item(
            CanonicalSeverity.Warning,
            source: RecommendationSource.Engine,
            storyHash: "h-42",
            storyPath: "blocking>rcsi>Sales"));

        Assert.True(card.ShowMute);
        Assert.Equal("h-42", card.Item.StoryPathHash);
        Assert.Equal("blocking>rcsi>Sales", card.Item.StoryPath);
    }

    [Fact]
    public void LegacyCard_HasNoMuteKeyInputs()
    {
        // Legacy rows never mute: no hash, no path, no Mute button.
        var card = Card(Item(CanonicalSeverity.Warning, source: RecommendationSource.Legacy));

        Assert.False(card.ShowMute);
        Assert.Null(card.Item.StoryPathHash);
        Assert.Null(card.Item.StoryPath);
    }
}
