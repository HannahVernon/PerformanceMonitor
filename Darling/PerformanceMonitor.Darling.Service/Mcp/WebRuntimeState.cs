/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// The live web-dashboard enable/port state, published by the WORKER (the control-plane owner) and observed
/// by <see cref="DarlingWebHostService"/>'s supervisor loop (#1562) — the exact twin of
/// <see cref="McpRuntimeState"/> for the second Kestrel host. This is the seam that makes the viewer's
/// Settings toggle (config_service.web_enabled / web_port) take effect WITHOUT a service restart: the
/// worker's reload beacon applies the store row to the live config every ~15 seconds and publishes it here;
/// the web host polls and starts/stops/rebinds its inner web app to match.
///
/// <para>Before the first <see cref="Publish"/> (worker still bootstrapping, or the store and file both
/// unreadable) the host falls back to the file-loaded values. The LAN-exposure block (web.network) is
/// deliberately NOT carried here: network exposure stays file-defined and restart-only (changing an
/// exposure surface should require touching the host), but the store toggle still acts as an instant kill
/// switch for an exposed dashboard.</para>
///
/// <para>Thread-safety: one writer (the worker's sweep thread), one reader (the web host's supervisor).
/// State is swapped as one immutable record reference, so the reader always sees a coherent
/// (Enabled, Port) pair — never a torn mix of old and new.</para>
/// </summary>
public sealed class WebRuntimeState
{
    /// <summary>A coherent published snapshot; null until the worker first publishes.</summary>
    public sealed record Snapshot(bool Enabled, int Port);

    private volatile Snapshot? _current;

    /// <summary>Publishes the live control-plane values (worker only; called at startup and on every reload).</summary>
    public void Publish(bool enabled, int port) => _current = new Snapshot(enabled, port);

    /// <summary>The latest published snapshot, or null when the worker has not published yet.</summary>
    public Snapshot? Read() => _current;
}
