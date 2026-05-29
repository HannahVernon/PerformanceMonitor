/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// SMTP email sending service with per-metric cooldown.
/// Uses System.Net.Mail.SmtpClient (no new NuGet packages needed).
/// </summary>
public class EmailAlertService
{
    private static readonly AlertBranding s_branding = new(
        "Performance Monitor Lite",
        "To silence this alert: open Performance Monitor Lite → Settings → Manage Mute Rules");

    /// <summary>Test seam: the branding this app feeds the shared email/template renderer.</summary>
    internal static AlertBranding Branding => s_branding;

    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly IAlertSettings _settings;
    private readonly IAlertHistoryStore _historyStore;
    private readonly ILogger<EmailAlertService> _logger;
    private readonly WebhookAlertService _webhookAlertService;

    /* Failure tracking for louder logging */
    private int _consecutiveSmtpFailures;
    private string? _lastSmtpError;

    public EmailAlertService(IAlertSettings settings, IAlertHistoryStore historyStore, ILogger<EmailAlertService> logger)
    {
        _settings = settings;
        _historyStore = historyStore;
        _logger = logger;
        _webhookAlertService = new WebhookAlertService(settings, s_branding, new AppLoggerAdapter<WebhookAlertService>());
    }

    /// <summary>
    /// Attempts to send an alert email if SMTP is enabled and cooldown has elapsed.
    /// Never throws — logs errors via the injected ILogger.
    /// </summary>
    public async Task TrySendAlertEmailAsync(
        string metricName,
        string serverName,
        string currentValue,
        string thresholdValue,
        int serverId = 0,
        AlertContext? context = null,
        double? numericCurrentValue = null,
        double? numericThresholdValue = null,
        bool muted = false,
        string? detailText = null)
    {
        try
        {
            string? sendError = null;
            bool sent = false;
            var notificationType = muted ? "muted" : "tray";

            /* Attempt email delivery if SMTP is fully configured and alert is not muted */
            if (!muted && _settings.SmtpEnabled &&
                !string.IsNullOrWhiteSpace(_settings.SmtpServer) &&
                !string.IsNullOrWhiteSpace(_settings.SmtpFromAddress) &&
                !string.IsNullOrWhiteSpace(_settings.SmtpRecipients))
            {
                var cooldownKey = $"{serverId}:{metricName}";

                /* Seed the in-memory cooldown from config_alert_log the first
                   time this key is seen, so an alert email sent shortly before
                   an app restart is not immediately re-sent afterward (#981).
                   The in-memory dictionary is authoritative once seeded. */
                if (!_cooldowns.ContainsKey(cooldownKey))
                {
                    var lastPersistedSend = await _historyStore.GetLastEmailSentUtcAsync(serverId.ToString(), metricName);
                    if (lastPersistedSend.HasValue)
                    {
                        _cooldowns.TryAdd(cooldownKey, lastPersistedSend.Value);
                    }
                }

                var withinCooldown = _cooldowns.TryGetValue(cooldownKey, out var lastSent) &&
                    DateTime.UtcNow - lastSent < TimeSpan.FromMinutes(_settings.EmailCooldownMinutes);

                if (!withinCooldown)
                {
                    notificationType = "email";

                    var subject = $"[SQL Monitor Alert] {metricName} on {serverName}";
                    var (htmlBody, plainTextBody) = EmailTemplateBuilder.BuildAlertEmail(
                        metricName, serverName, currentValue, thresholdValue, _settings.EmailCooldownMinutes, s_branding, context);

                    try
                    {
                        await SendEmailAsync(_settings, subject, htmlBody, plainTextBody, context);
                        sent = true;
                        _cooldowns[cooldownKey] = DateTime.UtcNow;

                        if (_consecutiveSmtpFailures > 0)
                        {
                            _logger.LogInformation($"Email delivery recovered after {_consecutiveSmtpFailures} failure(s)");
                        }
                        _consecutiveSmtpFailures = 0;
                        _lastSmtpError = null;

                        _logger.LogInformation($"Alert email sent for {metricName} on {serverName}");
                    }
                    catch (Exception ex)
                    {
                        sendError = ex.Message;
                        _consecutiveSmtpFailures++;
                        _lastSmtpError = ex.Message;

                        if (_consecutiveSmtpFailures <= 3)
                        {
                            _logger.LogError($"ALERT EMAIL FAILED ({_consecutiveSmtpFailures}x): {ex.GetType().Name}: {ex.Message}");
                        }
                        else if (_consecutiveSmtpFailures % 50 == 0)
                        {
                            _logger.LogError($"ALERT EMAIL STILL FAILING: {_consecutiveSmtpFailures} consecutive failures. Last error: {ex.Message}");
                        }
                    }
                }
            }

            /* Send webhook notifications (Teams / Slack) alongside email */
            bool webhookSent = false;
            if (!muted)
            {
                webhookSent = await _webhookAlertService.TrySendWebhookAlertsAsync(
                    metricName, serverName, currentValue, thresholdValue, serverId.ToString(), context);
            }

            /* Reflect webhook delivery in notification type */
            if (webhookSent)
            {
                notificationType = notificationType == "email" ? "email+webhook" : "webhook";
                sent = true;
            }

            /* Always log the alert, regardless of email status. The numeric
               current/threshold resolution happens in the store (it owns the
               DuckDB DOUBLE columns); the record carries both the display text
               and the optional numerics so no data is lost. */
            /* Persist the structured context as JSON alongside the flat detail_text so the
               in-app dialog can render the same advice / T-SQL / drill-down shape that
               email and webhooks already show (and survive purge of the source findings). */
            string? contextJson = context is not null ? AlertContextSerializer.Serialize(context) : null;
            await _historyStore.RecordAlertAsync(new AlertHistoryRecord(
                serverId.ToString(), serverName, metricName,
                currentValue, thresholdValue,
                numericCurrentValue, numericThresholdValue,
                sent, notificationType, sendError,
                muted, detailText, contextJson));
        }
        catch (Exception ex)
        {
            _logger.LogError($"TrySendAlertEmailAsync outer error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the UTC time of the most recent alert_log row for this
    /// (serverId, metricName), regardless of notification channel or
    /// delivery result. Used by <see cref="AnalysisNotificationService"/>
    /// to seed its per-finding cooldown across restarts — unlike the
    /// email cooldown (which filters to successful sends), the analysis
    /// cooldown is stamped unconditionally, so the persisted equivalent
    /// is the latest row for that metric_name, period.
    /// Delegates to the injected <see cref="IAlertHistoryStore"/> (the int
    /// serverId is bridged to the store's string serverId).
    /// </summary>
    public Task<DateTime?> GetLastAlertTimeAsync(int serverId, string metricName)
        => _historyStore.GetLastAlertTimeAsync(serverId.ToString(), metricName);

    /// <summary>
    /// Sends a test email to verify SMTP configuration.
    /// Returns null on success, or the error message on failure.
    /// </summary>
    public static async Task<string?> SendTestEmailAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(App.SmtpServer))
                return "SMTP server is not configured.";

            if (string.IsNullOrWhiteSpace(App.SmtpFromAddress))
                return "From address is not configured.";

            if (string.IsNullOrWhiteSpace(App.SmtpRecipients))
                return "No recipients configured.";

            var (htmlBody, plainTextBody) = EmailTemplateBuilder.BuildTestEmail(s_branding);
            await SendEmailAsync(new AppAlertSettings(), "[SQL Monitor] Test Email", htmlBody, plainTextBody);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Shared SMTP send helper with multipart/alternative (HTML + plain text).
    /// </summary>
    private static async Task SendEmailAsync(IAlertSettings settings, string subject, string htmlBody, string plainTextBody, AlertContext? context = null)
    {
        using var smtpClient = new SmtpClient(settings.SmtpServer, settings.SmtpPort)
        {
            EnableSsl = settings.SmtpUseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30000
        };

        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
        {
            var password = settings.GetSmtpPassword();
            smtpClient.Credentials = new NetworkCredential(settings.SmtpUsername, password ?? "");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(settings.SmtpFromAddress),
            Subject = subject
        };

        /* Multipart/alternative: plain text + HTML */
        var plainView = AlternateView.CreateAlternateViewFromString(plainTextBody, null, MediaTypeNames.Text.Plain);
        var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody, null, MediaTypeNames.Text.Html);
        message.AlternateViews.Add(plainView);
        message.AlternateViews.Add(htmlView);

        /* XML attachment (deadlock graph, blocked process report) */
        if (!string.IsNullOrEmpty(context?.AttachmentXml) && !string.IsNullOrEmpty(context?.AttachmentFileName))
        {
            var xmlBytes = Encoding.UTF8.GetBytes(context.AttachmentXml);
            var stream = new MemoryStream(xmlBytes);
            message.Attachments.Add(new Attachment(stream, context.AttachmentFileName, "application/xml"));
        }

        foreach (var recipient in settings.SmtpRecipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(recipient);
        }

        await smtpClient.SendMailAsync(message);
    }
}
