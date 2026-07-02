/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// M0 scaffold marker. SchemaVersion 0 means no Postgres schema exists yet; the first real
/// migration bumps it to 1, and the service applies pending versioned SQL migrations on startup
/// (headless plan: "plain versioned SQL scripts the service applies on startup").
/// </summary>
public static class StorageVersion
{
    public const int SchemaVersion = 0;
}
