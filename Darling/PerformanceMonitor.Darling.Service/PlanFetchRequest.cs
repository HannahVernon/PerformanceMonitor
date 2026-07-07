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
/// The parsed <c>fetch_plan</c> command payload an <c>args_json</c> carries (the viewer builds it, the service
/// parses it). Exactly one of the two handle modes is populated: <see cref="PlanHandle"/> for the query-grid
/// fetch (the currently-cached plan by plan_handle, via <c>PgPlanFetcher.FetchPlanXmlAsync</c>) or
/// <see cref="SqlHandle"/> + the statement offsets for the deadlock-graph / blocked-process fetch (those XE
/// payloads carry only a sql_handle per executionStack frame — no plan_handle — so they need the by-sql_handle
/// path, <c>PgPlanFetcher.FetchPlanBySqlHandleAsync</c>). Property names are camelCase to match the viewer's
/// serialization (parsed case-insensitively). This is a pure args model + validation
/// (<see cref="TryParse"/>) — the actual cache read is <c>PgPlanFetcher</c>'s, not duplicated here.
/// </summary>
public sealed record PlanFetchRequest(
    string? PlanHandle,
    string? SqlHandle,
    int StatementStartOffset,
    int StatementEndOffset,
    string? DatabaseName)
{
    /// <summary>True when at least one usable handle is present — the dispatch fails a request with neither.</summary>
    public bool HasHandle => !string.IsNullOrWhiteSpace(PlanHandle) || !string.IsNullOrWhiteSpace(SqlHandle);

    /// <summary>True when the plan_handle mode should run (it takes precedence when both are somehow present).</summary>
    public bool UsePlanHandle => !string.IsNullOrWhiteSpace(PlanHandle);

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parses <c>args_json</c> into a request, returning false for null/blank/malformed JSON or a request that
    /// carries neither handle. Statement offsets default to whole-statement (0 / -1) when absent. Never throws.
    /// </summary>
    public static bool TryParse(string? argsJson, out PlanFetchRequest request)
    {
        request = new PlanFetchRequest(null, null, 0, -1, null);
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

        var parsed = new PlanFetchRequest(
            NullIfBlank(dto.PlanHandle),
            NullIfBlank(dto.SqlHandle),
            dto.StatementStartOffset ?? 0,
            dto.StatementEndOffset ?? -1,
            NullIfBlank(dto.DatabaseName));

        if (!parsed.HasHandle)
        {
            return false;
        }

        request = parsed;
        return true;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The wire shape of <c>args_json</c> — offsets nullable so an absent pair defaults to whole-statement.</summary>
    private sealed class Dto
    {
        public string? PlanHandle { get; set; }
        public string? SqlHandle { get; set; }
        public int? StatementStartOffset { get; set; }
        public int? StatementEndOffset { get; set; }
        public string? DatabaseName { get; set; }
    }
}
