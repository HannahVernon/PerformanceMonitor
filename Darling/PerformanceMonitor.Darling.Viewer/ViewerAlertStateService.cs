/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Viewer-local per-server alert ACKNOWLEDGEMENT state — the "ack-until-worse" half of Lite's
/// <c>AlertStateService</c> (Lite/Services/AlertStateService.cs), ported for the headless viewer's
/// per-server attention badge. State is persisted to a small JSON file so an acknowledgement survives
/// a restart, exactly like Lite. Thread-safe: every operation is guarded by <c>_lock</c>.
///
/// <para><b>Why only the ack half.</b> Lite's <c>AlertStateService</c> tracks two suppressions — a whole-server
/// SILENCE and a per-server ACKNOWLEDGEMENT. In the headless viewer the silence half is already handled a
/// better way: "Silence This Server" writes a store-side whole-server mute rule
/// (<see cref="ViewerDataService.BuildServerSilenceRule"/>) the running Darling service honors, so a silenced
/// server's later alerts come back flagged <c>muted</c> and the badge derivation
/// (<see cref="ServerAttentionDeriver"/>) drops them — no viewer-local silence flag needed. Acknowledgement,
/// by contrast, is genuinely viewer-local: it quiets the at-a-glance badge for an operator who has SEEN the
/// alerts, WITHOUT stopping the service's email/Teams/Slack delivery. So this service ports the acknowledgement
/// state machine only, keyed on the Postgres <c>server_id</c> (int) instead of Lite's storage-name GUID.</para>
///
/// <para><b>Ack-until-worse.</b> An acknowledgement records the wall-clock instant it was taken; the badge stays
/// suppressed for that server until a genuinely NEWER alert arrives (a row whose <c>alert_time</c> is later than
/// the ack), at which point <see cref="UpdateAlertState"/> clears it and the badge re-lights. This is the same
/// "Dismiss = until it worsens or recurs" spirit as the tray toast path — but a different UI surface (a persistent
/// per-server badge, not a transient toast), so it is a separate, small store, exactly as Lite keeps
/// <c>AlertStateService</c> separate from its toast/deliverer plumbing.</para>
/// </summary>
public sealed class ViewerAlertStateService
{
    private readonly object _lock = new();
    private readonly string _stateFilePath;
    private readonly Func<DateTime> _nowUtc;

    /* Acknowledged servers: server_id -> the UTC instant of acknowledgement. An alert row older than
       that instant stays suppressed; a newer row clears the entry (see UpdateAlertState). */
    private readonly Dictionary<int, DateTime> _acknowledged = new();

    /// <summary>Raised when an acknowledgement is taken or cleared, so a host can re-render immediately.</summary>
    public event EventHandler? SuppressionStateChanged;

    /// <param name="stateFilePath">
    /// Override the on-disk location (tests pass a unique temp file so their state never collides); null uses
    /// <see cref="DefaultFilePath"/>.
    /// </param>
    /// <param name="nowUtc">Injectable clock for deterministic ack-until-worse tests; null uses <see cref="DateTime.UtcNow"/>.</param>
    public ViewerAlertStateService(string? stateFilePath = null, Func<DateTime>? nowUtc = null)
    {
        _stateFilePath = stateFilePath ?? DefaultFilePath();
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        Load();
    }

    /// <summary>%APPDATA%\PerformanceMonitorDarling\alert-ack-state.json (the viewer's per-user data dir).</summary>
    public static string DefaultFilePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PerformanceMonitorDarling");
        return Path.Combine(directory, "alert-ack-state.json");
    }

    /// <summary>The resolved state-file path (surfaced mainly so tests and diagnostics can name it).</summary>
    public string FilePath => _stateFilePath;

    /// <summary>Whether alerts should surface (badge shown) for a server — false only while acknowledged.</summary>
    public bool ShouldShowAlerts(int serverId)
    {
        lock (_lock)
        {
            return !_acknowledged.ContainsKey(serverId);
        }
    }

    /// <summary>True while the server's alerts are acknowledged (badge suppressed).</summary>
    public bool IsAcknowledged(int serverId)
    {
        lock (_lock)
        {
            return _acknowledged.ContainsKey(serverId);
        }
    }

    /// <summary>
    /// Applies this poll's derived alert state for a server and returns whether its badge should show.
    /// Clears the acknowledgement first when <paramref name="latestEventTimeUtc"/> is newer than the ack
    /// timestamp — meaning a genuinely new alert arrived (the "until it worsens or recurs" clear), not just a
    /// re-read of the same rows. Mirrors Lite's <c>AlertStateService.UpdateAlertCounts</c>.
    /// </summary>
    public bool UpdateAlertState(int serverId, int alertCount, DateTime? latestEventTimeUtc)
    {
        lock (_lock)
        {
            if (latestEventTimeUtc.HasValue
                && _acknowledged.TryGetValue(serverId, out var ackTime)
                && latestEventTimeUtc.Value > ackTime)
            {
                /* A newer alert than the acknowledgement — clear it so the badge re-lights. */
                _acknowledged.Remove(serverId);
                Save();
            }

            return alertCount > 0 && !_acknowledged.ContainsKey(serverId);
        }
    }

    /// <summary>
    /// Acknowledges a server's alerts and suppresses its badge until a newer alert arrives. Persisted across
    /// restarts. Mirrors Lite's <c>AlertStateService.AcknowledgeAlert</c>.
    /// </summary>
    public void AcknowledgeAlert(int serverId)
    {
        lock (_lock)
        {
            _acknowledged[serverId] = _nowUtc();
            Save();
        }
        SuppressionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clears a server's acknowledgement outright (the badge re-lights on the next poll if alerts remain). The
    /// counterpart to Lite's <c>ClearAcknowledgementForNewCondition</c> — the viewer's alert rows always carry an
    /// <c>alert_time</c>, so <see cref="UpdateAlertState"/> normally clears the ack on its own; this is the
    /// explicit escape hatch. No-op (and no event) when nothing was acknowledged.
    /// </summary>
    public void ClearAcknowledgement(int serverId)
    {
        bool changed;
        lock (_lock)
        {
            changed = _acknowledged.Remove(serverId);
            if (changed)
            {
                Save();
            }
        }

        if (changed)
        {
            SuppressionStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Removes all state for a server (call when its tab is closed / it is unregistered).</summary>
    public void RemoveServerState(int serverId)
    {
        lock (_lock)
        {
            if (_acknowledged.Remove(serverId))
            {
                Save();
            }
        }
    }

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true
    };

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new AlertAckPersisted
            {
                AcknowledgedAlerts = new Dictionary<int, DateTime>(_acknowledged)
            };
            var json = JsonSerializer.Serialize(state, s_writeOptions);
            File.WriteAllText(_stateFilePath, json);
        }
        catch
        {
            /* Best effort — an ack that can't be persisted still holds in memory for the session. */
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<AlertAckPersisted>(json);
            if (state?.AcknowledgedAlerts == null)
            {
                return;
            }

            foreach (var kv in state.AcknowledgedAlerts)
            {
                _acknowledged[kv.Key] = kv.Value;
            }
        }
        catch
        {
            /* Best effort — start fresh if the file is missing or corrupted. */
        }
    }

    private sealed class AlertAckPersisted
    {
        public Dictionary<int, DateTime>? AcknowledgedAlerts { get; set; }
    }
}
