/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Notifications;
using PerformanceMonitorDashboard.Helpers;

namespace PerformanceMonitorDashboard.Services
{
    /// <summary>
    /// SMTP email sending service with per-metric cooldown and persistent alert log.
    /// Uses System.Net.Mail.SmtpClient (no new NuGet packages needed).
    /// <para>
    /// E2: the alert-history persistence (the in-memory <c>List&lt;AlertLogEntry&gt;</c>,
    /// load/save, and the GetAlertHistory / Hide* management API) moved to
    /// <see cref="JsonAlertHistoryStore"/>. The methods kept here are thin
    /// forwarders so existing consumers (AlertsHistoryContent, McpAlertTools,
    /// MainWindow, SettingsWindow) keep reaching <see cref="Current"/> unchanged;
    /// repointing them to the store is E3c.
    /// </para>
    /// </summary>
    public class EmailAlertService
    {
        private const string SmtpCredentialKey = "PerformanceMonitorDashboard_SMTP";
        private static readonly AlertBranding s_branding = new("Performance Monitor Dashboard", null);

        /// <summary>Test seam: the branding this app feeds the shared email/template renderer.</summary>
        internal static AlertBranding Branding => s_branding;
        private static readonly CredentialService s_credentialService = new();

        private readonly IAlertSettings _settings;
        private readonly JsonAlertHistoryStore _historyStore;
        private readonly ILogger<EmailAlertService> _logger;
        private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

        /* Failure tracking for louder logging */
        private int _consecutiveFailures;
        private string? _lastFailureError;

        /// <summary>
        /// The current instance, set when MainWindow creates the service.
        /// Used by MCP tools to access alert history.
        /// </summary>
        public static EmailAlertService? Current { get; private set; }

        public EmailAlertService(IAlertSettings settings, JsonAlertHistoryStore historyStore, ILogger<EmailAlertService> logger)
        {
            _settings = settings;
            _historyStore = historyStore;
            _logger = logger;
            Current = this;
        }

