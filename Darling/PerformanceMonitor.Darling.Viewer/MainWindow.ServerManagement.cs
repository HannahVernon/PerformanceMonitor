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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The MainWindow server-management + status-chrome partial (ported to parity with Lite's MainWindow):
/// the sidebar's favorite pinning + collection-freshness status dots, the right-click server menu, the
/// footer Add / Manage / Import / log buttons, the sidebar collapse toggle, and the multi-field status bar.
///
/// <para>Adaptation (hard viewer facts, matching the rest of the Lite→Darling port). The sidebar lists the
/// servers the Darling service is actually COLLECTING (the Postgres <c>servers</c> table), so its dots come
/// from collection freshness — the viewer never pings a monitored server — and Connect/Disconnect have no
/// meaning and are omitted. Add / Manage / Edit / Remove operate on the viewer's OWN registry
/// (<see cref="ViewerServerStore"/> → viewer-servers.json); an added/edited server appears in the sidebar
/// once the service registers it into the store (that service wiring is the follow-up flagged in the PR).
/// Favorites are the one thing the viewer acts on today: pinned in the registry (matched by server name),
/// starred and sorted-to-top here.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>The viewer's own server-definition registry, backing Add / Manage / Edit / Remove + favorites.</summary>
    private readonly ViewerServerStore _serverStore = new();

    private bool _sidebarCollapsed;

    // ── Server-list enrichment (favorites + freshness) ──────────────────────────────

    /// <summary>
    /// Stamps each row's favorite flag from the registry (by server name) and returns the list sorted
    /// favorites-first, then by display name — Lite's pin ordering. Called on load and after a registry change.
    /// </summary>
    private List<DarlingServer> ApplyFavoritesAndSort(List<DarlingServer> servers)
    {
        foreach (var s in servers)
        {
            s.IsFavorite = _serverStore.IsFavorite(s.ServerName);
        }

        return SortWithFavorites(servers);
    }

    private static List<DarlingServer> SortWithFavorites(List<DarlingServer> servers) =>
        servers
            .OrderByDescending(s => s.IsFavorite)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Re-stamps favorites on the currently-shown rows and re-sorts, preserving the selection.</summary>
    private void ReapplyFavoritesToServerList()
    {
        if (ServerList.ItemsSource is not IEnumerable<DarlingServer> current)
        {
            return;
        }

        foreach (var s in current)
        {
            s.IsFavorite = _serverStore.IsFavorite(s.ServerName);
        }

        var selected = ServerList.SelectedItem as DarlingServer;
        ServerList.ItemsSource = SortWithFavorites(current.ToList());
        if (selected is not null)
        {
            ServerList.SelectedItem = selected;
        }
    }

    /// <summary>
    /// Refreshes the sidebar status dots and the status bar's collector-health + collection fields from a
    /// single collection-freshness query (the same MAX(collection_time) signal the Overview cards use).
    /// Updates each row in place so the dots recolour without resetting the list's selection.
    /// </summary>
    private async Task RefreshServerStatusAsync()
    {
        if (_dataService is null)
        {
            return;
        }

        Dictionary<int, DateTime> freshness;
        try
        {
            freshness = await _dataService.GetServerFreshnessAsync();
        }
        catch (Exception ex)
        {
            ViewerLogger.Warn("ServerStatus", $"freshness read failed: {ex.Message}");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var servers = (ServerList.ItemsSource as IEnumerable<DarlingServer>)?.ToList() ?? new List<DarlingServer>();

        var online = 0;
        var warning = 0;
        var offline = 0;
        DateTime? newest = null;

        foreach (var s in servers)
        {
            DateTime? last = freshness.TryGetValue(s.ServerId, out var t) ? t : null;
            s.ApplyFreshness(last, nowUtc);

            switch (s.DotStatus)
            {
                case "Online": online++; break;
                case "Warning": warning++; break;
                default: offline++; break;
            }

            if (last.HasValue && (newest is null || last.Value > newest.Value))
            {
                newest = last.Value;
            }
        }

        if (servers.Count == 0)
        {
            CollectorHealthText.Text = "";
        }
        else if (warning + offline == 0)
        {
            CollectorHealthText.Text = $"Collectors: {online} OK";
        }
        else
        {
            CollectorHealthText.Text = $"Collectors: {warning} stale, {offline} offline";
        }

        if (newest is null)
        {
            CollectionStatusText.Text = "Collection: Idle";
        }
        else
        {
            var age = nowUtc - newest.Value;
            CollectionStatusText.Text = age.TotalSeconds < 90
                ? "Collection: Active"
                : $"Collection: {FormatAge(age)} ago";
        }
    }

    /// <summary>Refreshes the status bar's database-size field from the store's on-disk size.</summary>
    private async Task RefreshStoreSizeAsync()
    {
        if (_dataService is null)
        {
            return;
        }

        try
        {
            var bytes = await _dataService.GetStoreSizeBytesAsync();
            DatabaseSizeText.Text = bytes.HasValue ? $"Database: {FormatBytes(bytes.Value)}" : "Database: --";
        }
        catch (Exception ex)
        {
            ViewerLogger.Warn("ServerStatus", $"store size read failed: {ex.Message}");
        }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1) return $"{age.TotalSeconds:F0}s";
        if (age.TotalHours < 1) return $"{age.TotalMinutes:F0}m";
        if (age.TotalDays < 1) return $"{age.TotalHours:F0}h";
        return $"{age.TotalDays:F0}d";
    }

    private static string FormatBytes(long bytes)
    {
        const double mb = 1024.0 * 1024.0;
        const double gb = mb * 1024.0;
        return bytes >= gb ? $"{bytes / gb:F1} GB" : $"{bytes / mb:F0} MB";
    }

    // ── Server-row context menu (Toggle Favorite / Edit / Remove — no Connect/Disconnect) ─────

    /// <summary>The <see cref="DarlingServer"/> behind a server-row context-menu click (mirrors Lite's resolver).</summary>
    private static DarlingServer? GetServerFromContextMenu(object sender)
    {
        if (sender is not MenuItem menuItem)
        {
            return null;
        }

        var contextMenu = menuItem.Parent as ContextMenu;
        var target = contextMenu?.PlacementTarget as FrameworkElement;
        return target?.DataContext as DarlingServer;
    }

    private void ServerContextMenu_ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        var server = GetServerFromContextMenu(sender);
        if (server is null)
        {
            return;
        }

        var isFavorite = _serverStore.ToggleFavorite(server.ServerName);
        server.IsFavorite = isFavorite;
        ReapplyFavoritesToServerList();
        StatusText.Text = isFavorite ? $"Pinned {server.DisplayName}" : $"Unpinned {server.DisplayName}";
    }

    private void ServerContextMenu_Edit_Click(object sender, RoutedEventArgs e)
    {
        var server = GetServerFromContextMenu(sender);
        if (server is null)
        {
            return;
        }

        /* Edit the matching registry entry, or "adopt" the collected server into the registry by seeding a
           new entry from its name/display (the service registered it; the viewer had no definition yet). */
        var entry = _serverStore.GetByServerName(server.ServerName)
            ?? new ViewerServerEntry { ServerName = server.ServerName, DisplayName = server.DisplayName };

        var dialog = new AddServerDialog(_serverStore, entry) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ReapplyFavoritesToServerList();
            StatusText.Text = $"Saved '{server.DisplayName}' to the viewer registry.";
        }
    }

    private void ServerContextMenu_Remove_Click(object sender, RoutedEventArgs e)
    {
        var server = GetServerFromContextMenu(sender);
        if (server is null)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Remove '{server.DisplayName}' from the viewer's registry?\n\n" +
            "This removes only the viewer-side definition and its favorite pin. The Darling service keeps " +
            "collecting this server (stopping collection is service-side — see the release notes), so its row " +
            "stays in the list until the service is reconfigured.",
            "Remove Server",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var entry = _serverStore.GetByServerName(server.ServerName);
        if (entry is not null)
        {
            _serverStore.DeleteServer(entry.Id);
        }

        server.IsFavorite = false;
        ReapplyFavoritesToServerList();
        StatusText.Text = $"Removed '{server.DisplayName}' from the viewer registry.";
    }

    // ── Footer buttons (Add / Manage / Import Settings / View Log / Open Log Folder) ─────

    private void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddServerDialog(_serverStore) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.AddedServer is not null)
        {
            ReapplyFavoritesToServerList();
            StatusText.Text =
                $"Saved server '{dialog.AddedServer.DisplayName}' to the viewer registry. " +
                "The Darling service will collect it once server-registration wiring lands.";
        }
    }

    private void ManageServersButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new ManageServersWindow(_serverStore) { Owner = this };
        window.ShowDialog();
        if (window.ServersChanged)
        {
            ReapplyFavoritesToServerList();
        }
    }

    private void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        var logFile = ViewerLogger.GetCurrentLogFile();
        try
        {
            var target = File.Exists(logFile) ? logFile : ViewerLogger.GetLogDirectory();
            if (string.IsNullOrEmpty(target))
            {
                MessageBox.Show("No log file has been written yet.", "View Log", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var logDir = ViewerLogger.GetLogDirectory();
        if (string.IsNullOrEmpty(logDir))
        {
            logDir = ViewerLogger.DefaultLogDirectory();
        }

        try
        {
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo { FileName = logDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Imports server definitions (and copies viewer settings/preferences when absent) from a previous
    /// viewer's per-user config folder — the Darling analog of Lite's Import Settings. Lite's DuckDB
    /// "Import Data" has no viewer meaning and is omitted.
    /// </summary>
    private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a previous PerformanceMonitorDarling config folder"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var sourceServers = Path.Combine(dialog.FolderName, "viewer-servers.json");
        if (!File.Exists(sourceServers))
        {
            MessageBox.Show(
                "No viewer-servers.json found in the selected folder.\n\n" +
                "Select the PerformanceMonitorDarling folder of a previous viewer install " +
                "(under %APPDATA%).",
                "Import Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var (imported, skipped) = _serverStore.ImportServersFromFile(sourceServers);

            var targetDir = Path.GetDirectoryName(ViewerServerStore.DefaultFilePath());
            var copied = 0;
            if (!string.IsNullOrEmpty(targetDir))
            {
                foreach (var fileName in new[] { "viewer-settings.json", "viewer-preferences.json" })
                {
                    var source = Path.Combine(dialog.FolderName, fileName);
                    var destination = Path.Combine(targetDir, fileName);
                    if (File.Exists(source) && !File.Exists(destination))
                    {
                        Directory.CreateDirectory(targetDir);
                        File.Copy(source, destination);
                        copied++;
                    }
                }
            }

            var message = $"Imported {imported} server definition(s).";
            if (skipped > 0)
            {
                message += $"\nSkipped {skipped} already-configured server(s).";
            }
            if (copied > 0)
            {
                message += $"\nCopied {copied} settings file(s). Restart the viewer to apply them.";
            }
            message += "\n\nServer credentials are NOT importable (they live only in Windows Credential Manager, " +
                       "per user). Re-enter any SQL/service-principal secret in Manage Servers.";

            MessageBox.Show(message, "Import Settings", MessageBoxButton.OK, MessageBoxImage.Information);

            if (imported > 0)
            {
                ReapplyFavoritesToServerList();
            }
        }
        catch (Exception ex)
        {
            ViewerLogger.Error("ImportSettings", "Import failed", ex);
            MessageBox.Show($"Failed to import settings: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Sidebar collapse toggle ─────────────────────────────────────────────────────

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;

        if (_sidebarCollapsed)
        {
            SidebarColumn.Width = new GridLength(40);
            SidebarHeaderText.Visibility = Visibility.Collapsed;
            ServerList.Visibility = Visibility.Collapsed;
            SidebarFooter.Visibility = Visibility.Collapsed;
            ServersHintText.Visibility = Visibility.Collapsed;
            SidebarToggleButton.Content = "»";
        }
        else
        {
            SidebarColumn.Width = new GridLength(280);
            SidebarHeaderText.Visibility = Visibility.Visible;
            ServerList.Visibility = Visibility.Visible;
            SidebarFooter.Visibility = Visibility.Visible;
            ServersHintText.Visibility = ServerList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SidebarToggleButton.Content = "«";
        }
    }
}
