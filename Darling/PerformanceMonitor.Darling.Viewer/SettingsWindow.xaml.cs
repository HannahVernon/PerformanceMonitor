/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Windows;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's per-user Settings dialog: the persisted DEFAULTS that seed a newly-opened server tab's
/// toolbar (default time range + auto-refresh on/off + interval). Loaded from and saved to
/// <see cref="ViewerPreferencesStore"/> by MainWindow; this window only edits an in-memory copy and hands
/// the result back on Save. Changing settings applies to tabs opened afterward — open tabs keep their own
/// toolbar state (the simple, predictable rule). The dialog's combo item order mirrors the toolbar's, so
/// a selected index means the same thing here and there.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>The edited settings, populated on Save. Null until the user clicks Save (Cancel leaves it null).</summary>
    public ViewerPreferences? Result { get; private set; }

    public SettingsWindow(ViewerPreferences current)
    {
        InitializeComponent();

        /* Seed the controls from the current settings (normalized so an out-of-range persisted index can
           never leave a combo unselected). The combos mirror the toolbar's item order, so the stored
           index selects the matching row directly. */
        var normalized = new ViewerPreferences
        {
            DefaultTimeRangeIndex = current.DefaultTimeRangeIndex,
            AutoRefreshEnabled = current.AutoRefreshEnabled,
            AutoRefreshIntervalIndex = current.AutoRefreshIntervalIndex,
        }.Normalize();

        TimeRangeCombo.SelectedIndex = normalized.DefaultTimeRangeIndex;
        AutoRefreshCheckBox.IsChecked = normalized.AutoRefreshEnabled;
        AutoRefreshIntervalCombo.SelectedIndex = normalized.AutoRefreshIntervalIndex;

        /* The interval only matters while auto-refresh is on, so grey it out when the box is unchecked. */
        AutoRefreshIntervalCombo.IsEnabled = normalized.AutoRefreshEnabled;
    }

    private void AutoRefreshCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        /* Guard the load-time raise before the combo exists (Checked can fire during InitializeComponent). */
        if (AutoRefreshIntervalCombo is not null)
        {
            AutoRefreshIntervalCombo.IsEnabled = AutoRefreshCheckBox.IsChecked == true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new ViewerPreferences
        {
            DefaultTimeRangeIndex = TimeRangeCombo.SelectedIndex,
            AutoRefreshEnabled = AutoRefreshCheckBox.IsChecked == true,
            AutoRefreshIntervalIndex = AutoRefreshIntervalCombo.SelectedIndex,
        }.Normalize();

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
