/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
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
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Analysis;

namespace PerformanceMonitor.Notifications;

/// <summary>
/// Routes high-severity analysis findings into the notification channels.
/// Filters by severity, dedups per finding (so a recurring finding does not
/// re-notify every analysis cycle), composes a readable message, and hands off
/// to an <see cref="IFindingAlertSender"/> — each app's <c>EmailAlertService</c> —
/// which fans out to email + Slack + Teams and records the alert per that app's
/// history cadence.
///
/// <para>
/// Shared between Lite and Dashboard (Plan E E3c). The per-app divergences are absorbed
/// by two injected dependencies: a <c>serverId</c> resolver (Lite uses the finding's int
/// id as a string; Dashboard resolves the matching <c>ServerConnection</c> GUID and falls
/// back to the int id) and the <see cref="IFindingAlertSender"/> (which owns the per-app
/// record cadence — Approach B, plan §4.6).
/// </para>
/// </summary>
public sealed class AnalysisNotificationService
{
    private readonly IFindingAlertSender _sender;
    private readonly IAlertSettings _settings;
    private readonly Func<AnalysisFinding, string> _resolveServerId;
    private readonly ILogger<AnalysisNotificationService> _logger;

    /// <summary>
    /// Per-finding re-notification cooldown, keyed "{serverId}:{StoryPathHash}".
    /// Seeded lazily from the alert log on first lookup per key so a finding
    /// that just fired and entered its cooldown stays suppressed across an
    /// app restart. Pruned on each notify cycle to entries within
    /// 2 × AnalysisNotifyCooldownMinutes.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

    /// <param name="sender">The per-app alert dispatcher (its <c>EmailAlertService</c>).</param>
    /// <param name="settings">Alert settings (severity threshold + cooldown; clamped per app).</param>
    /// <param name="serverIdResolver">
    /// Resolves the persistence <c>serverId</c> for a finding. Lite: <c>f =&gt; f.ServerId.ToString()</c>;
    /// Dashboard: the <c>IServerManager</c> GUID lookup with the int-id fallback.
    /// </param>
    /// <param name="logger">Diagnostic logger.</param>
    public AnalysisNotificationService(
        IFindingAlertSender sender,
        IAlertSettings settings,
        Func<AnalysisFinding, string> serverIdResolver,
        ILogger<AnalysisNotificationService> logger)
    {
        _sender = sender;
        _settings = settings;
        _resolveServerId = serverIdResolver;
        _logger = logger;
    }

    /// <summary>
    /// Notifies on every finding at or above the configured severity that is not
    /// inside its re-notification cooldown. Never throws.
    /// </summary>
    public async Task NotifyAsync(IReadOnlyList<AnalysisFinding> findings)
    {
        if (findings is null || findings.Count == 0)
            return;

        // Bounds are enforced in the settings adapter (Dashboard clamps AnalysisNotifySeverity
        // to [0, 2] and AnalysisNotifyCooldownMinutes to [30, 10080]; Lite passes through);
        // the service consumes the already-clamped values.
        var threshold = _settings.AnalysisNotifySeverity;
        var cooldown = TimeSpan.FromMinutes(_settings.AnalysisNotifyCooldownMinutes);
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

            /* Cooldown key uses the finding's stable int id (matches both apps' prior
               behaviour); the resolved serverId below is only the persistence shape. */
            var key = $"{finding.ServerId}:{finding.StoryPathHash}";
            var serverId = _resolveServerId(finding);
            var metricName = FindingMessageFormatter.MetricName(finding);

            /* Seed the in-memory cooldown from the alert log on first lookup per key so an
               analysis finding that fired shortly before an app restart is not re-fired
               afterward. No channel/error filter — the cooldown is stamped unconditionally
               below, so the persisted equivalent is the latest row for that metric_name. */
            if (!_cooldowns.ContainsKey(key))
            {
                var lastPersisted = await _sender.GetLastAlertTimeAsync(serverId, metricName);
                if (lastPersisted.HasValue)
                {
                    _cooldowns.TryAdd(key, lastPersisted.Value);
                }
            }

            if (_cooldowns.TryGetValue(key, out var last) && now - last < cooldown)
                continue;

            try
            {
                /* SendFindingAlertAsync fans out to email + Slack + Teams and records the
                   alert per this app's cadence. It returns no success/failure signal, so the
                   cooldown is stamped regardless — a finding whose delivery failed is
                   suppressed for the full cooldown (accepted best-effort behavior). */
                await _sender.SendFindingAlertAsync(new FindingAlert(
                    metricName,
                    finding.ServerName,
                    FindingMessageFormatter.CurrentValue(finding),
                    threshold.ToString("F1"),
                    serverId,
                    FindingMessageFormatter.BuildContext(finding, threshold),
                    finding.Severity,
                    threshold,
                    FindingMessageFormatter.DetailText(finding, threshold)));

                _cooldowns[key] = now;
            }
            catch (Exception ex)
            {
                /* SendFindingAlertAsync is documented never to throw; this guards a
                   formatter defect so one bad finding cannot abort the rest. */
                _logger.LogError(
                    $"AnalysisNotificationService: failed to notify on finding {finding.StoryPathHash}: {ex.GetType().Name}: {ex.Message}");
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
    /// short hash suffix makes each distinct finding unique, so the {serverId}:{metricName}
    /// cooldown cannot collapse two findings sharing a Category.
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
            var baseline = BaselineContextFormatter.FormatBaselineContext(finding.RootFactMetadata);
            if (baseline is { Count: > 0 })
            {
                var parts = baseline.Select(kv => $"{Humanize(kv.Key)} {kv.Value}");
                sb.Append(" — ").Append(string.Join(", ", parts));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Plain-text detail block — the causal chain and supporting metadata. Used as the
    /// alert's <c>detailText</c> (Lite persists it on every analysis row; Dashboard uses it
    /// on the no-channel "tray" fallback row). Threshold is threaded through as a parameter.
    /// </summary>
    public static string DetailText(AnalysisFinding finding, double notifyThreshold)
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
    /// First detail item is the Diagnosis summary; then the Advice/Remediation block
    /// (when the shared analysis library has one for the finding's root fact-key); then
    /// the finding's drill-down detail flattened into label/value pairs.
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
                    IsCodeBlock = true,
                    /* Structured, typed payload for an in-app Apply (PR-B). Rides in the
                       persisted contextJson; may be null (e.g. PARAMETER_SENSITIVITY has
                       advice + prose but no force action), in which case no Apply affordance
                       is offered. Built from the same drill-down the preview rendered. */
                    Remediation = FactRemediation.BuildAction(finding)
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
