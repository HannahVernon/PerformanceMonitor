/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Text.Json;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The parsed <c>execute_actual_plan</c> command payload (the viewer builds it, the service parses it). It
/// carries an IDENTIFIER ONLY — the stored row key (<see cref="QueryHash"/> + <see cref="DatabaseName"/>) — and
/// deliberately NO SQL text. The service resolves the query text and estimated plan XML from its OWN store
/// (<c>query_stats</c>, by server_id + query_hash + database_name) before re-executing.
///
/// <para><b>Why identifier-only is a hard security requirement:</b> the target server id rides on the command
/// row and the query is RE-EXECUTED as the service's stored monitoring credential. If this payload could carry
/// SQL text, any writer of <c>config.config_command</c> could make the service run arbitrary SQL against every
/// monitored target — a privilege escalation through the command plane. Carrying only a key means the worst a
/// command writer can do is ask the service to re-run a query the collector ALREADY captured (and only a
/// read-write seat can enqueue at all). Property names are camelCase to match the viewer's serialization
/// (parsed case-insensitively). Pure args model + validation (<see cref="TryParse"/>); never throws.</para>
/// </summary>
public sealed record ActualPlanRequest(
    string? QueryHash,
    string? DatabaseName)
{
    /// <summary>True when the stored-row key is present — the dispatch fails a request without a query_hash.</summary>
    public bool HasIdentifier => !string.IsNullOrWhiteSpace(QueryHash);

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parses <c>args_json</c> into a request, returning false for null/blank/malformed JSON or a request that
    /// carries no query_hash. Never throws.
    /// </summary>
    public static bool TryParse(string? argsJson, out ActualPlanRequest request)
    {
        request = new ActualPlanRequest(null, null);
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return false;
        }

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(argsJson, s_options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (dto is null)
        {
            return false;
        }

        var parsed = new ActualPlanRequest(
            NullIfBlank(dto.QueryHash),
            NullIfBlank(dto.DatabaseName));

        if (!parsed.HasIdentifier)
        {
            return false;
        }

        request = parsed;
        return true;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The wire shape of <c>args_json</c> — an identifier only; NO query text field exists by design.</summary>
    private sealed class Dto
    {
        public string? QueryHash { get; set; }
        public string? DatabaseName { get; set; }
    }
}
