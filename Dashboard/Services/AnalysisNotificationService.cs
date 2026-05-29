/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard.Analysis;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitor.Notifications;
using PerformanceMonitorDashboard.Interfaces;
using PerformanceMonitorDashboard.Mcp;

namespace PerformanceMonitorDashboard.Services
{
    /// <summary>
    /// Routes high-severity analysis findings into the notification channels.
    /// Filters by severity, dedups per finding (so a recurring finding does not
    /// re-notify every analysis cycle), composes a structured message, and hands off
    /// to <see cref="EmailAlertService"/> — which fans out to email + Slack + Teams
    /// and logs to <c>alert_history.json</c>.
    ///
    /// <para>
    /// Called by <see cref="AnalysisScheduler"/> with the findings returned by each
    /// completed <c>AnalysisService.AnalyzeAsync</c>. Never throws.
    /// </para>
    /// </summary>
    public sealed class AnalysisNotificationService
    {
        private readonly EmailAlertService _emailAlertService;
        private readonly IAlertSettings _settings;
        private readonly IServerManager _serverManager;

        /// <summary>
        /// Per-finding re-notification cooldown, keyed "{serverId}:{StoryPathHash}".
        /// Seeded lazily from the alert log on first lookup per key so a finding
        /// that just fired and entered its cooldown stays suppressed across an
        /// app restart. Pruned on each notify cycle to entries within
        /// 2 × AnalysisNotifyCooldownMinutes.
        /// </summary>
        private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

        public AnalysisNotificationService(
            EmailAlertService emailAlertService,
            IAlertSettings settings,
            IServerManager serverManager)
        {
            _emailAlertService = emailAlertService;
            _settings = settings;
            _serverManager = serverManager;
        }

