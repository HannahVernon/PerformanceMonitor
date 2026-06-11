/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Notifications;

/// <summary>
/// Single source of truth for per-metric alert severity styling: accent/hex color, badge text,
/// and the webhook emoji. Both the email body (<see cref="EmailTemplateBuilder"/>) and the
/// webhook payloads (<see cref="WebhookAlertService"/>) consume this so the two maps cannot
/// drift apart (adding a metric to one and forgetting the other was a real foot-gun).
/// </summary>
internal static class AlertSeverity
{
    /// <returns>
    /// (HexColor, BadgeText, Emoji) for the metric. Unknown metrics fall back to INFO-blue.
    /// Email ignores the emoji; webhooks use all three.
    /// </returns>
    public static (string HexColor, string BadgeText, string Emoji) ForMetric(string metricName) => metricName switch
    {
        "Blocking Detected" => ("#D97706", "ALERT", "\U0001F7E0"),
        "Deadlocks Detected" => ("#DC2626", "ALERT", "\U0001F534"),
        "High CPU" => ("#F59E0B", "WARNING", "\U0001F7E1"),
        "Poison Wait" => ("#DC2626", "CRITICAL", "\U0001F534"),
        "Long-Running Query" => ("#D97706", "WARNING", "\U0001F7E0"),
        "TempDB Space" => ("#D97706", "WARNING", "\U0001F7E0"),
        "Long-Running Job" => ("#D97706", "WARNING", "\U0001F7E0"),
        "Server Unreachable" => ("#DC2626", "CRITICAL", "\U0001F534"),
        "Server Restored" => ("#16A34A", "RESOLVED", "\U0001F7E2"),
        _ => ("#2eaef1", "INFO", "\U0001F535")
    };
}
