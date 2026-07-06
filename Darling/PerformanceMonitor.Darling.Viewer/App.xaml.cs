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
using System.Windows.Threading;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Darling viewer — a Postgres client of the central store. MainWindow owns data startup:
/// it loads the viewer's sliver of darling.json (<see cref="ViewerSettings"/>) and connects
/// on first render. App startup applies the shared Dark theme through <see cref="ThemeManager"/>
/// so copied Lite XAML resolves its theme keys; the theme is fixed to Dark for v1 (no picker), and
/// <see cref="ThemeManager.CurrentTheme"/> stays "Dark" so <c>ChartStyle</c> draws dark chrome.
/// Single-instance plumbing comes in a later milestone.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        /* Re-apply through ThemeManager (App.xaml already merges the same dictionary) so
           ThemeManager owns the app-level merged dictionary at runtime, before StartupUri
           creates MainWindow. Dark is the default; naming it keeps the source of truth explicit. */
        ThemeManager.Apply("Dark");

        /* Minimal file logging (ported from Lite's AppLogger) so the sidebar's View Log / Open Log
           Folder buttons have a real target and operator bug reports carry viewer diagnostics. */
        ViewerLogger.Initialize();
        ViewerLogger.Info("App", $"Starting PerformanceMonitor Darling Viewer v{Assembly.GetExecutingAssembly().GetName().Version}");

        /* Surface otherwise-invisible crashes into the log now that we have one. */
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewerLogger.Info("App", "Shutting down");
        ViewerLogger.Shutdown();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        => ViewerLogger.Error("App", "Unhandled UI exception", e.Exception);

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => ViewerLogger.Error("App", "Unhandled domain exception", e.ExceptionObject as Exception);
}
