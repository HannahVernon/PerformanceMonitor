/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;

namespace PerformanceMonitorDashboard.Services
{
    /// <summary>
    /// SMTP email sending service with per-metric cooldown and in-memory alert log.
    /// Uses System.Net.Mail.SmtpClient (no new NuGet packages needed).
    /// </summary>
    public class EmailAlertService
    {
        private const string SmtpCredentialKey = "PerformanceMonitorDashboard_SMTP";
        private const int MaxAlertLogEntries = 1000;
        private static readonly CredentialService s_credentialService = new();

        private readonly UserPreferencesService _preferencesService;
        private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
        private static readonly TimeSpan CooldownPeriod = TimeSpan.FromMinutes(15);

        /* In-memory alert log — survives for the lifetime of the application */
        private readonly List<AlertLogEntry> _alertLog = new();
        private readonly object _alertLogLock = new();

        /* Failure tracking for louder logging */
        private int _consecutiveFailures;
        private string? _lastFailureError;

        /// <summary>
        /// The current instance, set when MainWindow creates the service.
        /// Used by MCP tools to access alert history.
        /// </summary>
        public static EmailAlertService? Current { get; private set; }

        public EmailAlertService(UserPreferencesService preferencesService)
        {
            _preferencesService = preferencesService;
            Current = this;
        }

        /// <summary>
        /// Attempts to send an alert email if SMTP is enabled and cooldown has elapsed.
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
                var prefs = _preferencesService.GetPreferences();
                if (!prefs.SmtpEnabled) return;

                if (string.IsNullOrWhiteSpace(prefs.SmtpServer) ||
                    string.IsNullOrWhiteSpace(prefs.SmtpFromAddress) ||
                    string.IsNullOrWhiteSpace(prefs.SmtpRecipients))
                {
                    return;
                }

                var cooldownKey = $"{serverId}:{metricName}";
                if (_cooldowns.TryGetValue(cooldownKey, out var lastSent) &&
                    DateTime.UtcNow - lastSent < CooldownPeriod)
                {
                    return;
                }

                var subject = $"[SQL Monitor Alert] {metricName} on {serverName}";
                var (htmlBody, plainTextBody) = EmailTemplateBuilder.BuildAlertEmail(
                    metricName, serverName, currentValue, thresholdValue, context);

                string? sendError = null;
                bool sent = false;

                try
                {
                    await SendEmailAsync(prefs, subject, htmlBody, plainTextBody, context);
                    sent = true;
                    _cooldowns[cooldownKey] = DateTime.UtcNow;

                    /* Log recovery if we had previous failures */
                    if (_consecutiveFailures > 0)
                    {
                        Logger.Info($"Alert email delivery recovered after {_consecutiveFailures} failure(s)");
                    }
                    _consecutiveFailures = 0;
                    _lastFailureError = null;

                    Logger.Info($"Alert email sent for {metricName} on {serverName}");
                }
                catch (Exception ex)
                {
                    sendError = ex.Message;
                    _consecutiveFailures++;
                    _lastFailureError = ex.Message;

                    /* Loud on first 3 failures, then periodic reminders */
                    if (_consecutiveFailures <= 3)
                    {
                        Logger.Error($"ALERT EMAIL FAILED ({_consecutiveFailures}x): {ex.GetType().Name}: {ex.Message}");
                    }
                    else if (_consecutiveFailures % 50 == 0)
                    {
                        Logger.Error($"ALERT EMAIL STILL FAILING: {_consecutiveFailures} consecutive failures. Last error: {ex.Message}");
                    }
                }

                /* Log the alert attempt */
                RecordAlert(serverId, serverName, metricName, currentValue, thresholdValue, sent, "email", sendError);
            }
            catch (Exception ex)
            {
                Logger.Error($"TrySendAlertEmailAsync outer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Records an alert (tray notification or email) to the in-memory log.
        /// </summary>
        public void RecordAlert(string serverId, string serverName, string metricName,
            string currentValue, string thresholdValue, bool alertSent,
            string notificationType, string? sendError = null)
        {
            var entry = new AlertLogEntry
            {
                AlertTime = DateTime.UtcNow,
                ServerId = serverId,
                ServerName = serverName,
                MetricName = metricName,
                CurrentValue = currentValue,
                ThresholdValue = thresholdValue,
                AlertSent = alertSent,
                NotificationType = notificationType,
                SendError = sendError
            };

            lock (_alertLogLock)
            {
                _alertLog.Add(entry);

                /* Trim if over max */
                if (_alertLog.Count > MaxAlertLogEntries)
                {
                    _alertLog.RemoveRange(0, _alertLog.Count - MaxAlertLogEntries);
                }
            }
        }

        /// <summary>
        /// Gets alert history from the in-memory log.
        /// </summary>
        public List<AlertLogEntry> GetAlertHistory(int hoursBack = 24, int limit = 50)
        {
            var cutoff = DateTime.UtcNow.AddHours(-hoursBack);

            lock (_alertLogLock)
            {
                return _alertLog
                    .Where(a => a.AlertTime >= cutoff)
                    .OrderByDescending(a => a.AlertTime)
                    .Take(limit)
                    .ToList();
            }
        }

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
        public async Task<string?> SendTestEmailAsync(UserPreferences prefs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prefs.SmtpServer))
                    return "SMTP server is not configured.";

                if (string.IsNullOrWhiteSpace(prefs.SmtpFromAddress))
                    return "From address is not configured.";

                if (string.IsNullOrWhiteSpace(prefs.SmtpRecipients))
                    return "No recipients configured.";

                var subject = "[SQL Monitor] Test Email";
                var (htmlBody, plainTextBody) = EmailTemplateBuilder.BuildTestEmail();

                await SendEmailAsync(prefs, subject, htmlBody, plainTextBody);
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

        private static async Task SendEmailAsync(UserPreferences prefs, string subject, string htmlBody, string plainTextBody, AlertContext? context = null)
        {
            using var smtpClient = new SmtpClient(prefs.SmtpServer, prefs.SmtpPort)
            {
                EnableSsl = prefs.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 30000
            };

            if (!string.IsNullOrWhiteSpace(prefs.SmtpUsername))
            {
                var password = GetSmtpPassword();
                smtpClient.Credentials = new NetworkCredential(prefs.SmtpUsername, password ?? "");
            }

            using var message = new MailMessage
            {
                From = new MailAddress(prefs.SmtpFromAddress),
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

            foreach (var recipient in prefs.SmtpRecipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                message.To.Add(recipient);
            }

            await smtpClient.SendMailAsync(message);
        }
    }

    /// <summary>
    /// Represents a single alert event in the in-memory log.
    /// </summary>
    public class AlertLogEntry
    {
        public DateTime AlertTime { get; set; }
        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string MetricName { get; set; } = "";
        public string CurrentValue { get; set; } = "";
        public string ThresholdValue { get; set; } = "";
        public bool AlertSent { get; set; }
        public string NotificationType { get; set; } = "";
        public string? SendError { get; set; }
    }
}
