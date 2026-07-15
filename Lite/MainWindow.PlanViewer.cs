/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PerformanceMonitor.Ui;

namespace PerformanceMonitorLite;

public partial class MainWindow : Window
{
    /* The standalone Plan Viewer's tab management (empty "New Plan" sub-tabs, open/paste/drag-drop, plan
       rendering, unique labelling, teardown) is the shared PerformanceMonitor.Ui.StandalonePlanViewerController
       -- ~300 lines that used to be duplicated near-verbatim between this file and the Darling viewer. This
       file keeps only what is app-specific: revealing/hiding the outer ServerTabControl on open/close, and
       forwarding the XAML-wired drag-over / drop / key-down handlers to the controller. Lazy (a field
       initializer can't reference the instance-named MainWindowPlanTabControl). */
    private StandalonePlanViewerController? _planViewerControllerField;
    private StandalonePlanViewerController PlanViewerController =>
        _planViewerControllerField ??= new StandalonePlanViewerController(MainWindowPlanTabControl);

    private void OpenPlanViewerButton_Click(object sender, RoutedEventArgs e)
    {
        PlanViewerController.EnsureInitialized();
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        ServerTabControl.Visibility = Visibility.Visible;
        MainWindowPlanViewerTab.Visibility = Visibility.Visible;
        if (MainWindowPlanViewerTab.IsSelected)
        {
            // Already on Plan Viewer -- just add a new empty sub-tab
            PlanViewerController.AddNewEmptyPlanSubTab();
        }
        else
        {
            MainWindowPlanViewerTab.IsSelected = true;
            PlanViewerController.AddNewEmptyPlanSubTab();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => MainWindowPlanTabControl.Focus()));
    }

    private void MainWindowPlanViewerClose_Click(object sender, RoutedEventArgs e)
    {
        // Cleanup each sub-tab's viewer + reset the inner tab control (shared), then restore this app's shell.
        PlanViewerController.Reset();
        MainWindowPlanViewerTab.Visibility = Visibility.Collapsed;
        // Select first visible tab
        foreach (TabItem item in ServerTabControl.Items)
        {
            if (item.Visibility == Visibility.Visible && item != MainWindowPlanViewerTab)
            {
                item.IsSelected = true;
                break;
            }
        }
    }

    private void MainWindowPlanViewer_DragOver(object sender, DragEventArgs e) =>
        PlanViewerController.HandleDragOver(e);

    private void MainWindowPlanViewer_Drop(object sender, DragEventArgs e) =>
        PlanViewerController.HandleDrop(e);

    private void MainWindowPlanViewer_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        PlanViewerController.HandleKeyDown(e);
}
