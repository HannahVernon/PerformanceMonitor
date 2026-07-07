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
/// Single-instance enforcement uses the shared <see cref="SingleInstanceCoordinator"/> (as Lite /
/// Dashboard) so a second launch surfaces the existing window instead of opening a duplicate viewer,
/// and a newer Velopack build launched over an older one takes over.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "PerformanceMonitorDarlingViewer_SingleInstance";
    private const string ExitForUpgradeEventName = "PerformanceMonitorDarlingViewer_ExitForUpgrade";
    private const string ShowWindowEventName = "PerformanceMonitorDarlingViewer_ShowWindow";
    private SingleInstanceCoordinator? _instanceCoordinator;
    private SingleInstanceSignal? _instanceSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        /* Re-apply through ThemeManager (App.xaml already merges the same dictionary) so
           ThemeManager owns the app-level merged dictionary at runtime, before StartupUri
           creates MainWindow. Dark is the default; naming it keeps the source of truth explicit. */
        ThemeManager.Apply("Dark");

        /* Minimal file logging (ported from Lite's AppLogger) so the sidebar's View Log / Open Log
           Folder buttons have a real target and operator bug reports carry viewer diagnostics. */
        ViewerLogger.Initialize();

        /* Single-instance with upgrade handoff (shared PerformanceMonitor.Ui coordinator, mirroring Lite /
           Dashboard). Runs before base.OnStartup creates the StartupUri window, so a second launch surfaces
           the existing window and exits instead of opening a duplicate viewer; a newer build launched over an
           older one closes it and takes over (Velopack upgrade). The viewer holds no exclusive local resource
           (it is a stateless Postgres read-client), so the exit-for-upgrade listener can open immediately. */
        _instanceCoordinator = new SingleInstanceCoordinator(new SingleInstanceOptions
        {
            MutexName = MutexName,
            ProcessName = "PerformanceMonitor.Darling.Viewer",
            ExitEventName = ExitForUpgradeEventName,
            SurfaceRunningInstance = () => SingleInstanceSignal.TrySignal(ShowWindowEventName),
            GracefulSelfExit = () => Dispatcher.BeginInvoke(new Action(Shutdown)),
            Prompts = new MessageBoxHandoffPrompts("Performance Monitor Darling"),
            AutoConfirm = Array.Exists(e.Args, a => string.Equals(a, HandoffArgs.AutoConfirm, StringComparison.OrdinalIgnoreCase)),
            Log = msg => { try { ViewerLogger.Info("SingleInstance", msg); } catch { /* logger not yet ready */ } },
        });

        if (!_instanceCoordinator.TryBecomeOwner())
        {
            Shutdown();
            return;
        }

        /* Own the "surface me" channel before the (possibly slow) first render, so a fast second launch finds
           it; a signal that lands before the window exists is a harmless no-op (the callback null-checks). */
        _instanceSignal = new SingleInstanceSignal(ShowWindowEventName, OnSurfaceWindowRequested);

        /* No risky exclusive init to protect, so let a newer build ask us to step aside for an upgrade now. */
        _instanceCoordinator.EnableUpgradeHandoff();

        ViewerLogger.Info("App", $"Starting PerformanceMonitor Darling Viewer v{Assembly.GetExecutingAssembly().GetName().Version}");

        /* Surface otherwise-invisible crashes into the log now that we have one. */
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);
    }

    /// <summary>
    /// Invoked on <see cref="SingleInstanceSignal"/>'s background thread when a second launch asks us to
    /// surface the window. Marshals to the UI thread and brings the existing window to the front via WPF's
    /// own path (the viewer has no tray, so this simply restores/activates it).
    /// </summary>
    private void OnSurfaceWindowRequested()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Current.MainWindow is MainWindow window)
            {
                window.SurfaceWindow();
            }
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewerLogger.Info("App", "Shutting down");
        _instanceSignal?.Dispose();
        _instanceCoordinator?.Dispose();
        ViewerLogger.Shutdown();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        => ViewerLogger.Error("App", "Unhandled UI exception", e.Exception);

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => ViewerLogger.Error("App", "Unhandled domain exception", e.ExceptionObject as Exception);
}
