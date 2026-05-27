/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorLite.Analysis;
using PerformanceMonitorLite.Mcp;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Routes high-severity analysis findings into the notification channels.
/// Filters by severity, dedups per finding (so a recurring finding does not
/// re-notify every analysis cycle), composes a readable message, and hands off
/// to <see cref="EmailAlertService"/> — which fans out to email + Slack + Teams
/// and logs to config_alert_log.
///
/// Called by the scheduled-analysis loop in CollectionBackgroundService with the
/// findings returned by each completed AnalysisService.AnalyzeAsync.
/// </summary>
public sealed class AnalysisNotificationService
{
    private readonly EmailAlertService _emailAlertService;

    /// <summary>
    /// Per-finding re-notification cooldown, keyed "{serverId}:{StoryPathHash}".
    /// Seeded lazily from the alert log on first lookup per key so a finding
    /// that just fired and entered its cooldown stays suppressed across an
    /// app restart. Pruned on each notify cycle to entries within
    /// 2 × AnalysisNotifyCooldownMinutes.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

    public AnalysisNotificationService(EmailAlertService emailAlertService)
    {
        _emailAlertService = emailAlertService;
    }

    /// <summary>
    /// Notifies on every finding at or above the configured severity that is not
    /// inside its re-notification cooldown. Never throws.
    /// </summary>
    public async Task NotifyAsync(IReadOnlyList<AnalysisFinding> findings)
    {
        if (findings is null || findings.Count == 0)
            return;

        var threshold = App.AnalysisNotifySeverity;
        var cooldown = TimeSpan.FromMinutes(App.AnalysisNotifyCooldownMinutes);
        var now = DateTime.UtcNow;

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

            /* Seed the in-memory cooldown from config_alert_log on first lookup
               per key so an analysis finding that fired shortly before an app
               restart is not re-fired afterward. Same lazy-seed pattern as
               EmailAlertService (#981), but with no channel/error filter — the
               cooldown is stamped unconditionally below. */
            if (!_cooldowns.ContainsKey(key))
            {
                var metricName = FindingMessageFormatter.MetricName(finding);
                var lastPersisted = await _emailAlertService.GetLastAlertTimeAsync(finding.ServerId, metricName);
                if (lastPersisted.HasValue)
                {
                    _cooldowns.TryAdd(key, lastPersisted.Value);
                }
            }

            if (_cooldowns.TryGetValue(key, out var last) && now - last < cooldown)
                continue;

            try
            {
                /* TrySendAlertEmailAsync fans out to email + Slack + Teams and logs a
                   config_alert_log row. It returns no success/failure signal, so the
                   cooldown is stamped regardless — a finding whose delivery failed is
                   suppressed for the full cooldown (accepted best-effort behavior). */
                await _emailAlertService.TrySendAlertEmailAsync(
                    FindingMessageFormatter.MetricName(finding),
                    finding.ServerName,
                    FindingMessageFormatter.CurrentValue(finding),
                    threshold.ToString("F1"),
                    finding.ServerId,
                    FindingMessageFormatter.BuildContext(finding),
                    numericCurrentValue: finding.Severity,
                    numericThresholdValue: threshold,
                    muted: false,
                    detailText: FindingMessageFormatter.DetailText(finding));

                _cooldowns[key] = now;
            }
            catch (Exception ex)
            {
                /* TrySendAlertEmailAsync is documented never to throw; this guards a
                   formatter defect so one bad finding cannot abort the rest. */
                AppLogger.Error("AnalysisNotify",
                    $"Failed to notify on finding {finding.StoryPathHash}: {ex.Message}", ex);
            }
        }
    }
}

/// <summary>
/// Composes the arguments for an analysis-finding notification. The engine never
/// populates <see cref="AnalysisFinding.StoryText"/>, so the readable message is
/// built here from the finding's structured fields and drill-down detail.
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
    /// Plain-text detail block — the causal chain and supporting metadata.
    /// </summary>
    public static string DetailText(AnalysisFinding finding)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  Story: {finding.StoryPath}");
        sb.AppendLine($"  Severity: {finding.Severity:F2} (notify threshold {App.AnalysisNotifySeverity:F1})");
        sb.AppendLine($"  Confidence: {finding.Confidence:F2}");
        sb.AppendLine($"  Facts in chain: {finding.FactCount}");

        if (!string.IsNullOrEmpty(finding.DatabaseName))
            sb.AppendLine($"  Database: {finding.DatabaseName}");

        if (finding.TimeRangeStart.HasValue && finding.TimeRangeEnd.HasValue)
            sb.AppendLine($"  Window: {finding.TimeRangeStart.Value:u} - {finding.TimeRangeEnd.Value:u}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Maps the finding's ephemeral <see cref="AnalysisFinding.DrillDown"/> detail into an
    /// <see cref="AlertContext"/>. DrillDown values are anonymous types behind object
    /// (a bare object, or a List&lt;object&gt; of them), so they are round-tripped through
    /// System.Text.Json and walked as JsonElement — robust to any shape DrillDownCollector emits.
    /// </summary>
    public static AlertContext BuildContext(AnalysisFinding finding)
    {
        var context = new AlertContext();
        if (finding.DrillDown is null || finding.DrillDown.Count == 0)
            return context;

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

    /// <summary>Turns a snake_case key into spaced Title Case ("top_blocking_chains" -> "Top Blocking Chains").</summary>
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
