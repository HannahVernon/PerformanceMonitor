/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's full Settings window — a faithful, section-for-section port of Performance Monitor Lite's
/// <c>SettingsWindow</c> (Lite/Windows/SettingsWindow.xaml.cs), adapted DuckDB/in-process → Postgres/service
/// the same way the rest of the viewer was ported. Every section from Lite is present: Data Collection, MCP
/// server, viewer defaults (with the #1401 time-range + auto-refresh preferences folded in), the full
/// notifications block (alert thresholds, toggles, LRQ filters, cooldowns, delivery mode, global filters,
/// mute-rule default, automated analysis), SMTP email, Teams/Slack webhooks, and the collection schedule.
///
/// <para>
/// Persistence: the alert/notification/SMTP/webhook/MCP values are persisted in <see cref="ViewerAppSettings"/>
/// (the Darling equivalent of Lite's <c>App.*</c> + settings.json); secrets go to Windows Credential Manager
/// via <see cref="ViewerSecretStore"/>, exactly as Lite stores them. The folded-in viewer preferences round-trip
/// through <see cref="ViewerPreferences"/> (handed back to <see cref="MainWindow"/> via <see cref="Result"/>).
/// In Darling those alert/collection values are the SERVICE's domain (darling.json); the window persists them
/// faithfully so it is complete and its state survives a restart — the service honoring a change is a separate
/// wiring concern (see the PR body). The controls that act inside the viewer TODAY work immediately: Manage Mute
/// Rules (writes Postgres), Send Test Email / Send Test Notification (the shared, connection-independent
/// renderers), Validate Settings, and the viewer preferences.
/// </para>
///
/// <para>
/// Two Lite-only MECHANISMS are adapted sensibly (matching how "Pause Collection" is handled in the port):
/// the Data Collection Pause button can't reach the remote service, so it is shown disabled with a
/// service-managed status; and the per-server Collector Schedule editor (Lite's ScheduleManager +
/// CollectorScheduleEditorWindow) has no store model in Darling, so the section is an informational panel.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Branding the shared email/webhook renderers stamp on test messages (mirrors the service's).</summary>
    private static readonly AlertBranding s_branding = new("Performance Monitor Darling", null);

    private readonly ViewerAppSettingsStore _appSettingsStore;
    private readonly ViewerAppSettings _appSettings;
    private readonly ViewerDataService? _dataService;

    /// <summary>
    /// The edited viewer preferences (default time range + auto-refresh), populated on a successful Save so
    /// <see cref="MainWindow"/> can refresh its in-memory copy that seeds newly-opened server tabs. Null until
    /// Save succeeds (Close without saving leaves it null).
    /// </summary>
    public ViewerPreferences? Result { get; private set; }

    /// <param name="preferences">Current viewer preferences (the toolbar-seed defaults) to seed the controls.</param>
    /// <param name="appSettingsStore">The store this window loads from and saves the app settings to.</param>
    /// <param name="dataService">The store connection, for "Manage Mute Rules"; null when not connected yet (button disabled).</param>
    public SettingsWindow(ViewerPreferences preferences, ViewerAppSettingsStore appSettingsStore, ViewerDataService? dataService)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(appSettingsStore);

        InitializeComponent();

        _appSettingsStore = appSettingsStore;
        _appSettings = appSettingsStore.Load();
        _dataService = dataService;

        LoadViewerPreferences(preferences);
        UpdateCollectionStatus();
        LoadMcpSettings();
        UpdateMcpStatus();
        LoadConnectionTimeout();
        LoadCsvSeparator();
        LoadTimeDisplayMode();
        LoadAlertSettings();
        LoadSmtpSettings();
        LoadWebhookSettings();

        /* Manage Mute Rules writes the shared Postgres config; needs a live store connection. */
        ManageMuteRulesButton.IsEnabled = _dataService is not null;
    }

    // ── Viewer preferences (folded in from #1401: default time range + auto-refresh) ──

    private void LoadViewerPreferences(ViewerPreferences preferences)
    {
        /* Normalize so an out-of-range persisted index can never leave a combo unselected; the combos mirror
           the toolbar's item order, so the stored index selects the matching row directly. */
        var normalized = new ViewerPreferences
        {
            DefaultTimeRangeIndex = preferences.DefaultTimeRangeIndex,
            AutoRefreshEnabled = preferences.AutoRefreshEnabled,
            AutoRefreshIntervalIndex = preferences.AutoRefreshIntervalIndex,
        }.Normalize();

        DefaultTimeRangeCombo.SelectedIndex = normalized.DefaultTimeRangeIndex;
        AutoRefreshCheckBox.IsChecked = normalized.AutoRefreshEnabled;
        AutoRefreshIntervalCombo.SelectedIndex = normalized.AutoRefreshIntervalIndex;
        AutoRefreshIntervalCombo.IsEnabled = normalized.AutoRefreshEnabled;
    }

    private ViewerPreferences BuildViewerPreferences() => new ViewerPreferences
    {
        DefaultTimeRangeIndex = DefaultTimeRangeCombo.SelectedIndex,
        AutoRefreshEnabled = AutoRefreshCheckBox.IsChecked == true,
        AutoRefreshIntervalIndex = AutoRefreshIntervalCombo.SelectedIndex,
    }.Normalize();

    private void AutoRefreshCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        /* Guard the load-time raise before the combo exists (Checked can fire during InitializeComponent). */
        if (AutoRefreshIntervalCombo is not null)
        {
            AutoRefreshIntervalCombo.IsEnabled = AutoRefreshCheckBox.IsChecked == true;
        }
    }

    // ── Data Collection (adapted: collection runs in the remote service) ──

    private void UpdateCollectionStatus()
    {
        /* The viewer has no in-process collector to pause — collection runs in the Darling service, which the
           viewer cannot reach to pause. Present it honestly and disable the button (Lite disables it the same
           way when it has no background service). */
        CollectionStatusText.Text = "Status: Managed by the Darling service (the viewer cannot pause remote collection)";
        PauseResumeButton.IsEnabled = false;
    }

    private void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        /* No-op: collection runs in the remote service. The button is disabled; this handler only satisfies
           the XAML binding. */
    }

    // ── MCP server (the service hosts the MCP; the viewer persists the desired config) ──

    private void LoadMcpSettings()
    {
        McpEnabledCheckBox.IsChecked = _appSettings.McpEnabled;
        McpPortTextBox.Text = _appSettings.McpPort.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateMcpStatus()
    {
        McpStatusText.Text = McpEnabledCheckBox.IsChecked == true
            ? "The MCP server is hosted by the Darling service; restart the service to apply changes."
            : "Status: Disabled";
    }

    /// <summary>Applies the MCP fields to <see cref="_appSettings"/>. Returns false only when an ENABLED server has a bad port.</summary>
    private bool SaveMcpSettings()
    {
        _appSettings.McpEnabled = McpEnabledCheckBox.IsChecked == true;

        if (int.TryParse(McpPortTextBox.Text, out var port) && port is >= 1024 and <= 65535)
        {
            _appSettings.McpPort = port;
            return true;
        }

        if (_appSettings.McpEnabled)
        {
            MessageBox.Show(
                "MCP port must be between 1024 and 65535.\nPorts 0–1023 are well-known privileged ports reserved by the operating system.",
                "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void CopyMcpCommandButton_Click(object sender, RoutedEventArgs e)
    {
        var port = McpPortTextBox.Text;
        var command = $"claude mcp add --transport http --scope user sql-monitor-darling http://localhost:{port}/";
        /* SetDataObject with copy=false avoids WPF's problematic Clipboard.Flush(). */
        Clipboard.SetDataObject(command, false);
        McpStatusText.Text = "Copied to clipboard!";
    }

    private void AutoPortButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            McpPortTextBox.Text = FindFreeTcpPort().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not find an available port: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Asks the OS for a free loopback TCP port (bind to port 0, read the assignment, release).</summary>
    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── Viewer defaults (connection timeout, CSV separator, timestamp display) ──

    private void LoadConnectionTimeout() =>
        ConnectionTimeoutBox.Text = _appSettings.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

    private void SaveConnectionTimeout()
    {
        if (int.TryParse(ConnectionTimeoutBox.Text, out var timeout) && timeout is >= 5 and <= 60)
        {
            _appSettings.ConnectionTimeoutSeconds = timeout;
        }
    }

    private void LoadCsvSeparator()
    {
        foreach (ComboBoxItem item in CsvSeparatorCombo.Items)
        {
            if (item.Tag?.ToString() == _appSettings.CsvSeparator)
            {
                CsvSeparatorCombo.SelectedItem = item;
                break;
            }
        }
        if (CsvSeparatorCombo.SelectedItem == null)
        {
            CsvSeparatorCombo.SelectedIndex = 0;
        }
    }

    private void SaveCsvSeparator()
    {
        if (CsvSeparatorCombo.SelectedItem is ComboBoxItem { Tag: string sep })
        {
            _appSettings.CsvSeparator = sep;
        }
    }

    private void LoadTimeDisplayMode()
    {
        foreach (ComboBoxItem item in TimeDisplayModeCombo.Items)
        {
            if (item.Tag?.ToString() == _appSettings.TimeDisplayMode)
            {
                TimeDisplayModeCombo.SelectedItem = item;
                break;
            }
        }
        if (TimeDisplayModeCombo.SelectedItem == null)
        {
            TimeDisplayModeCombo.SelectedIndex = 0;
        }
    }

    private void SaveTimeDisplayMode()
    {
        if (TimeDisplayModeCombo.SelectedItem is ComboBoxItem { Tag: string mode })
        {
            _appSettings.TimeDisplayMode = mode;
        }
    }

    // ── Notifications / alert thresholds ──

    private void LoadAlertSettings()
    {
        MinimizeToTrayCheckBox.IsChecked = _appSettings.MinimizeToTray;
        AlertsEnabledCheckBox.IsChecked = _appSettings.AlertsEnabled;
        NotifyConnectionCheckBox.IsChecked = _appSettings.NotifyConnectionChanges;
        AlertCpuCheckBox.IsChecked = _appSettings.AlertCpuEnabled;
        AlertCpuThresholdBox.Text = _appSettings.AlertCpuThreshold.ToString(CultureInfo.InvariantCulture);
        AlertCpuModeBox.SelectedIndex = _appSettings.AlertCpuMode == "SqlOnly" ? 1 : 0;
        AlertBlockingCheckBox.IsChecked = _appSettings.AlertBlockingEnabled;
        AlertBlockingThresholdBox.Text = _appSettings.AlertBlockingThreshold.ToString(CultureInfo.InvariantCulture);
        AlertDeadlockCheckBox.IsChecked = _appSettings.AlertDeadlockEnabled;
        AlertDeadlockThresholdBox.Text = _appSettings.AlertDeadlockThreshold.ToString(CultureInfo.InvariantCulture);
        AlertPoisonWaitCheckBox.IsChecked = _appSettings.AlertPoisonWaitEnabled;
        AlertPoisonWaitThresholdBox.Text = _appSettings.AlertPoisonWaitThresholdMs.ToString(CultureInfo.InvariantCulture);
        AlertLongRunningQueryCheckBox.IsChecked = _appSettings.AlertLongRunningQueryEnabled;
        AlertLongRunningQueryThresholdBox.Text = _appSettings.AlertLongRunningQueryThresholdMinutes.ToString(CultureInfo.InvariantCulture);
        AlertLongRunningQueryMaxResultsBox.Text = _appSettings.AlertLongRunningQueryMaxResults.ToString(CultureInfo.InvariantCulture);
        LrqExcludeSpServerDiagnosticsCheckBox.IsChecked = _appSettings.AlertLongRunningQueryExcludeSpServerDiagnostics;
        LrqExcludeWaitForCheckBox.IsChecked = _appSettings.AlertLongRunningQueryExcludeWaitFor;
        LrqExcludeBackupsCheckBox.IsChecked = _appSettings.AlertLongRunningQueryExcludeBackups;
        LrqExcludeMiscWaitsCheckBox.IsChecked = _appSettings.AlertLongRunningQueryExcludeMiscWaits;
        LrqExcludeCdcCheckBox.IsChecked = _appSettings.AlertLongRunningQueryExcludeCdc;
        AlertExcludedDatabasesBox.Text = string.Join(", ", _appSettings.AlertExcludedDatabases);
        AlertTempDbSpaceCheckBox.IsChecked = _appSettings.AlertTempDbSpaceEnabled;
        AlertTempDbSpaceThresholdBox.Text = _appSettings.AlertTempDbSpaceThresholdPercent.ToString(CultureInfo.InvariantCulture);
        AlertLowDiskCheckBox.IsChecked = _appSettings.AlertLowDiskEnabled;
        AlertLowDiskThresholdPercentBox.Text = _appSettings.AlertLowDiskThresholdPercent.ToString(CultureInfo.InvariantCulture);
        AlertLowDiskThresholdGbBox.Text = _appSettings.AlertLowDiskThresholdGb.ToString(CultureInfo.InvariantCulture);
        AlertLongRunningJobCheckBox.IsChecked = _appSettings.AlertLongRunningJobEnabled;
        AlertLongRunningJobMultiplierBox.Text = _appSettings.AlertLongRunningJobMultiplier.ToString(CultureInfo.InvariantCulture);
        AlertFailedJobCheckBox.IsChecked = _appSettings.AlertFailedJobEnabled;
        AlertFailedJobLookbackBox.Text = _appSettings.AlertFailedJobLookbackMinutes.ToString(CultureInfo.InvariantCulture);
        AlertCooldownBox.Text = _appSettings.AlertCooldownMinutes.ToString(CultureInfo.InvariantCulture);
        EmailCooldownBox.Text = _appSettings.EmailCooldownMinutes.ToString(CultureInfo.InvariantCulture);
        AlertDeliveryModeBox.SelectedIndex = _appSettings.AlertDeliveryMode == "PerEvent" ? 1 : 0;
        AlertPerEventMaxBox.Text = _appSettings.AlertPerEventMaxPerCycle.ToString(CultureInfo.InvariantCulture);
        MuteRuleDefaultExpirationCombo.SelectedIndex = _appSettings.MuteRuleDefaultExpiration switch
        {
            "1 hour" => 0,
            "24 hours" => 1,
            "7 days" => 2,
            _ => 3
        };
        LogAlertDismissalsCheckBox.IsChecked = _appSettings.LogAlertDismissals;
        AnalysisEnabledCheckBox.IsChecked = _appSettings.AnalysisEnabled;
        AnalysisNotificationsCheckBox.IsChecked = _appSettings.AnalysisNotificationsEnabled;
        AnalysisIntervalBox.Text = _appSettings.AnalysisIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        AnalysisNotifySeverityBox.Text = _appSettings.AnalysisNotifySeverity.ToString("0.0", CultureInfo.InvariantCulture);
        UpdateAlertControlStates();
    }

    private bool SaveAlertSettings()
    {
        _appSettings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        _appSettings.AlertsEnabled = AlertsEnabledCheckBox.IsChecked == true;
        _appSettings.NotifyConnectionChanges = NotifyConnectionCheckBox.IsChecked == true;
        _appSettings.AlertCpuEnabled = AlertCpuCheckBox.IsChecked == true;
        if (int.TryParse(AlertCpuThresholdBox.Text, out var cpu) && cpu is > 0 and <= 100)
            _appSettings.AlertCpuThreshold = cpu;
        _appSettings.AlertCpuMode = AlertCpuModeBox.SelectedIndex == 1 ? "SqlOnly" : "Total";
        _appSettings.AlertBlockingEnabled = AlertBlockingCheckBox.IsChecked == true;
        if (int.TryParse(AlertBlockingThresholdBox.Text, out var blocking) && blocking > 0)
            _appSettings.AlertBlockingThreshold = blocking;
        _appSettings.AlertDeadlockEnabled = AlertDeadlockCheckBox.IsChecked == true;
        if (int.TryParse(AlertDeadlockThresholdBox.Text, out var deadlock) && deadlock > 0)
            _appSettings.AlertDeadlockThreshold = deadlock;
        _appSettings.AlertPoisonWaitEnabled = AlertPoisonWaitCheckBox.IsChecked == true;
        if (int.TryParse(AlertPoisonWaitThresholdBox.Text, out var poisonWait) && poisonWait > 0)
            _appSettings.AlertPoisonWaitThresholdMs = poisonWait;
        _appSettings.AlertLongRunningQueryEnabled = AlertLongRunningQueryCheckBox.IsChecked == true;
        if (int.TryParse(AlertLongRunningQueryThresholdBox.Text, out var lrq) && lrq > 0)
            _appSettings.AlertLongRunningQueryThresholdMinutes = lrq;
        if (int.TryParse(AlertLongRunningQueryMaxResultsBox.Text, out var lrqMax) && lrqMax >= 1)
            _appSettings.AlertLongRunningQueryMaxResults = lrqMax;
        _appSettings.AlertLongRunningQueryExcludeSpServerDiagnostics = LrqExcludeSpServerDiagnosticsCheckBox.IsChecked == true;
        _appSettings.AlertLongRunningQueryExcludeWaitFor = LrqExcludeWaitForCheckBox.IsChecked == true;
        _appSettings.AlertLongRunningQueryExcludeBackups = LrqExcludeBackupsCheckBox.IsChecked == true;
        _appSettings.AlertLongRunningQueryExcludeMiscWaits = LrqExcludeMiscWaitsCheckBox.IsChecked == true;
        _appSettings.AlertLongRunningQueryExcludeCdc = LrqExcludeCdcCheckBox.IsChecked == true;
        _appSettings.AlertExcludedDatabases = AlertExcludedDatabasesBox.Text
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        _appSettings.AlertTempDbSpaceEnabled = AlertTempDbSpaceCheckBox.IsChecked == true;
        if (int.TryParse(AlertTempDbSpaceThresholdBox.Text, out var tempDb) && tempDb is > 0 and <= 100)
            _appSettings.AlertTempDbSpaceThresholdPercent = tempDb;
        _appSettings.AlertLowDiskEnabled = AlertLowDiskCheckBox.IsChecked == true;
        if (int.TryParse(AlertLowDiskThresholdPercentBox.Text, out var lowDiskPct) && lowDiskPct is >= 0 and <= 100)
            _appSettings.AlertLowDiskThresholdPercent = lowDiskPct;
        if (int.TryParse(AlertLowDiskThresholdGbBox.Text, out var lowDiskGb) && lowDiskGb >= 0)
            _appSettings.AlertLowDiskThresholdGb = lowDiskGb;
        _appSettings.AlertLongRunningJobEnabled = AlertLongRunningJobCheckBox.IsChecked == true;
        if (int.TryParse(AlertLongRunningJobMultiplierBox.Text, out var jobMult) && jobMult is >= 2 and <= 20)
            _appSettings.AlertLongRunningJobMultiplier = jobMult;
        _appSettings.AlertFailedJobEnabled = AlertFailedJobCheckBox.IsChecked == true;
        if (int.TryParse(AlertFailedJobLookbackBox.Text, out var failedJobLookback) && failedJobLookback is >= 1 and <= 1440)
            _appSettings.AlertFailedJobLookbackMinutes = failedJobLookback;

        var validationErrors = new List<string>();
        if (int.TryParse(AlertCooldownBox.Text, out var alertCooldown) && alertCooldown is >= 1 and <= 120)
            _appSettings.AlertCooldownMinutes = alertCooldown;
        else
            validationErrors.Add("Tray notification cooldown must be between 1 and 120 minutes.");
        if (int.TryParse(EmailCooldownBox.Text, out var emailCooldown) && emailCooldown is >= 1 and <= 120)
            _appSettings.EmailCooldownMinutes = emailCooldown;
        else
            validationErrors.Add("Email alert cooldown must be between 1 and 120 minutes.");
        _appSettings.AlertDeliveryMode = AlertDeliveryModeBox.SelectedIndex == 1 ? "PerEvent" : "Summary";
        if (int.TryParse(AlertPerEventMaxBox.Text, out var perEventMax) && perEventMax is >= 1 and <= 100)
            _appSettings.AlertPerEventMaxPerCycle = perEventMax;
        else
            validationErrors.Add("Per-event max-per-cycle must be between 1 and 100.");
        _appSettings.MuteRuleDefaultExpiration = (MuteRuleDefaultExpirationCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "24 hours";
        _appSettings.LogAlertDismissals = LogAlertDismissalsCheckBox.IsChecked == true;
        _appSettings.AnalysisEnabled = AnalysisEnabledCheckBox.IsChecked == true;
        _appSettings.AnalysisNotificationsEnabled = AnalysisNotificationsCheckBox.IsChecked == true;
        if (int.TryParse(AnalysisIntervalBox.Text, out var analysisInterval) && analysisInterval is >= 5 and <= 360)
            _appSettings.AnalysisIntervalMinutes = analysisInterval;
        else
            validationErrors.Add("Analysis interval must be between 5 and 360 minutes.");
        if (double.TryParse(AnalysisNotifySeverityBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var analysisSeverity)
            && analysisSeverity is >= 0.0 and <= 2.0)
            _appSettings.AnalysisNotifySeverity = analysisSeverity;
        else
            validationErrors.Add("Analysis notify severity must be between 0.0 and 2.0.");

        if (validationErrors.Count > 0)
        {
            MessageBox.Show(
                "Some alert settings have invalid values and were not changed:\n\n" +
                string.Join("\n", validationErrors),
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void AlertsEnabledCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateAlertControlStates();

    private void RestoreAlertDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        AlertCpuThresholdBox.Text = "80";
        AlertCpuModeBox.SelectedIndex = 0; // Total
        AlertBlockingThresholdBox.Text = "1";
        AlertDeadlockThresholdBox.Text = "1";
        AlertPoisonWaitThresholdBox.Text = "500";
        AlertLongRunningQueryThresholdBox.Text = "30";
        AlertTempDbSpaceThresholdBox.Text = "80";
        AlertLowDiskThresholdPercentBox.Text = "10";
        AlertLowDiskThresholdGbBox.Text = "5";
        AlertLongRunningJobMultiplierBox.Text = "3";
        AlertFailedJobLookbackBox.Text = "60";
        AlertCooldownBox.Text = "5";
        EmailCooldownBox.Text = "15";
        AlertDeliveryModeBox.SelectedIndex = 0;
        AlertPerEventMaxBox.Text = "10";
        AnalysisIntervalBox.Text = "30";
        AnalysisNotifySeverityBox.Text = "1.5";
        AlertExcludedDatabasesBox.Text = "";
        MuteRuleDefaultExpirationCombo.SelectedIndex = 1; // 24 hours
        UpdateAlertPreviewText();
    }

    private void ManageMuteRulesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataService is null)
        {
            return;
        }

        var window = new MuteRulesWindow(_dataService) { Owner = this };
        window.ShowDialog();
    }

    private void UpdateAlertPreviewText()
    {
        var parts = new List<string>();

        if (AlertCpuCheckBox.IsChecked == true)
        {
            var cpuLabel = AlertCpuModeBox.SelectedIndex == 1 ? "SQL CPU" : "Total CPU";
            parts.Add($"{cpuLabel} > {AlertCpuThresholdBox.Text}%");
        }
        if (AlertBlockingCheckBox.IsChecked == true)
            parts.Add($"blocking >= {AlertBlockingThresholdBox.Text}");
        if (AlertDeadlockCheckBox.IsChecked == true)
            parts.Add($"deadlocks >= {AlertDeadlockThresholdBox.Text}");
        if (AlertPoisonWaitCheckBox.IsChecked == true)
            parts.Add($"poison waits >= {AlertPoisonWaitThresholdBox.Text}ms avg");
        if (AlertLongRunningQueryCheckBox.IsChecked == true)
            parts.Add($"queries > {AlertLongRunningQueryThresholdBox.Text}min");
        if (AlertTempDbSpaceCheckBox.IsChecked == true)
            parts.Add($"tempdb > {AlertTempDbSpaceThresholdBox.Text}%");
        if (AlertLowDiskCheckBox.IsChecked == true)
            parts.Add($"disk free < {AlertLowDiskThresholdPercentBox.Text}% or {AlertLowDiskThresholdGbBox.Text}GB");
        if (AlertLongRunningJobCheckBox.IsChecked == true)
            parts.Add($"jobs > {AlertLongRunningJobMultiplierBox.Text}x avg");
        if (AlertFailedJobCheckBox.IsChecked == true)
            parts.Add($"failed jobs (last {AlertFailedJobLookbackBox.Text}m)");

        AlertPreviewText.Text = parts.Count > 0
            ? $"Will alert when: {string.Join(", ", parts)}"
            : "No alerts enabled";
    }

    private void UpdateAlertControlStates()
    {
        var enabled = AlertsEnabledCheckBox.IsChecked == true;
        NotifyConnectionCheckBox.IsEnabled = enabled;
        AlertCpuCheckBox.IsEnabled = enabled;
        AlertCpuThresholdBox.IsEnabled = enabled;
        AlertCpuModeBox.IsEnabled = enabled;
        AlertBlockingCheckBox.IsEnabled = enabled;
        AlertBlockingThresholdBox.IsEnabled = enabled;
        AlertDeadlockCheckBox.IsEnabled = enabled;
        AlertDeadlockThresholdBox.IsEnabled = enabled;
        AlertPoisonWaitCheckBox.IsEnabled = enabled;
        AlertPoisonWaitThresholdBox.IsEnabled = enabled;
        AlertLongRunningQueryCheckBox.IsEnabled = enabled;
        AlertLongRunningQueryThresholdBox.IsEnabled = enabled;
        AlertTempDbSpaceCheckBox.IsEnabled = enabled;
        AlertTempDbSpaceThresholdBox.IsEnabled = enabled;
        AlertLowDiskCheckBox.IsEnabled = enabled;
        AlertLowDiskThresholdPercentBox.IsEnabled = enabled;
        AlertLowDiskThresholdGbBox.IsEnabled = enabled;
        AlertLongRunningJobCheckBox.IsEnabled = enabled;
        AlertLongRunningJobMultiplierBox.IsEnabled = enabled;
        AlertFailedJobCheckBox.IsEnabled = enabled;
        AlertFailedJobLookbackBox.IsEnabled = enabled;
        UpdateAlertPreviewText();
    }

    // ── SMTP email ──

    private void LoadSmtpSettings()
    {
        SmtpEnabledCheckBox.IsChecked = _appSettings.SmtpEnabled;
        SmtpServerBox.Text = _appSettings.SmtpServer;
        SmtpPortBox.Text = _appSettings.SmtpPort.ToString(CultureInfo.InvariantCulture);
        SmtpSslCheckBox.IsChecked = _appSettings.SmtpUseSsl;
        SmtpUsernameBox.Text = _appSettings.SmtpUsername;
        SmtpFromBox.Text = _appSettings.SmtpFromAddress;
        SmtpRecipientsBox.Text = _appSettings.SmtpRecipients;

        var password = ViewerSecretStore.GetSmtpPassword();
        if (!string.IsNullOrEmpty(password))
        {
            SmtpPasswordBox.Password = password;
        }

        UpdateSmtpControlStates();
    }

    private void SaveSmtpSettings()
    {
        _appSettings.SmtpEnabled = SmtpEnabledCheckBox.IsChecked == true;
        _appSettings.SmtpServer = SmtpServerBox.Text?.Trim() ?? "";
        if (int.TryParse(SmtpPortBox.Text, out var port) && port is > 0 and < 65536)
            _appSettings.SmtpPort = port;
        _appSettings.SmtpUseSsl = SmtpSslCheckBox.IsChecked == true;
        _appSettings.SmtpUsername = SmtpUsernameBox.Text?.Trim() ?? "";
        _appSettings.SmtpFromAddress = SmtpFromBox.Text?.Trim() ?? "";
        _appSettings.SmtpRecipients = SmtpRecipientsBox.Text?.Trim() ?? "";

        /* Password goes to Windows Credential Manager, never the JSON settings file. */
        if (!string.IsNullOrEmpty(SmtpPasswordBox.Password))
        {
            ViewerSecretStore.SaveSmtpPassword(_appSettings.SmtpUsername, SmtpPasswordBox.Password);
        }
    }

    private void SmtpEnabledCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateSmtpControlStates();

    private void UpdateSmtpControlStates()
    {
        var enabled = SmtpEnabledCheckBox.IsChecked == true;
        SmtpServerBox.IsEnabled = enabled;
        SmtpPortBox.IsEnabled = enabled;
        SmtpSslCheckBox.IsEnabled = enabled;
        SmtpUsernameBox.IsEnabled = enabled;
        SmtpPasswordBox.IsEnabled = enabled;
        SmtpFromBox.IsEnabled = enabled;
        SmtpRecipientsBox.IsEnabled = enabled;
        TestEmailButton.IsEnabled = enabled;
        ValidateSmtpButton.IsEnabled = enabled;
    }

    private void ValidateSmtpButton_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(SmtpServerBox.Text))
            errors.Add("SMTP server is required");
        if (!int.TryParse(SmtpPortBox.Text, out var port) || port is < 1 or > 65535)
            errors.Add("Port must be between 1 and 65535");
        if (string.IsNullOrWhiteSpace(SmtpFromBox.Text))
            errors.Add("From address is required");
        else if (!SmtpFromBox.Text.Trim().Contains('@'))
            errors.Add("From address must be a valid email");
        if (string.IsNullOrWhiteSpace(SmtpRecipientsBox.Text))
            errors.Add("At least one recipient is required");

        if (errors.Count == 0)
        {
            SmtpStatusText.Text = "Settings look good. Use 'Send Test Email' to verify delivery.";
        }
        else
        {
            SmtpStatusText.Text = "";
            MessageBox.Show(
                "SMTP configuration has issues:\n\n" + string.Join("\n", errors),
                "SMTP Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TestEmailButton_Click(object sender, RoutedEventArgs e)
    {
        TestEmailButton.IsEnabled = false;
        TestEmailButton.Content = "Sending...";

        try
        {
            /* Build the test settings straight from the live UI (test before save), so the user verifies
               exactly what they typed. The shared EmailSendCore renders + sends — no store/service needed. */
            var settings = TestAlertSettings.FromUi(this);
            var error = await EmailSendCore.SendTestEmailAsync(settings, s_branding);
            if (error == null)
            {
                MessageBox.Show("Test email sent successfully!", "Test Email", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Failed to send test email:\n\n{error}", "Test Email Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            TestEmailButton.Content = "Send Test Email";
            TestEmailButton.IsEnabled = true;
        }
    }

    // ── Webhooks (Teams / Slack) ──

    private void LoadWebhookSettings()
    {
        TeamsWebhookEnabledCheckBox.IsChecked = _appSettings.TeamsWebhookEnabled;
        TeamsWebhookUrlBox.Text = ViewerSecretStore.GetTeamsWebhookUrl();
        TeamsProxyAddressBox.Text = _appSettings.TeamsProxyAddress;
        SlackWebhookEnabledCheckBox.IsChecked = _appSettings.SlackWebhookEnabled;
        SlackWebhookUrlBox.Text = ViewerSecretStore.GetSlackWebhookUrl();
        SlackProxyAddressBox.Text = _appSettings.SlackProxyAddress;
        UpdateTeamsControlStates();
        UpdateSlackControlStates();
    }

    private void SaveWebhookSettings()
    {
        _appSettings.TeamsWebhookEnabled = TeamsWebhookEnabledCheckBox.IsChecked == true;
        _appSettings.TeamsProxyAddress = TeamsProxyAddressBox.Text?.Trim() ?? "";
        _appSettings.SlackWebhookEnabled = SlackWebhookEnabledCheckBox.IsChecked == true;
        _appSettings.SlackProxyAddress = SlackProxyAddressBox.Text?.Trim() ?? "";

        /* Webhook URLs go to Windows Credential Manager, never the JSON settings file. */
        ViewerSecretStore.SaveTeamsWebhookUrl(TeamsWebhookUrlBox.Text?.Trim() ?? "");
        ViewerSecretStore.SaveSlackWebhookUrl(SlackWebhookUrlBox.Text?.Trim() ?? "");
    }

    private void TeamsWebhookEnabledCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateTeamsControlStates();

    private void SlackWebhookEnabledCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateSlackControlStates();

    private void UpdateTeamsControlStates()
    {
        var enabled = TeamsWebhookEnabledCheckBox.IsChecked == true;
        TeamsWebhookUrlBox.IsEnabled = enabled;
        TeamsProxyAddressBox.IsEnabled = enabled;
        TestTeamsButton.IsEnabled = enabled;
    }

    private void UpdateSlackControlStates()
    {
        var enabled = SlackWebhookEnabledCheckBox.IsChecked == true;
        SlackWebhookUrlBox.IsEnabled = enabled;
        SlackProxyAddressBox.IsEnabled = enabled;
        TestSlackButton.IsEnabled = enabled;
    }

    private async void TestTeamsButton_Click(object sender, RoutedEventArgs e)
    {
        TestTeamsButton.IsEnabled = false;
        TestTeamsButton.Content = "Sending...";

        try
        {
            var url = TeamsWebhookUrlBox.Text?.Trim() ?? "";
            var proxy = TeamsProxyAddressBox.Text?.Trim();
            var error = await WebhookAlertService.SendTestTeamsAsync(url, proxy, s_branding);

            if (error == null)
            {
                MessageBox.Show("Teams test notification sent successfully!", "Test Webhook", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Failed to send Teams test notification:\n\n{error}", "Test Webhook Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to send Teams test notification:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestTeamsButton.Content = "Send Test Notification";
            TestTeamsButton.IsEnabled = true;
        }
    }

    private async void TestSlackButton_Click(object sender, RoutedEventArgs e)
    {
        TestSlackButton.IsEnabled = false;
        TestSlackButton.Content = "Sending...";

        try
        {
            var url = SlackWebhookUrlBox.Text?.Trim() ?? "";
            var proxy = SlackProxyAddressBox.Text?.Trim();
            var error = await WebhookAlertService.SendTestSlackAsync(url, proxy, s_branding);

            if (error == null)
            {
                MessageBox.Show("Slack test notification sent successfully!", "Test Webhook", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Failed to send Slack test notification:\n\n{error}", "Test Webhook Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to send Slack test notification:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestSlackButton.Content = "Send Test Notification";
            TestSlackButton.IsEnabled = true;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        }
        catch { /* A missing default browser must not crash the settings window. */ }
        e.Handled = true;
    }

    // ── Save / Close ──

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var mcpValid = SaveMcpSettings();
        SaveConnectionTimeout();
        SaveCsvSeparator();
        SaveTimeDisplayMode();
        var alertsValid = SaveAlertSettings();
        SaveSmtpSettings();
        SaveWebhookSettings();

        /* Persist the app settings (valid values were applied above; invalid ones were skipped and warned
           about) and capture the edited viewer preferences for MainWindow to save + re-seed tabs from. */
        _appSettingsStore.Save(_appSettings);
        Result = BuildViewerPreferences();

        /* Leave the window open on a validation warning so the user can fix the flagged value; otherwise
           the close IS the confirmation (mirrors the viewer's other dialogs). */
        if (!alertsValid || !mcpValid)
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A throwaway <see cref="IAlertSettings"/> built from the live SMTP/webhook controls, so "Send Test
    /// Email" verifies exactly what the user typed without first saving (Lite's "test before save"). Only the
    /// SMTP members matter for the email test; the rest satisfy the interface with the current UI values.
    /// </summary>
    private sealed class TestAlertSettings : IAlertSettings
    {
        public bool SmtpEnabled { get; private init; }
        public string SmtpServer { get; private init; } = "";
        public int SmtpPort { get; private init; }
        public bool SmtpUseSsl { get; private init; }
        public string SmtpUsername { get; private init; } = "";
        public string SmtpFromAddress { get; private init; } = "";
        public string SmtpRecipients { get; private init; } = "";
        private string SmtpPassword { get; init; } = "";
        public string? GetSmtpPassword() => string.IsNullOrEmpty(SmtpPassword) ? null : SmtpPassword;

        public int EmailCooldownMinutes { get; private init; }

        public bool TeamsWebhookEnabled { get; private init; }
        public string TeamsWebhookUrl { get; private init; } = "";
        public string TeamsProxyAddress { get; private init; } = "";

        public bool SlackWebhookEnabled { get; private init; }
        public string SlackWebhookUrl { get; private init; } = "";
        public string SlackProxyAddress { get; private init; } = "";

        public double AnalysisNotifySeverity { get; private init; }
        public int AnalysisNotifyCooldownMinutes { get; private init; }

        public static TestAlertSettings FromUi(SettingsWindow w)
        {
            int.TryParse(w.SmtpPortBox.Text, out var smtpPort);
            return new TestAlertSettings
            {
                SmtpEnabled = w.SmtpEnabledCheckBox.IsChecked == true,
                SmtpServer = w.SmtpServerBox.Text?.Trim() ?? "",
                SmtpPort = smtpPort,
                SmtpUseSsl = w.SmtpSslCheckBox.IsChecked == true,
                SmtpUsername = w.SmtpUsernameBox.Text?.Trim() ?? "",
                SmtpFromAddress = w.SmtpFromBox.Text?.Trim() ?? "",
                SmtpRecipients = w.SmtpRecipientsBox.Text?.Trim() ?? "",
                SmtpPassword = w.SmtpPasswordBox.Password,
                EmailCooldownMinutes = w._appSettings.EmailCooldownMinutes,
                TeamsWebhookEnabled = w.TeamsWebhookEnabledCheckBox.IsChecked == true,
                TeamsWebhookUrl = w.TeamsWebhookUrlBox.Text?.Trim() ?? "",
                TeamsProxyAddress = w.TeamsProxyAddressBox.Text?.Trim() ?? "",
                SlackWebhookEnabled = w.SlackWebhookEnabledCheckBox.IsChecked == true,
                SlackWebhookUrl = w.SlackWebhookUrlBox.Text?.Trim() ?? "",
                SlackProxyAddress = w.SlackProxyAddressBox.Text?.Trim() ?? "",
                AnalysisNotifySeverity = w._appSettings.AnalysisNotifySeverity,
                AnalysisNotifyCooldownMinutes = w._appSettings.AnalysisNotifyCooldownMinutes,
            };
        }
    }
}
