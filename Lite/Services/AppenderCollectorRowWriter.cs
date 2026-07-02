/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using DuckDB.NET.Data;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Adapts a DuckDB appender row to the shared <see cref="ICollectorRowWriter"/> contract so
/// extracted collector definitions write Lite's store without knowing DuckDB exists. Reused
/// across rows via <see cref="CurrentRow"/>; the host runner owns row begin/end.
/// </summary>
internal sealed class AppenderCollectorRowWriter : ICollectorRowWriter
{
    public IDuckDBAppenderRow? CurrentRow { get; set; }

    private IDuckDBAppenderRow Row => CurrentRow ?? throw new InvalidOperationException("CurrentRow not set");

    public ICollectorRowWriter Value(string? value) { Row.AppendValue(value); return this; }

    public ICollectorRowWriter Value(long value) { Row.AppendValue(value); return this; }

    public ICollectorRowWriter Value(long? value) { if (value.HasValue) { Row.AppendValue(value.Value); } else { Row.AppendNullValue(); } return this; }

    public ICollectorRowWriter Value(int value) { Row.AppendValue(value); return this; }

    public ICollectorRowWriter Value(int? value) { if (value.HasValue) { Row.AppendValue(value.Value); } else { Row.AppendNullValue(); } return this; }

    public ICollectorRowWriter Value(short value) { Row.AppendValue(value); return this; }

    public ICollectorRowWriter Value(short? value) { if (value.HasValue) { Row.AppendValue(value.Value); } else { Row.AppendNullValue(); } return this; }

    public ICollectorRowWriter Value(double value) { Row.AppendValue(value); return this; }

    public ICollectorRowWriter Value(double? value) { if (value.HasValue) { Row.AppendValue(value.Value); } else { Row.AppendNullValue(); } return this; }

    public ICollectorRowWriter Value(decimal value) { Row.AppendValue(value); return this; }

    public ICollectorRowWriter Value(decimal? value) { if (value.HasValue) { Row.AppendValue(value.Value); } else { Row.AppendNullValue(); } return this; }

    public ICollectorRowWriter Value(bool value) { Row.AppendValue(value); return this; }

    public ICollectorRowWriter Value(bool? value) { if (value.HasValue) { Row.AppendValue(value.Value); } else { Row.AppendNullValue(); } return this; }

    public ICollectorRowWriter Value(DateTime value) { Row.AppendValue(value); return this; }

    public ICollectorRowWriter Value(DateTime? value) { if (value.HasValue) { Row.AppendValue(value.Value); } else { Row.AppendNullValue(); } return this; }

    public ICollectorRowWriter NullValue() { Row.AppendNullValue(); return this; }
}
