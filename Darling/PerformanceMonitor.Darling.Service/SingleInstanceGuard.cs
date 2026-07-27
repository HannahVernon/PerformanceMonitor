/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// A system-wide single-instance lock for the Darling service (#1581). The field incident: a stray
/// <c>Service.exe --version</c> fell through into a real startup and spun up a SECOND service instance, which
/// fought the first over the bundled PostgreSQL shared-memory segment and the MCP/web ports — a ~7.5-hour
/// outage. This guard is the defense-in-depth backstop (the arg handling in <see cref="DarlingCliCommands"/>
/// stops the specific <c>--version</c> path; this stops ANY second invocation that reaches startup): acquire a
/// named mutex BEFORE running the host (which is what touches Postgres); if another instance already holds it,
/// the caller logs and exits without starting.
///
/// <para>The production name is <see cref="ServiceMutexName"/> in the <c>Global\</c> namespace so it spans
/// terminal-services sessions — the service runs in session 0 and an interactive <c>Service.exe</c> run (the
/// incident) is in the user session, so a session-local (<c>Local\</c>) mutex would NOT collide. Creating a
/// <c>Global\</c> object needs <c>SeCreateGlobalPrivilege</c>, which the service account and elevated consoles
/// hold; tests pass a <c>Local\</c> name to stay permission-independent.</para>
///
/// <para>An <see cref="AbandonedMutexException"/> (a previous instance crashed or was force-killed without a
/// clean shutdown) is treated as acquired, with <see cref="WasAbandoned"/> set so the caller warns — a stale
/// abandoned mutex must never wedge every future start. The owning instance releases the mutex on graceful
/// shutdown (<see cref="Dispose"/>), so a normal SCM stop→start re-acquires cleanly (the SCM serializes the two
/// processes, and each start is a fresh process).</para>
///
/// <para>Windows-guarded by the CALLER (<c>OperatingSystem.IsWindows()</c>), matching how the codebase gates
/// Windows-only behavior: the <c>Global\</c> namespace and the managed-Postgres shared-memory fight are Windows
/// concerns, and the managed bootstrap already fail-closes on non-Windows.</para>
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>The production mutex name — <c>Global\</c> so it spans sessions (service in session 0 vs an
    /// interactive user-session invocation). Pinned by test.</summary>
    internal const string ServiceMutexName = @"Global\PerformanceMonitorDarlingService";

    private Mutex? _mutex;
    private bool _owns;
    private bool _disposed;

    private SingleInstanceGuard()
    {
    }

    /// <summary>True when this process acquired the single-instance slot and should proceed with startup;
    /// false when another instance already holds it and the caller must exit without starting.</summary>
    public bool Owns => _owns;

    /// <summary>True when the slot was acquired from a previous instance that left the mutex abandoned (it
    /// crashed / was killed without releasing) — the caller should log a warning but proceed.</summary>
    public bool WasAbandoned { get; private set; }

    /// <summary>
    /// Attempts to become the single running instance under <paramref name="mutexName"/>. Never throws for the
    /// expected "already held" / "abandoned" outcomes — inspect <see cref="Owns"/> / <see cref="WasAbandoned"/>.
    /// Must be paired with <see cref="Dispose"/> on the SAME thread (a mutex has thread affinity — the acquiring
    /// thread must release it), which the caller satisfies by disposing in the finally around the blocking
    /// <c>host.Run()</c>.
    /// </summary>
    public static SingleInstanceGuard TryAcquire(string mutexName)
    {
        var guard = new SingleInstanceGuard();
        guard._mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            /* WaitOne(0): take the mutex if free, otherwise return false immediately (another instance holds
               it) — never block. */
            guard._owns = guard._mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            /* A previous owner died without releasing — WaitOne throws but we NOW hold it. */
            guard._owns = true;
            guard.WasAbandoned = true;
        }

        if (!guard._owns)
        {
            /* We never owned it — dispose our handle now (nothing to release). */
            guard._mutex.Dispose();
            guard._mutex = null;
        }

        return guard;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mutex is null)
        {
            return;
        }

        if (_owns)
        {
            /* ReleaseMutex must run on the acquiring thread; the caller disposes on the same (main) thread it
               acquired on. Swallow the rare not-held/wrong-thread case on an abnormal exit path. */
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
