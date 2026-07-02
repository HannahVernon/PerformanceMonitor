/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Darling's settings adapter for the Phase-5 shared alert engine: implements BOTH the engine
/// threshold surface (<see cref="IAlertEngineSettings"/>) and the delivery surface
/// (<see cref="IAlertSettings"/>, consumed by the shared <c>EmailSendCore</c>/
/// <c>WebhookAlertService</c>) over one <see cref="DarlingConfig"/> — the Darling twin of Lite's
/// <c>AppAlertSettings</c>. Pass-through reads (the config object is held by reference, so a
/// future config-reload feature is reflected immediately); the few clamps mirror the ones Lite
/// applies at settings-load time (cooldowns 1–120, failed-job lookback 1–1440, low-disk % 0–100,
/// low-disk GB ≥ 0). SMTP is "enabled" when host + from + to are all configured and a webhook
/// channel when its URL is set — no speculative enable flags.
/// </summary>
public sealed class DarlingAlertSettings : IAlertEngineSettings, IAlertSettings
{
    private readonly DarlingConfig _config;

    public DarlingAlertSettings(DarlingConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /* ---------------- IAlertEngineSettings (thresholds) ---------------- */

    public bool AlertsEnabled => _config.Alerts.Enabled;

    public bool CpuEnabled => _config.Alerts.CpuEnabled;
    public bool BlockingEnabled => _config.Alerts.BlockingEnabled;
    public bool DeadlockEnabled => _config.Alerts.DeadlockEnabled;
    public bool PoisonWaitEnabled => _config.Alerts.PoisonWaitEnabled;
    public bool LongRunningQueryEnabled => _config.Alerts.LongRunningQueryEnabled;
    public bool TempDbSpaceEnabled => _config.Alerts.TempDbSpaceEnabled;
    public bool LowDiskEnabled => _config.Alerts.LowDiskEnabled;
    public bool LongRunningJobEnabled => _config.Alerts.LongRunningJobEnabled;
    public bool FailedJobEnabled => _config.Alerts.FailedJobEnabled;

    public int CpuThresholdPercent => _config.Alerts.CpuThresholdPercent;
    public int BlockingCountThreshold => _config.Alerts.BlockingCountThreshold;
    public int DeadlockCountThreshold => _config.Alerts.DeadlockCountThreshold;
    public int PoisonWaitThresholdMs => _config.Alerts.PoisonWaitThresholdMs;
    public int LongRunningQueryThresholdMinutes => _config.Alerts.LongRunningQueryThresholdMinutes;
    public int TempDbSpaceThresholdPercent => _config.Alerts.TempDbSpaceThresholdPercent;
    public int LowDiskThresholdPercent => Math.Clamp(_config.Alerts.LowDiskThresholdPercent, 0, 100);
    public int LowDiskThresholdGb => Math.Max(0, _config.Alerts.LowDiskThresholdGb);
    public int LongRunningJobMultiplier => _config.Alerts.LongRunningJobMultiplier;
    public int FailedJobLookbackMinutes => Math.Clamp(_config.Alerts.FailedJobLookbackMinutes, 1, 1440);
    public int CooldownMinutes => Math.Clamp(_config.Alerts.CooldownMinutes, 1, 120);

    public IReadOnlyList<string> ExcludedDatabases => _config.Alerts.ExcludedDatabases;

    /// <summary>"sql" → SqlProcess; anything else (incl. Lite's default "total") → TotalServer.</summary>
    public CpuAlertMode CpuAlertMode =>
        string.Equals(_config.Alerts.CpuMode, "sql", StringComparison.OrdinalIgnoreCase)
            ? CpuAlertMode.SqlProcess
            : CpuAlertMode.TotalServer;

    /* The long-running-query read shape — Lite's App defaults hardcoded (defaults over
       speculative config): max 5 rows, all five noise filters on. */
    public int LongRunningQueryMaxResults => 5;
    public bool LongRunningQueryExcludeSpServerDiagnostics => true;
    public bool LongRunningQueryExcludeWaitFor => true;
    public bool LongRunningQueryExcludeBackups => true;
    public bool LongRunningQueryExcludeMiscWaits => true;
    public bool LongRunningQueryExcludeCdc => true;

    /* ---------------- IAlertSettings (delivery) ---------------- */

    public bool SmtpEnabled =>
        !string.IsNullOrWhiteSpace(_config.Smtp.Host)
        && !string.IsNullOrWhiteSpace(_config.Smtp.From)
        && !string.IsNullOrWhiteSpace(_config.Smtp.To);

    public string SmtpServer => _config.Smtp.Host;
    public int SmtpPort => _config.Smtp.Port;
    public bool SmtpUseSsl => _config.Smtp.UseSsl;
    public string SmtpUsername => _config.Smtp.Username ?? "";
    public string SmtpFromAddress => _config.Smtp.From;
    public string SmtpRecipients => _config.Smtp.To;

    /// <summary>
    /// DPAPI-decrypts smtp.encryptedPassword (the --encrypt-password pattern); null when unset.
    /// Called inside EmailSendCore's send try/catch, so a decrypt failure surfaces as that
    /// alert's send_error rather than killing the sweep.
    /// </summary>
    public string? GetSmtpPassword()
    {
        var blob = _config.Smtp.EncryptedPassword;
        if (string.IsNullOrWhiteSpace(blob))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("smtp.encryptedPassword requires Windows (DPAPI).");
        }

        return DarlingSecrets.Unprotect(blob);
    }

    public int EmailCooldownMinutes => Math.Clamp(_config.Smtp.EmailCooldownMinutes, 1, 120);

    public bool TeamsWebhookEnabled => !string.IsNullOrWhiteSpace(_config.Webhooks.TeamsUrl);
    public string TeamsWebhookUrl => _config.Webhooks.TeamsUrl;
    public string TeamsProxyAddress => _config.Webhooks.TeamsProxy;

    public bool SlackWebhookEnabled => !string.IsNullOrWhiteSpace(_config.Webhooks.SlackUrl);
    public string SlackWebhookUrl => _config.Webhooks.SlackUrl;
    public string SlackProxyAddress => _config.Webhooks.SlackProxy;

    /* Scheduled-analysis notifications (AN3): the shared AnalysisNotificationService's
       severity floor + per-finding re-notify cooldown — Lite's App defaults hardcoded
       (AnalysisNotifySeverity 1.5, AnalysisNotifyCooldownMinutes 360; defaults over
       speculative config). */
    public double AnalysisNotifySeverity => 1.5;
    public int AnalysisNotifyCooldownMinutes => 360;
}
