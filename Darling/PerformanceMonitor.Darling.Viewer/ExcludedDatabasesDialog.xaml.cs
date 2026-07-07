/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Windows;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's Excluded Databases editor — adapted from Lite's dialog. Lite reads the server's LIVE
/// database list (it connects to the monitored server) and offers a checkbox per database; the Darling
/// viewer never connects to monitored servers, so this is a manual, one-name-per-line editor for the same
/// <see cref="ViewerServerEntry.ExcludedDatabases"/> field the Darling service will consume. Persisted
/// through <see cref="ViewerServerStore.UpdateServer(ViewerServerEntry)"/>.
/// </summary>
public partial class ExcludedDatabasesDialog : Window
{
    private readonly ViewerServerStore _serverStore;
    private readonly ViewerServerEntry _entry;

    /// <summary>Set true when the exclusion list was changed and saved, so the caller refreshes.</summary>
    public bool ExclusionsModified { get; private set; }

    public ExcludedDatabasesDialog(ViewerServerStore serverStore, ViewerServerEntry entry)
    {
        InitializeComponent();
        _serverStore = serverStore;
        _entry = entry;
        HeaderText.Text = $"Excluded Databases — {entry.DisplayNameWithIntent}";
        ExclusionsBox.Text = string.Join(Environment.NewLine, entry.ExcludedDatabases ?? new System.Collections.Generic.List<string>());
        StatusText.Text = $"{(entry.ExcludedDatabases?.Count ?? 0)} database(s) currently excluded.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _entry.ExcludedDatabases = ExclusionsBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _serverStore.UpdateServer(_entry);
            ExclusionsModified = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to save: {ex.Message}";
            ViewerLogger.Error("ExcludedDatabasesDialog", "Failed to save exclusions", ex);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
