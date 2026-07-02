/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Darling's <see cref="IAlertDeliverer"/> — Lite's <c>EmailAlertService.TrySendAlertEmailAsync</c>
/// record-and-send cadence transplanted: the shared <see cref="EmailSendCore"/> attempts email
/// (when SMTP is configured and outside the per-fingerprint cooldown) and fans out to the shared
/// <see cref="WebhookAlertService"/> (Teams/Slack), then ONE combined <c>config_alert_log</c> row
/// is written per fired alert regardless of channel outcome — including muted alerts (flagged
/// muted, channels skipped) and alerts with no channel configured at all (recorded as 'tray',
/// Lite's taxonomy for delivered-without-email; the headless smoke asserts this row exists).
/// Never throws — a dead SMTP server or Postgres store must not abort the engine's sweep.
/// Delivery-mode fan-out (#1141 Per-event splitting, #1236 per-server override) is deliberately
/// NOT implemented: Darling ships Lite's Summary default.
/// </summary>
public sealed class DarlingAlertDeliverer : IAlertDeliverer
{
    private static readonly AlertBranding s_branding = new(
        "Performance Monitor Darling",
        /* Headless: no snooze-hint footer (Lite's points at its Settings window). */
        null);

    /// <summary>The branding this service feeds the shared email/template/webhook renderers.</summary>
    public static AlertBranding Branding => s_branding;

    private readonly IAlertHistoryStore _historyStore;
    private readonly EmailSendCore _core;
    private readonly ILogger _logger;

    public DarlingAlertDeliverer(
        IAlertSettings settings,
        IAlertHistoryStore historyStore,
        WebhookAlertService webhookAlertService,
        ILogger logger)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _core = new EmailSendCore(settings, historyStore, webhookAlertService, s_branding, logger);
    }

    public async Task DeliverAsync(AlertOutcome outcome, CancellationToken cancellationToken = default)
    {
        if (outcome is null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        try
        {
            /* Lite's EmailAlertService.cs:65-66 — muted alerts skip both channels but still
               record their history row below. */
            var result = await _core.TrySendAsync(
                outcome.MetricName, outcome.ServerName, outcome.CurrentValue, outcome.ThresholdValue,
                outcome.ServerKey, outcome.Context, attemptChannels: !outcome.Muted);

            /* Lite's single-row notification_type taxonomy (EmailAlertService.cs:68-80):
               muted → "muted"; email attempted → "email"; webhook delivery upgrades to
               "email+webhook"/"webhook"; otherwise "tray". */
            var notificationType = outcome.Muted ? "muted" : "tray";
            if (result.EmailAttempted)
            {
                notificationType = "email";
            }

            var sent = result.EmailSent;
            if (result.WebhookSent)
            {
                notificationType = notificationType == "email" ? "email+webhook" : "webhook";
                sent = true;
            }

            /* Always log the alert, regardless of channel status (EmailAlertService.cs:82-94) —
               the structured context persists as JSON alongside the flat detail_text. */
            string? contextJson = outcome.Context is not null ? AlertContextSerializer.Serialize(outcome.Context) : null;
            await _historyStore.RecordAlertAsync(new AlertHistoryRecord(
                outcome.ServerKey, outcome.ServerName, outcome.MetricName,
                outcome.CurrentValue, outcome.ThresholdValue,
                outcome.NumericCurrentValue, outcome.NumericThresholdValue,
                sent, notificationType, result.SendError,
                outcome.Muted, outcome.DetailText, contextJson));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Alert delivery failed for {Metric} on {Server}: {Message}",
                outcome.MetricName, outcome.ServerName, ex.Message);
        }
    }
}
