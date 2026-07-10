/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.PlanAnalysis;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The "Get Actual Plan" surface — the side-effecting sibling of "Fetch Live Plan" (see
/// <see cref="ViewerServerTab.LivePlan.cs"/>). Where Fetch Live Plan READS a cached plan, this RE-EXECUTES the
/// stored query on the target so the plan comes back with runtime statistics; it is on the Top Queries grid only
/// (mirroring Lite — a re-executable query row, not a procedure). The viewer never touches SQL Server: it enqueues
/// an <c>execute_actual_plan</c> command (identifier only — <see cref="ViewerDataService.BuildActualPlanArgs"/>)
/// and the SERVICE resolves the text from its store and re-executes.
///
/// <para>Because re-execution APPLIES the query's side effects, an informed-consent dialog runs first, and when
/// the query MODIFIES data (<see cref="QueryModificationDetector"/>, from the stored estimated plan — fail-safe
/// to "modifying" when the plan can't be analyzed) the dialog flags it PROMINENTLY and names the statement types.
/// FLAG, don't refuse: the operator decides. Enqueuing is a config write, so a read-only <c>viewer</c> seat is
/// short-circuited (and the enqueue's <see cref="ViewerReadOnlyException"/> is the backstop), same as Fetch Live
/// Plan. Same spinner + Cancel UX; the result opens in the same Plan Viewer host.</para>
/// </summary>
public partial class ViewerServerTab
{
    /// <summary>
    /// "Get Actual Plan" on a Top Queries row — re-executes the stored query on the target (via the service) to
    /// capture its ACTUAL plan. Gated in XAML on the row's <c>CanGetActualPlan</c>, so it only fires for a Top
    /// Queries (<see cref="ViewerQueryStatsRow"/>) row that carries query text + a query_hash; the Query Store /
    /// Top Procedures rows that share the menu stay disabled.
    /// </summary>
    private async void GetActualQueryPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var grid = FindParentDataGrid(menuItem);
        if (grid?.CurrentItem is not ViewerQueryStatsRow stats || string.IsNullOrEmpty(stats.QueryHash)) return;

        /* A read-only seat can't enqueue a command — short-circuit with the explanation (the enqueue's
           ViewerReadOnlyException is the belt-and-braces backstop). */
        if (BlockedByReadOnlySeat()) return;

        var label = $"Actual Plan - {stats.QueryHash}";

        /* Fetch the stored ESTIMATED plan for modification detection — the viewer reads its own store (the
           SERVICE re-resolves the query text by identifier when it executes). A missing/unparseable plan makes
           the detector return the fail-safe "uncertain → modifying" verdict, so the dialog still warns. */
        string? estimatedPlanXml = null;
        try
        {
            estimatedPlanXml = await _dataService.GetQueryStatsPlanXmlAsync(_server.ServerId, stats.DatabaseName, stats.QueryHash);
        }
        catch
        {
            /* Detection degrades to the fail-safe uncertain path — no plan means "treat as modifying". */
        }

        var modification = QueryModificationDetector.Detect(estimatedPlanXml, stats.QueryText);
        if (!ConfirmActualPlanExecution(stats.DatabaseName, modification))
        {
            return;
        }

        var argsJson = ViewerDataService.BuildActualPlanArgs(stats.QueryHash, stats.DatabaseName);
        await ExecuteAndOpenActualPlanAsync(argsJson, label, stats.QueryText);
    }

    /// <summary>
    /// The informed-consent gate: warns that the query WILL EXECUTE against [server].[database], and — when the
    /// modification detector says it writes data — prepends a PROMINENT, distinct block naming the statement types
    /// and stating plainly that re-executing RE-APPLIES those changes. Returns true only on OK.
    /// </summary>
    private bool ConfirmActualPlanExecution(string databaseName, QueryModificationInfo modification)
    {
        var prompt = new StringBuilder();

        var modificationWarning = QueryModificationDetector.BuildConsentWarning(modification, databaseName);
        if (modificationWarning.Length > 0)
        {
            prompt.AppendLine(modificationWarning);
            prompt.AppendLine("──────────────────────────────────────────");
            prompt.AppendLine();
        }

        var db = string.IsNullOrWhiteSpace(databaseName) ? "the default database" : $"[{databaseName}]";
        prompt.AppendLine($"The service will EXECUTE this query against [{_server.DisplayName}] in database {db} to capture its actual plan.");
        prompt.AppendLine();
        prompt.AppendLine("It runs as the service's stored monitoring login, with SET STATISTICS XML ON. All data results are discarded — only the plan is returned.");

        var result = MessageBox.Show(
            prompt.ToString(),
            modification.ModifiesData ? "Get Actual Plan — DATA WILL BE MODIFIED" : "Get Actual Plan",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.OK;
    }

    /// <summary>
    /// Enqueues the actual-plan capture, shows the "Capturing actual plan…" spinner (cancelable via the overlay's
    /// Cancel button), and either opens the returned plan or shows the service's failure / no-plan / timeout
    /// message. Mirrors <see cref="FetchAndOpenLivePlanAsync"/>'s outcome handling, incl. the read-only backstop.
    /// </summary>
    private async Task ExecuteAndOpenActualPlanAsync(string argsJson, string label, string? queryText)
    {
        BeginActualPlanCapture(label);
        try
        {
            var result = await _dataService.ExecuteActualPlanAsync(_server.ServerId, argsJson, _planLoadCts!.Token);
            if (result.Status == ActualPlanStatus.Captured)
            {
                OpenPlanTab(result.PlanXml!, label, queryText); /* HidePlanLoading runs inside OpenPlanTab */
            }
            else
            {
                HidePlanLoading();
                ShowActualPlanOutcome(result);
            }
        }
        catch (OperationCanceledException)
        {
            HidePlanLoading();
        }
        catch (ViewerReadOnlyException)
        {
            HidePlanLoading();
            BlockedByReadOnlySeat(force: true);
        }
        catch (Exception ex)
        {
            HidePlanLoading();
            MessageBox.Show($"Failed to capture the actual plan:\n\n{ex.Message}", "Actual Plan Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Shows the Plan Viewer's loading overlay with an actual-plan (re-executing) label + a fresh CTS.</summary>
    private void BeginActualPlanCapture(string label)
    {
        ShowPlanLoading(label);
        PlanLoadingLabel.Text = $"Capturing actual plan (re-executing): {label}";
        _planLoadCts?.Dispose();
        _planLoadCts = new System.Threading.CancellationTokenSource();
    }

    /// <summary>Surfaces a non-captured outcome (no-plan info, or a failure/timeout warning with the service's message).</summary>
    private void ShowActualPlanOutcome(ActualPlanResult result)
    {
        var (caption, icon) = result.Status switch
        {
            ActualPlanStatus.NoPlanCaptured => ("No Plan Captured", MessageBoxImage.Information),
            ActualPlanStatus.TimedOut => ("Actual Plan Still Running", MessageBoxImage.Warning),
            _ => ("Actual Plan Failed", MessageBoxImage.Warning),
        };
        MessageBox.Show(result.Message ?? "The actual plan could not be captured.", caption, MessageBoxButton.OK, icon);
    }
}
