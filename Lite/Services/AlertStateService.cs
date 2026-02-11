/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace PerformanceMonitorLite.Services
{
    /// <summary>
    /// Manages alert state including suppression and acknowledgement for server tab badges.
    /// Thread-safe: All HashSet operations are protected by _lock.
    /// </summary>
    public class AlertStateService
    {
        private readonly object _lock = new object();

        // Suppression state (session-only for Lite - not persisted)
        private readonly HashSet<string> _silencedServers;

        // Acknowledged alerts (session-only, clears on next refresh with new data)
        private readonly HashSet<string> _acknowledgedAlerts;

        // Event for when suppression state changes
        public event EventHandler? SuppressionStateChanged;

        public AlertStateService()
        {
            _silencedServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _acknowledgedAlerts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines if alerts should be shown for a specific server.
        /// </summary>
        public bool ShouldShowAlerts(string serverId)
        {
            lock (_lock)
            {
                // Check if server is silenced
                if (_silencedServers.Contains(serverId))
                    return false;

                // Check if acknowledged this refresh cycle
                if (_acknowledgedAlerts.Contains(serverId))
                    return false;

                return true;
            }
        }

        /// <summary>
        /// Acknowledges alerts for a specific server (hides badge until next refresh with new data).
        /// </summary>
        public void AcknowledgeAlert(string serverId)
        {
            lock (_lock)
            {
                _acknowledgedAlerts.Add(serverId);
            }
            SuppressionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Clears acknowledgement for a server (called when new alert data arrives).
        /// </summary>
        public void ClearAcknowledgement(string serverId)
        {
            lock (_lock)
            {
                _acknowledgedAlerts.Remove(serverId);
            }
        }

        /// <summary>
        /// Silences a server entirely (no badges until unsilenced).
        /// </summary>
        public void SilenceServer(string serverId)
        {
            lock (_lock)
            {
                _silencedServers.Add(serverId);
            }
            SuppressionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Unsilences a server.
        /// </summary>
        public void UnsilenceServer(string serverId)
        {
            lock (_lock)
            {
                _silencedServers.Remove(serverId);
            }
            SuppressionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Checks if a server is silenced.
        /// </summary>
        public bool IsServerSilenced(string serverId)
        {
            lock (_lock)
            {
                return _silencedServers.Contains(serverId);
            }
        }

        /// <summary>
        /// Removes all state for a server (call when server tab is closed).
        /// </summary>
        public void RemoveServerState(string serverId)
        {
            lock (_lock)
            {
                _silencedServers.Remove(serverId);
                _acknowledgedAlerts.Remove(serverId);
            }
        }
    }
}
