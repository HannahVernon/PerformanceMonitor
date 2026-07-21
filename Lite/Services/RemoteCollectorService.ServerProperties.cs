/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Collects server edition/version/hardware metadata via the shared
    /// <see cref="ServerPropertiesCollector"/> definition (Azure edition/tier naming, the vCore
    /// parse, and the best-effort WS5 health probe live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectServerPropertiesAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(ServerPropertiesCollector.Instance, server, cancellationToken);
}
