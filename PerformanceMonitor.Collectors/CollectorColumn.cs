/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Logical column types for collector payload columns — engine-neutral names each host maps to
/// its own storage types (Lite/DuckDB and Darling/Postgres both have direct equivalents).
/// </summary>
public enum CollectorColumnType
{
    BigInt,
    Integer,
    SmallInt,
    Varchar,
    Timestamp,
    Double,
    Decimal,
    Boolean,
}

/// <summary>
/// One payload column a collector definition emits, in emission order. The host writes its
/// standard prefix columns first; these describe everything after the prefix.
/// </summary>
public sealed class CollectorColumn
{
    public CollectorColumn(string name, CollectorColumnType type)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; }

    public CollectorColumnType Type { get; }
}
