/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitorLite.Models;
using PerformanceMonitorLite.Services;

namespace PerformanceMonitorLite.Windows;

public partial class CollectorScheduleEditorWindow : Window
{
    private readonly ScheduleManager _scheduleManager;
    private readonly ServerManager _serverManager;
    private readonly string? _serverId;
    private readonly string? _serverDisplayName;
    private List<CollectorSchedule> _editingSchedules = new();
    private bool _suppressPresetChange;
    private bool _isEditingDefault;

    /// <summary>
    /// True if the user saved changes.
    /// </summary>
    public bool Saved { get; private set; }

    /// <summary>
    /// Opens the editor for a specific server's schedule.
    /// </summary>
    public CollectorScheduleEditorWindow(
        ScheduleManager scheduleManager,
        ServerManager serverManager,
        string serverId,
        string serverDisplayName)
    {
        InitializeComponent();
        _scheduleManager = scheduleManager;
        _serverManager = serverManager;
        _serverId = serverId;
        _serverDisplayName = serverDisplayName;
        _isEditingDefault = false;

        Title = $"Collector Schedules - {serverDisplayName}";
        HeaderText.Text = $"Collector Schedules - {serverDisplayName}";
        SubHeaderText.Text = $"Server: {serverDisplayName}";

        SetupCopyFromServerCombo();
        LoadServerSchedule();
    }

    /// <summary>
    /// Opens the editor for the default schedule.
    /// </summary>
    public CollectorScheduleEditorWindow(
        ScheduleManager scheduleManager,
        ServerManager serverManager)
    {
        InitializeComponent();
        _scheduleManager = scheduleManager;
        _serverManager = serverManager;
        _isEditingDefault = true;

        Title = "Default Collector Schedule";
        HeaderText.Text = "Default Collector Schedule";
        SubHeaderText.Text = "This schedule applies to all servers without a custom override.";

        /* Hide server-specific controls */
        UseDefaultCheckBox.Visibility = Visibility.Collapsed;
        CopyFromDefaultButton.Visibility = Visibility.Collapsed;

        _editingSchedules = CloneScheduleList(_scheduleManager.GetDefaultSchedule());
        ScheduleGrid.ItemsSource = _editingSchedules;
        DetectActivePreset();
    }

    private void SetupCopyFromServerCombo()
    {
        var servers = _serverManager.GetAllServers()
            .Where(s => s.Id != _serverId)
            .ToList();

        if (servers.Count > 0)
        {
            CopyFromServerCombo.Visibility = Visibility.Visible;
            CopyFromServerButton.Visibility = Visibility.Visible;
            CopyFromServerCombo.DisplayMemberPath = "DisplayName";
            CopyFromServerCombo.SelectedValuePath = "Id";
            CopyFromServerCombo.ItemsSource = servers;
            CopyFromServerCombo.SelectedIndex = 0;
        }
    }

    private void LoadServerSchedule()
    {
        bool usesDefault = !_scheduleManager.HasServerOverride(_serverId!);
        UseDefaultCheckBox.IsChecked = usesDefault;

        _editingSchedules = CloneScheduleList(_scheduleManager.GetSchedulesForServer(_serverId!));
        ScheduleGrid.ItemsSource = _editingSchedules;
        UpdateEditableState(usesDefault);
        DetectActivePreset();
    }

    private void UpdateEditableState(bool usesDefault)
    {
        bool editable = !usesDefault;
        ScheduleGrid.IsReadOnly = usesDefault;
        ScheduleGrid.Opacity = usesDefault ? 0.6 : 1.0;
        PresetComboBox.IsEnabled = editable;
        CopyFromDefaultButton.IsEnabled = editable;
        CopyFromServerButton.IsEnabled = editable;
        CopyFromServerCombo.IsEnabled = editable;

        StatusText.Text = usesDefault
            ? "Using default schedule (read-only). Uncheck 'Use default schedule' to customize this server."
            : "Custom schedule. Changes apply only to this server.";
    }

    private void UseDefaultCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isEditingDefault) return;

        bool usesDefault = UseDefaultCheckBox.IsChecked == true;

        /* Reset to a fresh copy of the default schedule; UpdateEditableState makes it read-only when 'use default' is checked. */
        _editingSchedules = CloneScheduleList(_scheduleManager.GetDefaultSchedule());
        ScheduleGrid.ItemsSource = _editingSchedules;

        UpdateEditableState(usesDefault);
        DetectActivePreset();
    }

    private void DetectActivePreset()
    {
        _suppressPresetChange = true;
        try
        {
            string active = ScheduleManager.DetectPreset(_editingSchedules);
            for (int i = 0; i < PresetComboBox.Items.Count; i++)
            {
                if (PresetComboBox.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), active, StringComparison.OrdinalIgnoreCase))
                {
                    PresetComboBox.SelectedIndex = i;
                    return;
                }
            }
            PresetComboBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressPresetChange = false;
        }
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetChange) return;
        if (PresetComboBox.SelectedItem is not ComboBoxItem selected) return;

        string presetName = selected.Content?.ToString() ?? "";
        if (presetName == "Custom") return;

        var result = MessageBox.Show(
            $"Apply the \"{presetName}\" preset?\n\nThis will change all collector frequencies. Enabled/disabled state and retention settings are not affected.",
            "Apply Collection Preset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            DetectActivePreset();
            return;
        }

        ScheduleManager.ApplyPreset(_editingSchedules, presetName);
        ScheduleGrid.ItemsSource = null;
        ScheduleGrid.ItemsSource = _editingSchedules;
        DetectActivePreset();
    }

    private void CopyFromDefault_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Replace this server's schedule with a copy of the default schedule?",
            "Copy from Default",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _editingSchedules = CloneScheduleList(_scheduleManager.GetDefaultSchedule());
        ScheduleGrid.ItemsSource = _editingSchedules;
        DetectActivePreset();
    }

    private void CopyFromServer_Click(object sender, RoutedEventArgs e)
    {
        if (CopyFromServerCombo.SelectedItem is not Models.ServerConnection selected) return;
        var sourceServerId = selected.Id;

        var result = MessageBox.Show(
            $"Replace this server's schedule with a copy of {selected.DisplayName}'s schedule?",
            "Copy from Server",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _editingSchedules = CloneScheduleList(_scheduleManager.GetSchedulesForServer(sourceServerId));
        ScheduleGrid.ItemsSource = _editingSchedules;
        DetectActivePreset();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditingDefault)
        {
            /* Save to default schedule */
            foreach (var edited in _editingSchedules)
            {
                _scheduleManager.UpdateSchedule(edited.Name,
                    enabled: edited.Enabled,
                    frequencyMinutes: edited.FrequencyMinutes,
                    retentionDays: edited.RetentionDays);
            }
        }
        else if (UseDefaultCheckBox.IsChecked == true)
        {
            /* Revert to default — remove override */
            _scheduleManager.RemoveServerOverride(_serverId!);
        }
        else
        {
            /* Save per-server override */
            _scheduleManager.SetScheduleForServer(_serverId!, _editingSchedules);
        }

        Saved = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────

    private static List<CollectorSchedule> CloneScheduleList(IReadOnlyList<CollectorSchedule> source)
    {
        return source.Select(s => new CollectorSchedule
        {
            Name = s.Name,
            Enabled = s.Enabled,
            FrequencyMinutes = s.FrequencyMinutes,
            RetentionDays = s.RetentionDays,
            Description = s.Description
        }).ToList();
    }
}