        /// <summary>
        /// Notifies on every finding at or above the configured severity that is not
        /// inside its re-notification cooldown. Never throws.
        /// </summary>
        public async Task NotifyAsync(IReadOnlyList<AnalysisFinding> findings)
        {
            if (findings is null || findings.Count == 0)
                return;

            // Bounds are enforced in the settings adapter (DashboardAlertSettings clamps
            // AnalysisNotifySeverity to [0, 2] and AnalysisNotifyCooldownMinutes to
            // [30, 10080]); the service consumes the already-clamped values.
            var threshold = _settings.AnalysisNotifySeverity;
            var cooldownMinutes = _settings.AnalysisNotifyCooldownMinutes;
            var cooldown = TimeSpan.FromMinutes(cooldownMinutes);
            var now = DateTime.UtcNow;

            // Round-2/3 review: webhook-flag-on-with-URL-absent silently drops the alert
            // (WebhookAlertService short-circuits at the URL check). Require both the flag
            // AND the URL to count a channel as "attempted", so the fallback RecordAlert
            // fires when no channel can actually send.
            //
            // Known asymmetry: emailWouldLog is a *configuration* check, not a *delivery*
            // check. If SMTP is configured but inside its EmailCooldownMinutes window for
            // (serverId, metricName), TrySendAlertEmailAsync silently skips RecordAlert.
            // With default cooldowns (15-minute SMTP, 360-minute analysis) this is hard
            // to hit — the analysis per-finding cooldown is the much longer of the two.
            // A user who lowers AnalysisNotifyCooldownMinutes below EmailCooldownMinutes
            // can lose alert-history rows during the SMTP cooldown window; accepted.
            var emailWouldLog =
                _settings.SmtpEnabled
                && !string.IsNullOrWhiteSpace(_settings.SmtpServer)
                && !string.IsNullOrWhiteSpace(_settings.SmtpFromAddress)
                && !string.IsNullOrWhiteSpace(_settings.SmtpRecipients);
            var webhooksAttempted =
                (_settings.TeamsWebhookEnabled && !string.IsNullOrWhiteSpace(_settings.TeamsWebhookUrl))
             || (_settings.SlackWebhookEnabled && !string.IsNullOrWhiteSpace(_settings.SlackWebhookUrl));

            /* Drop entries past 2× cooldown so the dict stays bounded — any entry
               past 1× is already re-fire-eligible, doubling gives clock-skew margin.
               If a key here also matches a finding in this batch, the per-finding
               seed below will re-add it from history; that's a wash, not a bug. */
            var pruneBefore = now - TimeSpan.FromTicks(cooldown.Ticks * 2);
            foreach (var stale in _cooldowns)
            {
                if (stale.Value < pruneBefore)
                    _cooldowns.TryRemove(stale.Key, out _);
            }

            foreach (var finding in findings)
            {
                if (finding.Severity < threshold)
                    continue;

                var key = $"{finding.ServerId}:{finding.StoryPathHash}";

                /* Use the matching ServerConnection.Id (GUID string) when we can find
                   it — keeps alert_history.json keys consistent with the threshold-alert
                   engine. Fall back to the finding's stable int id (as a string) if the
                   lookup misses (server removed between cycle start and notify).
                   Resolved up-front so the seed lookup below uses the same shape as
                   the EmailAlertService call. */
                var matchedServer = _serverManager.GetAllServers()
                    .FirstOrDefault(s => string.Equals(s.ServerName, finding.ServerName, StringComparison.OrdinalIgnoreCase));
                var serverId = matchedServer?.Id ?? finding.ServerId.ToString();
                var metricName = FindingMessageFormatter.MetricName(finding);

                /* Seed the in-memory cooldown from alert_history.json on first lookup
                   per key so an analysis finding that fired shortly before an app
                   restart is not re-fired afterward. No channel/error filter — the
                   cooldown is stamped unconditionally below, so the persisted equivalent
                   is the latest row for that metric_name regardless of channel. */
                if (!_cooldowns.ContainsKey(key))
                {
                    var lastPersisted = _emailAlertService.GetLastAlertTime(serverId, metricName);
                    if (lastPersisted.HasValue)
                    {
                        _cooldowns.TryAdd(key, lastPersisted.Value);
                    }
                }

                if (_cooldowns.TryGetValue(key, out var last) && now - last < cooldown)
                    continue;

                try
                {
                    var currentValue = FindingMessageFormatter.CurrentValue(finding);
                    var thresholdDisplay = threshold.ToString("F1");
                    var context = FindingMessageFormatter.BuildContext(finding, threshold);

                    /* TrySendAlertEmailAsync fans out to email + Slack + Teams and records
                       an alert log row for each channel that actually fired. Returns no
                       success/failure signal — cooldown is stamped regardless. */
                    await _emailAlertService.TrySendAlertEmailAsync(
                        metricName,
                        finding.ServerName,
                        currentValue,
                        thresholdDisplay,
                        serverId,
                        context);

                    /* Fallback: when neither channel is configured to log, EmailAlertService
                       never calls RecordAlert and the finding silently disappears from the
                       Alerts tab. Log it ourselves so the history is complete regardless of
                       SMTP/webhook configuration. */
                    if (!emailWouldLog && !webhooksAttempted)
                    {
                        _emailAlertService.RecordAlert(
                            serverId,
                            finding.ServerName,
                            metricName,
                            currentValue,
                            thresholdDisplay,
                            alertSent: false,
                            notificationType: "tray",
                            muted: false,
                            detailText: FindingMessageFormatter.PlainTextDiagnosis(finding, threshold),
                            contextJson: AlertContextSerializer.Serialize(context));
                    }

                    _cooldowns[key] = now;
                }
                catch (Exception ex)
                {
                    /* TrySendAlertEmailAsync is documented never to throw; this guards a
                       formatter defect so one bad finding cannot abort the rest. */
                    Logger.Error(
                        $"AnalysisNotificationService: failed to notify on finding {finding.StoryPathHash}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Composes the arguments for an analysis-finding notification. The engine never
    /// populates <see cref="AnalysisFinding.StoryText"/>, so the readable message is
    /// built here from the finding's structured fields and drill-down detail.
    ///
    /// <para>
    /// Static — Dashboard reads settings via <see cref="IUserPreferencesService"/>,
    /// not <c>App.*</c> statics like Lite, so <c>notifyThreshold</c> is threaded
    /// through as a method parameter rather than read inline.
    /// </para>
    /// </summary>
    internal static class FindingMessageFormatter
    {
        private const int FieldValueLimit = 300;

        /// <summary>
        /// Alert metric name. The "Analysis: " prefix groups these in the Alerts tab; the
        /// short hash suffix makes each distinct finding unique, so EmailAlertService's own
        /// {serverId}:{metricName} cooldown cannot collapse two findings sharing a Category.
        /// </summary>
        public static string MetricName(AnalysisFinding finding)
        {
            var hash = finding.StoryPathHash ?? string.Empty;
            var shortHash = hash.Length >= 8 ? hash[..8] : hash;
            var category = string.IsNullOrEmpty(finding.Category) ? "finding" : finding.Category;
            return $"Analysis: {category} [{shortHash}]";
        }

        /// <summary>
        /// Headline value — the root fact and its value, plus baseline context for anomaly findings.
        /// </summary>
        public static string CurrentValue(AnalysisFinding finding)
        {
            var root = string.IsNullOrEmpty(finding.RootFactKey) ? finding.Category : finding.RootFactKey;
            var sb = new StringBuilder(root);

            if (finding.RootFactValue.HasValue)
                sb.Append($" ({finding.RootFactValue.Value:F1})");

            if (finding.RootFactMetadata is { Count: > 0 })
            {
                var baseline = ToolRecommendations.FormatBaselineContext(finding.RootFactMetadata);
                if (baseline is { Count: > 0 })
                {
                    var parts = baseline.Select(kv => $"{Humanize(kv.Key)} {kv.Value}");
                    sb.Append(" — ").Append(string.Join(", ", parts));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Plain-text diagnosis block — used only as the <c>RecordAlert.detailText</c> payload
        /// when no notification channel is configured (so the Alerts history tab still shows
        /// a searchable summary). Multi-line to match the existing alert-detail TextBox shape.
        /// </summary>
        public static string PlainTextDiagnosis(AnalysisFinding finding, double notifyThreshold)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  Story: {finding.StoryPath}");
            sb.AppendLine($"  Severity: {finding.Severity:F2} (notify threshold {notifyThreshold:F1})");
            sb.AppendLine($"  Confidence: {finding.Confidence:F2}");
            sb.AppendLine($"  Facts in chain: {finding.FactCount}");

            if (!string.IsNullOrEmpty(finding.DatabaseName))
                sb.AppendLine($"  Database: {finding.DatabaseName}");

            if (finding.TimeRangeStart.HasValue && finding.TimeRangeEnd.HasValue)
                sb.AppendLine($"  Window: {finding.TimeRangeStart.Value:u} - {finding.TimeRangeEnd.Value:u}");

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Builds the structured <see cref="AlertContext"/> for the alert template.
        /// First detail item is the Diagnosis summary; subsequent items are the
        /// finding's drill-down detail flattened into label/value pairs.
        /// </summary>
        public static AlertContext BuildContext(AnalysisFinding finding, double notifyThreshold)
        {
            var context = new AlertContext();

            /* Diagnosis summary — fits inside the 600px email template (label column 120px, value column ~480px). */
            var diagnosis = new AlertDetailItem { Heading = "Diagnosis" };
            diagnosis.Fields.Add(("Story", finding.StoryPath ?? string.Empty));
            diagnosis.Fields.Add(("Severity", finding.Severity.ToString("F2")));
            diagnosis.Fields.Add(("Notify threshold", notifyThreshold.ToString("F1")));
            diagnosis.Fields.Add(("Confidence", finding.Confidence.ToString("F2")));
            diagnosis.Fields.Add(("Facts", finding.FactCount.ToString()));
            if (!string.IsNullOrEmpty(finding.DatabaseName))
                diagnosis.Fields.Add(("Database", finding.DatabaseName));
            if (finding.TimeRangeStart.HasValue && finding.TimeRangeEnd.HasValue)
                diagnosis.Fields.Add(("Window", $"{finding.TimeRangeStart.Value:u} → {finding.TimeRangeEnd.Value:u}"));
            context.Details.Add(diagnosis);

            /* Advice (Investigation/Remediation prose) + generated remediation T-SQL, when the
               shared analysis library has a block for this finding's root fact-key. Inserted
               after Diagnosis and before the drill-down so every surface renders the same order:
               Diagnosis → Advice → Remediation T-SQL → drill-down. */
            var advice = FactAdvice.GetForFinding(finding);
            if (advice is not null)
            {
                context.Details.Add(new AlertDetailItem
                {
                    Heading = advice.Headline,
                    Body = $"Investigation: {advice.Investigation}\n\nRemediation: {advice.Remediation}"
                });

                if (!string.IsNullOrEmpty(advice.RemediationTsql))
                {
                    context.Details.Add(new AlertDetailItem
                    {
                        Heading = "Remediation T-SQL",
                        Body = advice.RemediationTsql,
                        IsCodeBlock = true
                    });
                }
            }

            /* Drill-down values are anonymous types behind object (a bare object, or a
               List<object> of them). Round-trip through System.Text.Json and walk as
               JsonElement — robust to any shape DrillDownCollector emits. */
            if (finding.DrillDown is { Count: > 0 })
            {
                foreach (var (key, value) in finding.DrillDown)
                {
                    if (value is null)
                        continue;

                    var item = new AlertDetailItem { Heading = Humanize(key) };
                    try
                    {
                        FlattenInto(item.Fields, JsonSerializer.SerializeToElement(value));
                    }
                    catch
                    {
                        /* Unexpected value shape — skip this drill-down entry, keep the rest. */
                        continue;
                    }

                    if (item.Fields.Count > 0)
                        context.Details.Add(item);
                }
            }

            return context;
        }

        /// <summary>
        /// Flattens one drill-down value into label/value field pairs. Arrays are capped at
        /// the first 3 elements; nested objects/arrays are rendered as compact JSON.
        /// </summary>
        private static void FlattenInto(List<(string Label, string Value)> fields, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var child in element.EnumerateArray())
                    {
                        if (index >= 3)
                            break;
                        index++;

                        if (child.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in child.EnumerateObject())
                                fields.Add(($"#{index} {Humanize(prop.Name)}", ScalarText(prop.Value)));
                        }
                        else
                        {
                            fields.Add(($"#{index}", ScalarText(child)));
                        }
                    }
                    break;

                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                        fields.Add((Humanize(prop.Name), ScalarText(prop.Value)));
                    break;

                default:
                    fields.Add(("value", ScalarText(element)));
                    break;
            }
        }

        /// <summary>Renders a single JSON value as truncated display text.</summary>
        private static string ScalarText(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => Truncate(element.GetString() ?? string.Empty),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                // Nested object/array — show compact raw JSON rather than recursing further.
                _ => Truncate(element.GetRawText())
            };
        }

        /// <summary>Turns a snake_case key into spaced Title Case ("top_blocking_chains" → "Top Blocking Chains").</summary>
        private static string Humanize(string key)
        {
            if (string.IsNullOrEmpty(key))
                return key;

            var words = key.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        }

        private static string Truncate(string text)
        {
            return text.Length <= FieldValueLimit ? text : text[..FieldValueLimit] + "…";
        }
    }
}
