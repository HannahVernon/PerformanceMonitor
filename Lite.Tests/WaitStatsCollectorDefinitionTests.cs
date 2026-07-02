/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the cross-SKU parity contract of the extracted wait_stats collector definition
/// (headless plan v5.1): verbatim query text, ignored-wait filtering, delta groups/keys/gap
/// policy, and payload column order. An intentional change to any of these must consciously
/// update these tests — that is the point.
/// </summary>
public sealed class WaitStatsCollectorDefinitionTests
{
    private const string ExpectedQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    wait_type = ws.wait_type,
    waiting_tasks_count = ws.waiting_tasks_count,
    wait_time_ms = ws.wait_time_ms,
    signal_wait_time_ms = ws.signal_wait_time_ms
FROM sys.dm_os_wait_stats AS ws
WHERE ws.wait_time_ms > 0
OPTION(RECOMPILE);";

    [Fact]
    public void Query_IsTheVerbatimParityContract()
    {
        Assert.Equal(ExpectedQuery, WaitStatsCollector.Instance.Query);
    }

    [Fact]
    public void PayloadColumns_AreInAppendOrder()
    {
        var names = WaitStatsCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "wait_type",
                "waiting_tasks_count",
                "wait_time_ms",
                "signal_wait_time_ms",
                "delta_waiting_tasks",
                "delta_wait_time_ms",
                "delta_signal_wait_time_ms",
            },
            names);
    }

    [Fact]
    public async Task ReadAsync_MapsColumns_AndFiltersIgnoredWaitTypes()
    {
        using var reader = new FakeDataReader(
            new object[] { "SOS_SCHEDULER_YIELD", 10L, 200L, 150L },
            new object[] { "SLEEP_TASK", 5L, 100L, 50L },
            new object[] { "PAGEIOLATCH_SH", 7L, 300L, 20L });

        var context = MakeContext(new RecordingDeltaCalculator(), ignored: new[] { "SLEEP_TASK" });

        var rows = await WaitStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new WaitStatsCollector.Row("SOS_SCHEDULER_YIELD", 10, 200, 150), rows[0]);
        Assert.Equal(new WaitStatsCollector.Row("PAGEIOLATCH_SH", 7, 300, 20), rows[1]);
    }

    [Fact]
    public void WritePayload_EmitsPayloadOrder_AndPinsDeltaGroupsKeysAndGapPolicy()
    {
        var deltas = new RecordingDeltaCalculator();
        var context = MakeContext(deltas);
        var writer = new RecordingRowWriter();
        var row = new WaitStatsCollector.Row("PAGEIOLATCH_SH", 7, 300, 20);

        WaitStatsCollector.Instance.WritePayload(row, writer, context);

        /* Payload order: raw values then the three deltas (RecordingDeltaCalculator returns value * 10). */
        Assert.Equal(new object?[] { "PAGEIOLATCH_SH", 7L, 300L, 20L, 70L, 3000L, 200L }, writer.Values);

        /* Delta contract: group names, key = wait_type, the host collection time, 300 s gap policy. */
        Assert.Equal(3, deltas.Calls.Count);
        Assert.Equal(("wait_stats_tasks", "PAGEIOLATCH_SH", 7L, context.CollectionTime, 300), deltas.Calls[0]);
        Assert.Equal(("wait_stats_time", "PAGEIOLATCH_SH", 300L, context.CollectionTime, 300), deltas.Calls[1]);
        Assert.Equal(("wait_stats_signal", "PAGEIOLATCH_SH", 20L, context.CollectionTime, 300), deltas.Calls[2]);
        Assert.All(deltas.Calls, _ => Assert.Equal(42, deltas.LastServerId));
    }

    private static CollectorContext MakeContext(ICollectorDeltaCalculator deltas, IEnumerable<string>? ignored = null)
        => new()
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            Deltas = deltas,
            IgnoredWaitTypes = new HashSet<string>(ignored ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
        };

    private sealed class RecordingDeltaCalculator : ICollectorDeltaCalculator
    {
        public List<(string Group, string Key, long Value, DateTime? Time, int MaxGap)> Calls { get; } = new();

        public int LastServerId { get; private set; }

        public long CalculateDelta(int serverId, string collectorName, string key, long currentValue,
            DateTime? collectionTime = null, int maxGapSeconds = 0)
        {
            LastServerId = serverId;
            Calls.Add((collectorName, key, currentValue, collectionTime, maxGapSeconds));
            return currentValue * 10;
        }

        public long CalculateDeltaWithInterval(int serverId, string collectorName, string key, long currentValue,
            out int intervalSeconds, DateTime? collectionTime = null, int maxGapSeconds = 0)
        {
            intervalSeconds = 0;
            return CalculateDelta(serverId, collectorName, key, currentValue, collectionTime, maxGapSeconds);
        }
    }

    private sealed class RecordingRowWriter : ICollectorRowWriter
    {
        public List<object?> Values { get; } = new();

        public ICollectorRowWriter Value(string? value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(long value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(long? value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(int value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(int? value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(double value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(double? value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(decimal value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(decimal? value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(bool value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(bool? value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(DateTime value) { Values.Add(value); return this; }
        public ICollectorRowWriter Value(DateTime? value) { Values.Add(value); return this; }
        public ICollectorRowWriter NullValue() { Values.Add(null); return this; }
    }

    /// <summary>Minimal forward-only DbDataReader over in-memory rows (positional Gets only).</summary>
    private sealed class FakeDataReader : DbDataReader
    {
        private readonly object[][] _rows;
        private int _index = -1;

        public FakeDataReader(params object[][] rows) => _rows = rows;

        public override bool Read() => ++_index < _rows.Length;

        public override string GetString(int ordinal) => (string)_rows[_index][ordinal];

        public override long GetInt64(int ordinal) => (long)_rows[_index][ordinal];

        public override bool IsDBNull(int ordinal) => _rows[_index][ordinal] is DBNull;

        public override int FieldCount => _rows.Length == 0 ? 0 : _rows[0].Length;

        public override bool HasRows => _rows.Length > 0;

        public override bool IsClosed => false;

        public override int Depth => 0;

        public override int RecordsAffected => -1;

        public override object this[int ordinal] => _rows[_index][ordinal];

        public override object this[string name] => throw new NotSupportedException();

        public override object GetValue(int ordinal) => _rows[_index][ordinal];

        public override bool NextResult() => false;

        public override bool GetBoolean(int ordinal) => (bool)_rows[_index][ordinal];
        public override byte GetByte(int ordinal) => (byte)_rows[_index][ordinal];
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => (char)_rows[_index][ordinal];
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => (DateTime)_rows[_index][ordinal];
        public override decimal GetDecimal(int ordinal) => (decimal)_rows[_index][ordinal];
        public override double GetDouble(int ordinal) => (double)_rows[_index][ordinal];
        public override IEnumerator GetEnumerator() => _rows.GetEnumerator();
        public override Type GetFieldType(int ordinal) => _rows[_index][ordinal].GetType();
        public override float GetFloat(int ordinal) => (float)_rows[_index][ordinal];
        public override Guid GetGuid(int ordinal) => (Guid)_rows[_index][ordinal];
        public override short GetInt16(int ordinal) => (short)_rows[_index][ordinal];
        public override int GetInt32(int ordinal) => (int)_rows[_index][ordinal];
        public override string GetName(int ordinal) => throw new NotSupportedException();
        public override int GetOrdinal(string name) => throw new NotSupportedException();
        public override int GetValues(object[] values) => throw new NotSupportedException();
    }
}
