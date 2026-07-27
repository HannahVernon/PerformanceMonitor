/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    private static string[]? s_perfmonCounterOverride;
    private static bool s_perfmonCounterOverrideLoaded;

    /// <summary>
    /// Host-side perfmon counter override from perfmon_counters.json (per-user config). Returns
    /// null when absent/invalid so the shared definition's curated default list applies. Cached
    /// after the first load, matching the original collector's one-time read.
    /// </summary>
    private static string[]? GetPerfmonCounterOverride()
    {
        if (s_perfmonCounterOverrideLoaded)
        {
            return s_perfmonCounterOverride;
        }

        var configPath = Path.Combine(App.ConfigDirectory, "perfmon_counters.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<PerfmonCounterConfig>(json);
                if (config?.Counters?.Length > 0)
                {
                    s_perfmonCounterOverride = config.Counters;
                }
            }
            catch { /* fall through to the definition's defaults */ }
        }

        s_perfmonCounterOverrideLoaded = true;
        return s_perfmonCounterOverride;
    }

    private class PerfmonCounterConfig
    {
        public string[] Counters { get; set; } = [];
    }

    /// <summary>
    /// Collects key performance counters via the shared <see cref="PerfmonStatsCollector"/>
    /// definition (the curated default counter list, the literal-escape interpolation, and the
    /// "{object}|{counter}|{instance}" delta contract live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectPerfmonStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(PerfmonStatsCollector.Instance, server, cancellationToken);
}