        /// <summary>
        /// Attempts to send alert notifications (email, Teams, Slack) based on enabled channels.
        /// Each channel operates independently — disabling email does not affect webhooks.
        /// Never throws.
        /// </summary>
        public async Task TrySendAlertEmailAsync(
            string metricName,
            string serverName,
            string currentValue,
            string thresholdValue,
            string serverId = "",
            AlertContext? context = null)
        {
            try
            {
                /* Attempt email delivery if SMTP is fully configured */
                if (_settings.SmtpEnabled &&
                    !string.IsNullOrWhiteSpace(_settings.SmtpServer) &&
                    !string.IsNullOrWhiteSpace(_settings.SmtpFromAddress) &&
                    !string.IsNullOrWhiteSpace(_settings.SmtpRecipients))
                {
                    var cooldownKey = $"{serverId}:{metricName}";

                    /* Seed the in-memory cooldown from the alert log the first
                       time this key is seen, so an alert email sent shortly
                       before an app restart is not immediately re-sent after
                       (#981 parity for Dashboard). The in-memory dictionary is
                       authoritative once seeded. */
                    if (!_cooldowns.ContainsKey(cooldownKey))
                    {
                        var lastPersistedSend = await _historyStore.GetLastEmailSentUtcAsync(serverId, metricName);
                        if (lastPersistedSend.HasValue)
                        {
                            _cooldowns.TryAdd(cooldownKey, lastPersistedSend.Value);
                        }
                    }

                    var withinCooldown = _cooldowns.TryGetValue(cooldownKey, out var lastSent) &&
                        DateTime.UtcNow - lastSent < TimeSpan.FromMinutes(_settings.EmailCooldownMinutes);

                    if (!withinCooldown)
                    {
                        bool sent = false;
                        string? sendError = null;
                        var subject = $"[SQL Monitor Alert] {metricName} on {serverName}";
                        var (htmlBody, plainTextBody) = EmailTemplateBuilder.BuildAlertEmail(
                            metricName, serverName, currentValue, thresholdValue, _settings.EmailCooldownMinutes, s_branding, context);

                        try
                        {
                            await SendEmailAsync(_settings, subject, htmlBody, plainTextBody, context);
                            sent = true;
                            _cooldowns[cooldownKey] = DateTime.UtcNow;

                            if (_consecutiveFailures > 0)
                            {
                                _logger.LogInformation($"Alert email delivery recovered after {_consecutiveFailures} failure(s)");
                            }
                            _consecutiveFailures = 0;
                            _lastFailureError = null;

                            _logger.LogInformation($"Alert email sent for {metricName} on {serverName}");
                        }
                        catch (Exception ex)
                        {
                            sendError = ex.Message;
                            _consecutiveFailures++;
                            _lastFailureError = ex.Message;

                            if (_consecutiveFailures <= 3)
                            {
                                _logger.LogError($"ALERT EMAIL FAILED ({_consecutiveFailures}x): {ex.GetType().Name}: {ex.Message}");
                            }
                            else if (_consecutiveFailures % 50 == 0)
                            {
                                _logger.LogError($"ALERT EMAIL STILL FAILING: {_consecutiveFailures} consecutive failures. Last error: {ex.Message}");
                            }
                        }

                        var emailContextJson = context is not null ? AlertContextSerializer.Serialize(context) : null;
                        RecordAlert(serverId, serverName, metricName, currentValue, thresholdValue, sent, "email", sendError, contextJson: emailContextJson);
                    }
                }

                /* Send webhook notifications (Teams / Slack) — independent of email */
                var webhookService = WebhookAlertService.Current;
                if (webhookService != null)
                {
                    var webhookSent = await webhookService.TrySendWebhookAlertsAsync(
                        metricName, serverName, currentValue, thresholdValue, serverId, context);
                    if (webhookSent)
                    {
                        var webhookContextJson = context is not null ? AlertContextSerializer.Serialize(context) : null;
                        RecordAlert(serverId, serverName, metricName, currentValue, thresholdValue, true, "webhook", contextJson: webhookContextJson);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"TrySendAlertEmailAsync outer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Records an alert (tray notification or email) to the alert-history store.
        /// Thin forwarder over <see cref="JsonAlertHistoryStore.RecordAlertAsync"/> —
        /// transitional until E3c repoints consumers at the store directly. The store
        /// completes synchronously (in-memory + trim), so this stays a sync method to
        /// preserve the existing call sites' shape.
        /// </summary>
        public void RecordAlert(string serverId, string serverName, string metricName,
            string currentValue, string thresholdValue, bool alertSent,
            string notificationType, string? sendError = null, bool muted = false, string? detailText = null,
            string? contextJson = null)
        {
            _historyStore.RecordAlertAsync(new AlertHistoryRecord(
                serverId, serverName, metricName,
                currentValue, thresholdValue,
                null, null,
                alertSent, notificationType, sendError,
                muted, detailText, contextJson)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns the AlertTime of the most recent log entry for the given
        /// (serverId, metricName). Thin forwarder over the store; transitional
        /// until E3c. Used by <see cref="AnalysisNotificationService"/>.
        /// </summary>
        public DateTime? GetLastAlertTime(string serverId, string metricName)
            => _historyStore.GetLastAlertTimeAsync(serverId, metricName).GetAwaiter().GetResult();

        /// <summary>
        /// Gets alert history from the log (excludes hidden alerts).
        /// Thin forwarder over the store; transitional until E3c.
        /// </summary>
        public List<AlertLogEntry> GetAlertHistory(int hoursBack = 24, int limit = 50)
            => _historyStore.GetAlertHistory(hoursBack, limit);

        /// <summary>
        /// Hides specific alerts matching the given keys.
        /// Thin forwarder over the store; transitional until E3c.
        /// </summary>
        public void HideAlerts(List<(DateTime AlertTime, string ServerName, string MetricName)> keys)
            => _historyStore.HideAlerts(keys);

        /// <summary>
        /// Hides all non-hidden alerts matching the time/server filter.
        /// Thin forwarder over the store; transitional until E3c.
        /// </summary>
        public void HideAllAlerts(int hoursBack, string? serverName = null)
            => _historyStore.HideAllAlerts(hoursBack, serverName);

        /// <summary>
        /// Saves the alert log to a JSON file. Call on application exit.
        /// Thin forwarder over the store; transitional until E3c.
        /// </summary>
        public void SaveAlertLog() => _historyStore.SaveAlertLog();

        /// <summary>
        /// Gets email delivery health summary.
        /// </summary>
        public (int ConsecutiveFailures, string? LastError) GetEmailHealth()
        {
            return (_consecutiveFailures, _lastFailureError);
        }

        /// <summary>
        /// Sends a test email to verify SMTP configuration.
        /// Returns null on success, or the error message on failure.
        /// </summary>
        public async Task<string?> SendTestEmailAsync(IAlertSettings settings)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(settings.SmtpServer))
                    return "SMTP server is not configured.";

                if (string.IsNullOrWhiteSpace(settings.SmtpFromAddress))
                    return "From address is not configured.";

                if (string.IsNullOrWhiteSpace(settings.SmtpRecipients))
                    return "No recipients configured.";

                var subject = "[SQL Monitor] Test Email";
                var (htmlBody, plainTextBody) = EmailTemplateBuilder.BuildTestEmail(s_branding);

                await SendEmailAsync(settings, subject, htmlBody, plainTextBody);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Gets the stored SMTP password from the credential manager.
        /// </summary>
        public static string? GetSmtpPassword()
        {
            try
            {
                var credential = s_credentialService.GetCredential(SmtpCredentialKey);
                return credential?.Password;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to retrieve SMTP password: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Saves the SMTP password to the credential manager.
        /// </summary>
        public static void SaveSmtpPassword(string password, string username)
        {
            try
            {
                s_credentialService.SaveCredential(SmtpCredentialKey, string.IsNullOrEmpty(username) ? "smtp" : username, password);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save SMTP password: {ex.Message}");
            }
        }

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
                var stream = new MemoryStream(xmlBytes); /* Disposed by MailMessage.Dispose() via Attachment chain */
                message.Attachments.Add(new Attachment(stream, context.AttachmentFileName, "application/xml"));
            }

            foreach (var recipient in settings.SmtpRecipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                message.To.Add(recipient);
            }

            await smtpClient.SendMailAsync(message);
        }
    }
}
