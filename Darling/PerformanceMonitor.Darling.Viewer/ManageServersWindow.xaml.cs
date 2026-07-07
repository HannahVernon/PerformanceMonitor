/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's Manage Servers window — a faithful port of Lite's, over the viewer's own registry
/// (<see cref="ViewerServerStore"/>). Adds / edits / removes server DEFINITIONS, edits per-server excluded
/// databases (populated from the store's collected database list), and manages shared credential profiles
/// (<see cref="ViewerProfileStore"/>) via the "Credential Profiles…" button — credential MANAGEMENT is in
/// scope even though the viewer never makes the live connection (the Darling service resolves a chosen
/// profile at connect time). Making the service honor these definitions is the separate wiring flagged in
/// the PR.
/// </summary>
public partial class ManageServersWindow : Window
{
    private readonly ViewerServerStore _serverStore;
    private readonly ViewerProfileStore _profileStore;
    private readonly ViewerDataService? _dataService;

    /// <summary>True when servers were modified, so the caller refreshes the sidebar.</summary>
    public bool ServersChanged { get; private set; }

    /// <param name="dataService">Store reader for the Excluded Databases picker's collected-database list;
    /// null (viewer not connected) degrades that picker to manual-only.</param>
    public ManageServersWindow(ViewerServerStore serverStore, ViewerProfileStore profileStore, ViewerDataService? dataService)
    {
        InitializeComponent();
        _serverStore = serverStore;
        _profileStore = profileStore;
        _dataService = dataService;
        RefreshGrid();
    }

    private void RefreshGrid()
    {
        ServersGrid.ItemsSource = null;
        var servers = _serverStore.GetAllServers();
        var appVersion = GetAppVersion();
        foreach (var s in servers)
        {
            s.InstalledVersion = appVersion;
        }
        ServersGrid.ItemsSource = servers;
    }

    private static string GetAppVersion()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";

        var plusIndex = raw.IndexOf('+');
        var trimmed = plusIndex >= 0 ? raw[..plusIndex] : raw;
        return Version.TryParse(trimmed, out var v)
            ? new Version(v.Major, v.Minor, v.Build).ToString()
            : trimmed;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddServerDialog(_serverStore, _profileStore) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ServersChanged = true;
            RefreshGrid();
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void ServersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelected();

    private void EditSelected()
    {
        if (ServersGrid.SelectedItem is not ViewerServerEntry selected)
        {
            return;
        }

        var dialog = new AddServerDialog(_serverStore, _profileStore, selected) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ServersChanged = true;
            RefreshGrid();
        }
    }

    private void EditMenuItem_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) => DeleteButton_Click(sender, e);

    private void CredentialProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ManageCredentialProfilesDialog(_profileStore) { Owner = this };
        dialog.ShowDialog();
        if (dialog.ProfilesChanged)
        {
            /* Server rows may reference a profile; refresh to reflect reassignments. */
            RefreshGrid();
        }
    }

    private void ExcludedDatabases_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not ViewerServerEntry selected)
        {
            return;
        }

        var dialog = new ExcludedDatabasesDialog(_serverStore, selected, _dataService) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ExclusionsModified)
        {
            ServersChanged = true;
            RefreshGrid();
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not ViewerServerEntry selected)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Delete server '{selected.DisplayNameWithIntent}'?\n\nThis will remove the server definition and its stored credentials.",
            "Delete Server",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _serverStore.DeleteServer(selected.Id);
            ServersChanged = true;
            RefreshGrid();
        }
    }

    private void CopyCell_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyCell(sender);
    private void CopyRow_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyRow(sender);
    private void CopyAllRows_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyAllRows(sender);
    private void ExportToCsv_Click(object sender, RoutedEventArgs e) => DataGridExport.ExportToCsv(sender, "servers", ViewerExportSettings.CsvSeparator);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
