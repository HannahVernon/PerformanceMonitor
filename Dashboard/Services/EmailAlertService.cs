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
using System.Text.Json;
using System.Threading.Tasks;
using PerformanceMonitor.Notifications;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Interfaces;
using PerformanceMonitorDashboard.Models;

namespace PerformanceMonitorDashboard.Services
{
    /// <summary>
    /// SMTP email sending service with per-metric cooldown and persistent alert log.
    /// Uses System.Net.Mail.SmtpClient (no new NuGet packages needed).
    /// </summary>
    public class EmailAlertService
    {
        private const string SmtpCredentialKey = "PerformanceMonitorDashboard_SMTP";
        private const int MaxAlertLogEntries = 1000;
        private static readonly AlertBranding s_branding = new("Performance Monitor Dashboard", null);

        /// <summary>Test seam: the branding this app feeds the shared email/template renderer.</summary>
        internal static AlertBranding Branding => s_branding;
        private static readonly CredentialService s_credentialService = new();
        private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

        private readonly IAlertSettings _settings;
        /* Retained for the LogAlertDismissals toggle in HideAlerts/HideAllAlerts, which is a
           history-management concern (not an alert setting) and so is not on IAlertSettings.
           Those methods move to JsonAlertHistoryStore in E3c; this field goes with them. */
        private readonly IUserPreferencesService _preferencesService;
        private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

        /* Alert log — loaded from JSON on startup, saved on exit, new alerts added in-memory */
        private readonly List<AlertLogEntry> _alertLog = new();
        private readonly object _alertLogLock = new();
        private readonly string _alertLogFilePath;

        /* Failure tracking for louder logging */
        private int _consecutiveFailures;
        private string? _lastFailureError;

        /// <summary>
        /// The current instance, set when MainWindow creates the service.
        /// Used by MCP tools to access alert history.
        /// </summary>
        public static EmailAlertService? Current { get; private set; }

