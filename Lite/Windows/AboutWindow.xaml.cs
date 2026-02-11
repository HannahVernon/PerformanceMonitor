/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace PerformanceMonitorLite.Windows;

public partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/erikdarlingdata/PerformanceMonitor";
    private const string IssuesUrl = "https://github.com/erikdarlingdata/PerformanceMonitor/issues";
    private const string ReleasesUrl = "https://github.com/erikdarlingdata/PerformanceMonitor/releases";
    private const string DarlingDataUrl = "https://www.erikdarling.com";

    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.Major}.{version?.Minor}.{version?.Build}";
    }

    private void GitHubLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(GitHubUrl);
    }

    private void ReportIssueLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(IssuesUrl);
    }

    private void CheckUpdatesLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(ReleasesUrl);
    }

    private void DarlingDataLink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(DarlingDataUrl);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            MessageBox.Show($"Could not open URL: {url}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
