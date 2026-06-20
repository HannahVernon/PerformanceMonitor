/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;

namespace PerformanceMonitorLite.Windows;

/// <summary>
/// Standalone window that hosts a <see cref="PerformanceMonitorLite.Controls.PlanViewerControl"/> so any
/// drill-down / history window can open a collected or actual plan in-app. Shown owned + non-modal so it
/// stays interactive above a modal drill-down and closes with its host.
/// </summary>
public partial class PlanViewerWindow : Window
{
    public PlanViewerWindow()
    {
        InitializeComponent();
        // The Lite PlanViewerControl needs an explicit Cleanup() to unsubscribe ThemeManager.
        Closed += (_, _) => Viewer.Cleanup();
    }

    /// <summary>Loads a plan into this window's viewer. Caller validates the XML first (see <see cref="ShowPlanAsync"/>).</summary>
    public void LoadPlan(string planXml, string label, string? queryText)
    {
        if (!string.IsNullOrWhiteSpace(label))
            Title = label.Length > 80 ? label[..80] : label;
        Viewer.LoadPlan(planXml, label, queryText);
    }

    /// <summary>
    /// Opens a new, owned, non-modal plan window and loads the plan. A new window per call lets the user
    /// compare plans side by side; owned so it stays usable above a modal host window. Returns a completed
    /// Task so it satisfies the shared controller's async <c>showPlan</c> delegate.
    /// </summary>
    public static Task ShowPlanAsync(Window owner, string planXml, string label, string? queryText)
    {
        // The Lite control does not validate XML — mirror OpenPlanTab and parse-check up front.
        try
        {
            XDocument.Parse(planXml);
        }
        catch (System.Xml.XmlException ex)
        {
            MessageBox.Show(owner, $"The plan XML is not valid:\n\n{ex.Message}", "Invalid Plan XML",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        var window = new PlanViewerWindow { Owner = owner };
        window.Show();
        try
        {
            window.LoadPlan(planXml, label, queryText);
        }
        catch (Exception ex)
        {
            window.Close();
            MessageBox.Show(owner, $"Failed to load the execution plan:\n\n{ex.Message}", "Plan Load Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return Task.CompletedTask;
    }
}
