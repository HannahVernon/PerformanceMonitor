/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;

namespace PerformanceMonitorInstallerGui.Utilities
{
    /// <summary>
    /// Shared logging utility for error logging to file
    /// </summary>
    public static class Logger
    {
        public static readonly string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "InstallerGui_Error.log");

        public static void LogToFile(string context, string message)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {message}\n";
                File.AppendAllText(LogFilePath, logEntry);
            }
            catch
            {
                /*Ignore logging errors*/
            }
        }

        public static void LogToFile(string context, Exception ex)
        {
            try
            {
                var logEntry = $"""
                    ===============================================
                    {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                    Context: {context}
                    Exception: {ex.GetType().FullName}
                    Message: {ex.Message}
                    Inner Exception: {ex.InnerException?.GetType().FullName}: {ex.InnerException?.Message}
                    Stack Trace:
                    {ex.StackTrace}
                    Inner Stack:
                    {ex.InnerException?.StackTrace}
                    ===============================================

                    """;
                File.AppendAllText(LogFilePath, logEntry);
            }
            catch
            {
                /*Ignore logging errors*/
            }
        }
    }
}
