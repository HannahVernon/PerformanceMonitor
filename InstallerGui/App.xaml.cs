/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PerformanceMonitorInstallerGui.Utilities;

namespace PerformanceMonitorInstallerGui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Logger.LogToFile("App.OnStartup", "Application starting...");

            /*
            Register global exception handlers
            */
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            if (exception != null)
            {
                Logger.LogToFile("OnUnhandledException", exception);
            }

            if (e.IsTerminating)
            {
                MessageBox.Show(
                    $"A fatal error occurred and the application must close.\n\n" +
                    $"Error: {exception?.Message}\n\n" +
                    $"Inner: {exception?.InnerException?.Message}\n\n" +
                    $"Log file: {Logger.LogFilePath}",
                    "Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.LogToFile("OnDispatcherUnhandledException", e.Exception);

            e.Handled = true;

            MessageBox.Show(
                $"An error occurred: {e.Exception.Message}\n\n" +
                $"Inner: {e.Exception.InnerException?.Message}\n\n" +
                $"Log file: {Logger.LogFilePath}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception != null)
            {
                Logger.LogToFile("OnUnobservedTaskException", e.Exception);
            }

            e.SetObserved();

            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(
                    $"A background task error occurred: {e.Exception?.InnerException?.Message ?? e.Exception?.Message}\n\n" +
                    $"Log file: {Logger.LogFilePath}",
                    "Background Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
        }
    }
}