        public EmailAlertService(IAlertSettings settings, IUserPreferencesService preferencesService)
        {
            _settings = settings;
            _preferencesService = preferencesService;
            Current = this;

            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PerformanceMonitorDashboard");
            Directory.CreateDirectory(appDataPath);
            _alertLogFilePath = Path.Combine(appDataPath, "alert_history.json");

            LoadAlertLog();
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
                        var lastPersistedSend = GetLastEmailSentUtc(serverId, metricName);
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

                            if (_consecutiveFailures <= 3)
                            {
                                Logger.Error($"ALERT EMAIL FAILED ({_consecutiveFailures}x): {ex.GetType().Name}: {ex.Message}");
                            }
                            else if (_consecutiveFailures % 50 == 0)
                            {
                                Logger.Error($"ALERT EMAIL STILL FAILING: {_consecutiveFailures} consecutive failures. Last error: {ex.Message}");
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
                Logger.Error($"TrySendAlertEmailAsync outer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Records an alert (tray notification or email) to the in-memory log.
        /// </summary>
        public void RecordAlert(string serverId, string serverName, string metricName,
            string currentValue, string thresholdValue, bool alertSent,
            string notificationType, string? sendError = null, bool muted = false, string? detailText = null,
            string? contextJson = null)
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
                SendError = sendError,
                Muted = muted,
                DetailText = detailText,
                ContextJson = contextJson
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
        /// Returns the AlertTime of the most recent log entry for the given
        /// (serverId, metricName), regardless of notification channel or send
        /// result. Used by <see cref="AnalysisNotificationService"/> to seed
        /// its per-finding cooldown across restarts. The analysis cooldown is
        /// stamped unconditionally, so the persisted equivalent ignores
        /// NotificationType (which can be "email", "webhook", or "tray" on
        /// Dashboard) and SendError. Returns null if no matching entry.
        /// </summary>
        public DateTime? GetLastAlertTime(string serverId, string metricName)
        {
            lock (_alertLogLock)
            {
                DateTime? max = null;
                foreach (var entry in _alertLog)
                {
                    if (entry.ServerId != serverId) continue;
                    if (entry.MetricName != metricName) continue;
                    if (max == null || entry.AlertTime > max.Value) max = entry.AlertTime;
                }
                return max;
            }
        }

        /// <summary>
        /// Gets alert history from the log (excludes hidden alerts).
        /// </summary>
        public List<AlertLogEntry> GetAlertHistory(int hoursBack = 24, int limit = 50)
        {
            var cutoff = DateTime.UtcNow.AddHours(-hoursBack);

            lock (_alertLogLock)
            {
                return _alertLog
                    .Where(a => a.AlertTime >= cutoff && !a.Hidden)
                    .OrderByDescending(a => a.AlertTime)
                    .Take(limit)
                    .ToList();
            }
        }

        /// <summary>
        /// Hides specific alerts matching the given keys.
        /// Each key is (AlertTime, ServerName, MetricName).
        /// </summary>
        public void HideAlerts(List<(DateTime AlertTime, string ServerName, string MetricName)> keys)
        {
            if (keys.Count == 0) return;

            var keySet = new HashSet<(DateTime, string, string)>(keys);
            int hidden = 0;

            lock (_alertLogLock)
            {
                foreach (var alert in _alertLog)
                {
                    if (keySet.Contains((alert.AlertTime, alert.ServerName, alert.MetricName)))
                    {
                        alert.Hidden = true;
                        hidden++;
                    }
                }
            }

            if (_preferencesService.GetPreferences().LogAlertDismissals)
                Logger.Info($"[AlertDismiss] Dismissed {hidden} of {keys.Count} selected alert(s)");
        }

        /// <summary>
        /// Hides all non-hidden alerts matching the time/server filter.
        /// </summary>
        public void HideAllAlerts(int hoursBack, string? serverName = null)
        {
            var cutoff = DateTime.UtcNow.AddHours(-hoursBack);
            int hidden = 0;

            lock (_alertLogLock)
            {
                foreach (var alert in _alertLog)
                {
                    if (!alert.Hidden &&
                        alert.AlertTime >= cutoff &&
                        (serverName == null || alert.ServerName == serverName))
                    {
                        alert.Hidden = true;
                        hidden++;
                    }
                }
            }

            if (_preferencesService.GetPreferences().LogAlertDismissals)
                Logger.Info($"[AlertDismiss] Dismissed all: {hidden} alert(s) hidden (hoursBack={hoursBack}, server={serverName ?? "all"})");
        }

        /// <summary>
        /// Gets email delivery health summary.
        /// </summary>
        public (int ConsecutiveFailures, string? LastError) GetEmailHealth()
        {
            return (_consecutiveFailures, _lastFailureError);
        }

        /// <summary>
        /// Returns the UTC time the most recent alert email was successfully
        /// sent for this server/metric, scanned from the in-memory alert log
        /// (which itself is loaded from alert_history.json on startup) — or
        /// null if none. Used to seed the in-memory cooldown after an app
        /// restart (#981 parity for Dashboard).
        /// </summary>
        /// <remarks>
        /// Dashboard records email and webhook deliveries as separate alert-log
        /// rows, so the filter is just NotificationType == "email" — Lite's
        /// combined "email+webhook" notification_type never appears here.
        /// </remarks>
        private DateTime? GetLastEmailSentUtc(string serverId, string metricName)
        {
            lock (_alertLogLock)
            {
                DateTime? max = null;
                foreach (var entry in _alertLog)
                {
                    if (entry.ServerId != serverId) continue;
                    if (entry.MetricName != metricName) continue;
                    if (entry.NotificationType != "email") continue;
                    if (!string.IsNullOrEmpty(entry.SendError)) continue;
                    if (max == null || entry.AlertTime > max.Value) max = entry.AlertTime;
                }
                return max;
            }
        }

        #region Alert Log Persistence

        /// <summary>
        /// Saves the alert log to a JSON file. Call on application exit.
        /// </summary>
        public void SaveAlertLog()
        {
            try
            {
                List<AlertLogEntry> snapshot;
                lock (_alertLogLock)
                {
                    snapshot = new List<AlertLogEntry>(_alertLog);
                }

                var json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
                File.WriteAllText(_alertLogFilePath, json);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save alert log: {ex.Message}");
            }
        }

        private void LoadAlertLog()
        {
            try
            {
                if (!File.Exists(_alertLogFilePath)) return;

                var json = File.ReadAllText(_alertLogFilePath);
                var entries = JsonSerializer.Deserialize<List<AlertLogEntry>>(json);

                if (entries != null)
                {
                    lock (_alertLogLock)
                    {
                        _alertLog.AddRange(entries);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load alert log, starting fresh: {ex.Message}");
            }
        }

        #endregion

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

    /// <summary>
    /// Represents a single alert event in the log.
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
        public bool Hidden { get; set; }
        public bool Muted { get; set; }
        public string? DetailText { get; set; }
        public string? ContextJson { get; set; }
    }
}
