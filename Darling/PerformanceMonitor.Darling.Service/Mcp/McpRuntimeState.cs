/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// The live MCP enable/port state, published by the WORKER (the control-plane owner) and observed by
/// <see cref="DarlingMcpHostService"/>'s supervisor loop (#1560). This is the seam that makes the
/// viewer's Settings toggle (config_service.mcp_enabled / mcp_port) take effect WITHOUT a service
/// restart: the worker's reload beacon applies the store row to the live config every ~15 seconds and
/// publishes it here; the MCP host polls and starts/stops/rebinds its inner web app to match.
///
/// <para>Before the first <see cref="Publish"/> (worker still bootstrapping, or the store and file both
/// unreadable) the host falls back to the file-loaded values — exactly the pre-#1560 behavior. The
/// LAN-exposure block (mcp.network) is deliberately NOT carried here: network exposure stays
/// file-defined and restart-only (changing an exposure surface should require touching the host), but
/// the store toggle still acts as an instant kill switch for an exposed server.</para>
///
/// <para>Thread-safety: one writer (the worker's sweep thread), one reader (the MCP host's supervisor).
/// State is swapped as one immutable record reference, so the reader always sees a coherent
/// (Enabled, Port) pair — never a torn mix of old and new.</para>
/// </summary>
public sealed class McpRuntimeState
{
    /// <summary>A coherent published snapshot; null until the worker first publishes.</summary>
    public sealed record Snapshot(bool Enabled, int Port);

    private volatile Snapshot? _current;

    /// <summary>Publishes the live control-plane values (worker only; called at startup and on every reload).</summary>
    public void Publish(bool enabled, int port) => _current = new Snapshot(enabled, port);

    /// <summary>The latest published snapshot, or null when the worker has not published yet.</summary>
    public Snapshot? Read() => _current;
}
