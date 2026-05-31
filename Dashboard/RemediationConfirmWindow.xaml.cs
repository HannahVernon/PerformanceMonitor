/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using PerformanceMonitorDashboard.Services.Remediation;

namespace PerformanceMonitorDashboard
{
    /// <summary>
    /// The five-W confirm gate for an Apply / Un-apply. Shows the exact SQL, which
    /// server + database(s), the executing identity (SUSER_SNAME) and the operator,
    /// the original regression factor, and the M2 "verify the forced plan is still
    /// better against current data" caveat. The dialog only returns true when the
    /// operator explicitly clicks the apply button — that return value is the sole
    /// thing that lets <see cref="RemediationApplyService"/> reach the privileged
    /// handler.
    /// </summary>
    public partial class RemediationConfirmWindow : Window
    {
        public RemediationConfirmWindow(RemediationConfirmRequest request, bool resolvedByName, string? resolvedByNameReason)
        {
            InitializeComponent();

            var verb = request.IsUnapply ? "Un-apply Fix" : "Apply Fix";
            Title = $"Confirm {verb}";
            HeaderText.Text = request.IsUnapply
                ? $"Un-apply (unforce) the forced plan on {request.ServerDisplayName}?"
                : $"Force the historical-better plan on {request.ServerDisplayName}?";

            ServerText.Text = request.ServerDisplayName;
            ExecutingText.Text = string.IsNullOrEmpty(request.ExecutingLogin)
                ? "(monitoring login — re-probed at execution time via SUSER_SNAME())"
                : request.ExecutingLogin;
            OperatorText.Text = request.OperatorIdentity;

            if (resolvedByName)
            {
                ByNameBanner.Visibility = Visibility.Visible;
                ByNameText.Text = resolvedByNameReason
                    ?? "The source server was resolved by name (the alert did not carry a stable server id). "
                       + "Confirm this is the intended server before applying.";
            }

            TargetsHeader.Text = request.Targets.Count == 1 ? "Target" : $"Targets ({request.Targets.Count})";
            var rows = new List<TargetRow>();
            foreach (var t in request.Targets)
                rows.Add(TargetRow.From(t, request.IsUnapply));
            TargetsList.ItemsSource = rows;

            // M2 caveat is an apply-time judgment; on un-apply there is no "still
            // better" decision, so the caveat is hidden.
            if (request.IsUnapply)
            {
                CaveatBanner.Visibility = Visibility.Collapsed;
            }
            else
            {
                CaveatText.Text = RemediationConfirmRequest.StillBetterCaveat;
            }

            SqlPreview.Text = request.PreviewSql;

            // Audit-table-absent hard block: the privileged core would block every
            // target with no mutation, so disable the confirm button and say why.
            if (!request.AuditTableExists)
            {
                AuditAbsentBanner.Visibility = Visibility.Visible;
                AuditAbsentText.Text =
                    "This server is not on the 2.12.0 schema (config.remediation_action_log is absent). "
                    + "Apply Fix is hard-blocked here — no change will be made. Upgrade this server to "
                    + "2.12.0 to enable audited Apply Fix.";
                ConfirmButton.Content = request.IsUnapply ? "Un-apply" : "Apply";
                ConfirmButton.IsEnabled = false;
                ConfirmButton.ToolTip = AuditAbsentText.Text;
            }
            else if (!request.IsUnapply && !request.AnyActionable)
            {
                // Nothing applyable (already forced / stale / QS off / no ALTER / wrong DB).
                ConfirmButton.Content = $"Apply to {request.ServerDisplayName}";
                ConfirmButton.IsEnabled = false;
                ConfirmButton.ToolTip = "No target is in an applyable state — see the per-target notes above.";
            }
            else
            {
                ConfirmButton.Content = request.IsUnapply
                    ? $"Un-apply on {request.ServerDisplayName}"
                    : $"Apply to {request.ServerDisplayName}";
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>Bindable projection of one confirm target.</summary>
        private sealed class TargetRow
        {
            public string HeadLine { get; private set; } = "";
            public string StatusLine { get; private set; } = "";

            public static TargetRow From(RemediationConfirmTarget t, bool isUnapply)
            {
                var head = $"[{t.Database}]  query_id {t.QueryId}, plan_id {t.PlanId}";
                if (!isUnapply && t.RegressionFactor > 0)
                    head += $"  —  regression {t.RegressionFactor.ToString("0.#", CultureInfo.InvariantCulture)}x";

                var status = DescribeDisposition(t, isUnapply);
                return new TargetRow { HeadLine = head, StatusLine = status };
            }

            private static string DescribeDisposition(RemediationConfirmTarget t, bool isUnapply)
            {
                if (isUnapply)
                    return "Will unforce if this plan was forced by Apply Fix; otherwise skipped.";

                return t.Disposition switch
                {
                    RemediationDisposition.Ok => "Ready to apply.",
                    RemediationDisposition.WarnFailing => "⚠ " + (t.DispositionMessage ?? "Has a prior force failure; re-forcing may not help."),
                    RemediationDisposition.AlreadyForced => "Already forced — will be skipped.",
                    RemediationDisposition.BlockStale => "Plan/query no longer present — will be skipped.",
                    RemediationDisposition.BlockQueryStoreOff => "Query Store is not READ_WRITE — cannot force.",
                    RemediationDisposition.BlockNoAlter => "Monitoring login lacks ALTER — will fail closed (no change).",
                    RemediationDisposition.BlockWrongDatabase => "Connected DB does not match the target — will not proceed.",
                    RemediationDisposition.BlockAuditTableAbsent => "Audit table absent (pre-2.12.0) — hard-blocked.",
                    _ => t.DispositionMessage ?? "Unable to determine target state."
                };
            }
        }
    }
}
