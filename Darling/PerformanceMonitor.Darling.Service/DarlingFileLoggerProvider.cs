/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The service's rolling file log — an <see cref="ILoggerProvider"/> so every existing
/// <c>ILogger</c> call site (collector run lines, connect edges, reload notices, errors) lands in a
/// greppable file with zero changes at the call sites. This is the service's PRIMARY diagnostic
/// surface: the Windows Event Log provider is also wired, but its event source can only be created
/// by an elevated principal — the recommended <c>NT SERVICE</c> virtual account cannot, so on a
/// by-the-book install the Event Log silently receives nothing until the installer pre-creates the
/// source (see the README's install step). A headless service must not depend on that.
///
/// <para>Disciplines ported from the viewer's <c>ViewerLogger</c> (itself a port of Lite's
/// AppLogger): buffered writes on a 5-second flush timer (a log line never blocks a collection
/// sweep on disk I/O), one file per day, and never-throw — a full disk or an ACL surprise turns
/// logging into a no-op, never a service crash. Files older than <see cref="RetentionDays"/> are
/// swept at startup so an unattended service never grows an unbounded log directory.</para>
///
/// <para>Directory: <c>%ProgramData%\PerformanceMonitorDarling\logs</c> — the machine-level Darling
/// folder the managed store already lives under (<c>...\pg</c>), created by the service account on
/// first use exactly like the data directory. File: <c>darling-service_yyyyMMdd.log</c>.</para>
/// </summary>
public sealed class DarlingFileLoggerProvider : ILoggerProvider
{
    internal const int RetentionDays = 14;

    private readonly ConcurrentQueue<string> _buffer = new();
    private readonly Timer _flushTimer;
    private readonly object _flushLock = new();
    private readonly string _logDirectory;
    private readonly bool _enabled;

    public DarlingFileLoggerProvider()
        : this(DefaultLogDirectory())
    {
    }

    /// <summary>Test seam: point the provider at any directory.</summary>
    internal DarlingFileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        try
        {
            Directory.CreateDirectory(_logDirectory);
            SweepOldFiles();
            _enabled = true;
        }
        catch
        {
            /* A log directory we cannot create must never take down the service. */
            _enabled = false;
        }

        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>%ProgramData%\PerformanceMonitorDarling\logs — the service's machine-level log directory.</summary>
    public static string DefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PerformanceMonitorDarling",
        "logs");

    /// <summary>Today's log file path (darling-service_yyyyMMdd.log under the log directory).</summary>
    public string CurrentLogFile() =>
        Path.Combine(_logDirectory, $"darling-service_{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Flush();
        _flushTimer.Dispose();
    }

    private void Enqueue(string line)
    {
        if (_enabled)
        {
            _buffer.Enqueue(line);
        }
    }

    private void Flush()
    {
        if (!_enabled || _buffer.IsEmpty)
        {
            return;
        }

        lock (_flushLock)
        {
            try
            {
                var sb = new StringBuilder();
                while (_buffer.TryDequeue(out var line))
                {
                    sb.AppendLine(line);
                }

                if (sb.Length > 0)
                {
                    File.AppendAllText(CurrentLogFile(), sb.ToString());
                }
            }
            catch
            {
                /* Never let a logging failure crash the service. */
            }
        }
    }

    private void SweepOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "darling-service_*.log")
                .Where(f => File.GetLastWriteTime(f) < cutoff))
            {
                File.Delete(file);
            }
        }
        catch
        {
            /* Retention is best-effort; a locked or undeletable file is tomorrow's problem. */
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly DarlingFileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(DarlingFileLoggerProvider provider, string category)
        {
            _provider = provider;
            /* The short category name — "DarlingWorker", not the full namespace — keeps lines scannable. */
            var dot = category.LastIndexOf('.');
            _category = dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Abbreviate(logLevel)}] [{_category}] {formatter(state, exception)}";
                if (exception is not null)
                {
                    line += $" | {exception.GetType().Name}: {exception.Message}";
                }

                _provider.Enqueue(line);
            }
            catch
            {
                /* A formatter that throws must never take down the caller. */
            }
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "?????",
        };
    }
}
