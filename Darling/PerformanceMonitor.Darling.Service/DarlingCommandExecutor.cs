/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The service side of the store&lt;-&gt;service COMMAND plane (Stage 2): the Viewer (Stage 3) enqueues rows
/// into <c>config.config_command</c>; this executor CLAIMS one at a time, EXECUTES it, and REPORTS the
/// terminal outcome back on the same row so the Viewer can poll for the result (esp. <c>test_connect</c>'s
/// probe facts). Mirrors <see cref="StoreConfigProvider"/>'s style: pure, testable dispatch
/// (<see cref="ResolvePlan"/>) separated from the store I/O, failure-isolated (a bad command is reported
/// <c>failed</c>, never thrown out of the poller).
///
/// <para>The claim is safe against multiple service instances: <see cref="ClaimCommandSql"/> selects the
/// oldest <c>pending</c> row <c>FOR UPDATE SKIP LOCKED</c>, so two services polling the same store each
/// grab a DIFFERENT row (or none) instead of racing the same one. The desired-state commands
/// (pause/resume/enable/disable/reload) only WRITE the <c>config.*</c> tables — the bump trigger fires the
/// <c>config_version</c> reload beacon and the worker's existing reconcile applies them; the executor never
/// mutates the live collection loop's state directly. The imperative commands that DO need the running loop
/// (<c>snapshot_now</c>/<c>analyze_now</c>) are delegated to the worker through
/// <see cref="IDarlingCommandHost"/>.</para>
/// </summary>
public sealed class DarlingCommandExecutor
{
    /// <summary>
    /// The atomic claim (the correctness-critical piece): mark the OLDEST <c>pending</c> row
    /// <c>in_progress</c> and RETURN it, guarded by <c>FOR UPDATE SKIP LOCKED</c> in the sub-select so
    /// concurrent service instances never claim the same row. Public const so a test can pin the shape.
    /// </summary>
    public const string ClaimCommandSql = @"
UPDATE config.config_command
SET status = 'in_progress',
    claimed_at = now() AT TIME ZONE 'UTC',
    service_instance = $1
WHERE command_id = (
    SELECT command_id
    FROM config.config_command
    WHERE status = 'pending'
    ORDER BY command_id
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
RETURNING command_id, command_type, target_server_id, args_json, requested_by";

    /// <summary>
    /// Writes the terminal outcome back on the claimed row (succeeded/failed + result_status/json). Internal
    /// so the round-trip test can pin + exercise it; $1 command_id, $2 status, $3 result_status, $4 result_json.
    /// </summary>
    internal const string ReportCommandSql = @"
UPDATE config.config_command
SET status = $2,
    completed_at = now() AT TIME ZONE 'UTC',
    result_status = $3,
    result_json = $4
WHERE command_id = $1";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly NpgsqlDataSource _postgres;
    private readonly IDarlingCommandHost _host;
    private readonly string _serviceInstance;
    private readonly ILogger? _logger;

    public DarlingCommandExecutor(NpgsqlDataSource postgres, IDarlingCommandHost host, string serviceInstance, ILogger? logger = null)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _serviceInstance = serviceInstance ?? throw new ArgumentNullException(nameof(serviceInstance));
        _logger = logger;
    }

    /// <summary>
    /// Claims and executes at most one pending command, reporting its outcome. Returns true if a command was
    /// claimed (so the caller can drain a backlog by looping until it returns false). Store-unreachable and
    /// any per-command failure are swallowed (logged) — the poller must never die for one bad tick.
    /// </summary>
    public async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        ClaimedCommand? claimed;
        try
        {
            claimed = await ClaimNextAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning("Could not poll the command queue — will retry next tick: {Message}", ex.Message);
            return false;
        }

        if (claimed is null)
        {
            return false;
        }

        await ExecuteAndReportAsync(claimed, cancellationToken);
        return true;
    }

    /// <summary>Atomically claims the oldest pending command (or null when the queue is empty).</summary>
    public async Task<ClaimedCommand?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(ClaimCommandSql, connection);
        command.Parameters.AddWithValue(_serviceInstance);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimedCommand(
            CommandId: reader.GetInt64(0),
            CommandType: reader.GetString(1),
            TargetServerId: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ArgsJson: reader.IsDBNull(3) ? null : reader.GetFieldValue<string>(3),
            RequestedBy: reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    /// <summary>
    /// Executes one claimed command and writes its terminal result. Never throws out (an unexpected
    /// exception is caught and reported as <c>failed</c>/<c>error</c>), so the poller keeps running.
    /// </summary>
    public async Task ExecuteAndReportAsync(ClaimedCommand command, CancellationToken cancellationToken)
    {
        CommandOutcome outcome;
        try
        {
            outcome = await RunAsync(command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Command {Id} ({Type}) failed: {Message}", command.CommandId, command.CommandType, ex.Message);
            outcome = new CommandOutcome(false, "error", ErrorJson(ex.Message));
        }

        await ReportAsync(command.CommandId, outcome, cancellationToken);
    }

    private async Task<CommandOutcome> RunAsync(ClaimedCommand command, CancellationToken cancellationToken)
    {
        var plan = ResolvePlan(command);
        switch (plan.Kind)
        {
            case CommandKind.Fail:
                _logger?.LogWarning("Command {Id}: {Reason} (type '{Type}')", command.CommandId, plan.FailReason, command.CommandType);
                return new CommandOutcome(false, plan.FailReason!, ErrorJson(plan.FailReason!));

            case CommandKind.StoreWrite:
                await ExecuteStoreWriteAsync(plan, cancellationToken);
                _logger?.LogInformation("Command {Id} ({Type}) => {Result}", command.CommandId, command.CommandType, plan.SuccessStatus);
                return new CommandOutcome(true, plan.SuccessStatus, DetailJson(plan.SuccessStatus));

            case CommandKind.Probe:
            {
                var server = DeserializeServer(command.ArgsJson!);
                if (server is null)
                {
                    return new CommandOutcome(false, "invalid args_json", ErrorJson("test_connect args_json did not deserialize to a server definition"));
                }

                var probe = await DarlingServerConnector.ProbeAsync(server, _logger, cancellationToken);
                var (resultStatus, resultJson) = MapProbeResult(probe);
                _logger?.LogInformation("Command {Id} (test_connect '{Server}') => {Result}", command.CommandId, server.DisplayName, resultStatus);
                return new CommandOutcome(probe.Success, resultStatus, resultJson);
            }

            case CommandKind.Snapshot:
                return await _host.SnapshotNowAsync(command.TargetServerId!.Value, cancellationToken);

            case CommandKind.Analyze:
                return await _host.AnalyzeNowAsync(command.TargetServerId!.Value, cancellationToken);

            default:
                return new CommandOutcome(false, "unknown command_type", ErrorJson("unknown command_type"));
        }
    }

    /// <summary>
    /// PURE dispatch: maps a claimed command to a plan without touching the store, so every branch — incl.
    /// the argument-validation failures and the unknown-type fallback — is unit-testable. Store-write
    /// commands carry their parameterized SQL (the toggled boolean is a compile-time literal, never user
    /// input; every value is a bound parameter); the imperative/probe commands carry only a kind.
    /// </summary>
    internal static CommandPlan ResolvePlan(ClaimedCommand command)
    {
        switch ((command.CommandType ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "pause":
                return StoreWrite("UPDATE config.config_service SET paused = TRUE WHERE id = 1", null, "paused");

            case "resume":
                return StoreWrite("UPDATE config.config_service SET paused = FALSE WHERE id = 1", null, "resumed");

            case "reload":
                /* No state change — an UPDATE bumps config_version via the self-bump trigger, forcing the
                   worker's next-sweep reload (an explicit resync). */
                return StoreWrite("UPDATE config.config_service SET updated_at = now() AT TIME ZONE 'UTC' WHERE id = 1", null, "reload signaled");

            case "enable_server":
                return command.TargetServerId is int enableId
                    ? StoreWrite(
                        "UPDATE config.config_monitored_servers SET is_enabled = TRUE, modified_at = now() AT TIME ZONE 'UTC' WHERE server_id = $1",
                        new object?[] { enableId }, "server enabled")
                    : Fail("enable_server requires target_server_id");

            case "disable_server":
                return command.TargetServerId is int disableId
                    ? StoreWrite(
                        "UPDATE config.config_monitored_servers SET is_enabled = FALSE, modified_at = now() AT TIME ZONE 'UTC' WHERE server_id = $1",
                        new object?[] { disableId }, "server disabled")
                    : Fail("disable_server requires target_server_id");

            case "enable_collector":
                return ResolveCollectorToggle(command, enabled: true);

            case "disable_collector":
                return ResolveCollectorToggle(command, enabled: false);

            case "test_connect":
                return string.IsNullOrWhiteSpace(command.ArgsJson)
                    ? Fail("test_connect requires args_json with the server definition")
                    : new CommandPlan(CommandKind.Probe, null, null, "connected", null);

            case "snapshot_now":
                return command.TargetServerId is int
                    ? new CommandPlan(CommandKind.Snapshot, null, null, "snapshot complete", null)
                    : Fail("snapshot_now requires target_server_id");

            case "analyze_now":
                return command.TargetServerId is int
                    ? new CommandPlan(CommandKind.Analyze, null, null, "analysis complete", null)
                    : Fail("analyze_now requires target_server_id");

            default:
                return Fail("unknown command_type");
        }
    }

    /// <summary>
    /// enable/disable_collector -> upsert <c>config_collector_schedules.enabled</c> for the collector named in
    /// args_json, scoped to <c>target_server_id</c> (else args_json.server_id, else fleet-wide when both are
    /// absent). Touches ONLY the enabled column (ON CONFLICT DO UPDATE), so an existing frequency/retention
    /// override is preserved. The two ON CONFLICT arbiters match V17's partial unique indexes (a PK cannot
    /// span the nullable server_id).
    /// </summary>
    private static CommandPlan ResolveCollectorToggle(ClaimedCommand command, bool enabled)
    {
        var verb = enabled ? "enable_collector" : "disable_collector";
        var collectorName = TryReadString(command.ArgsJson, "collector_name", "collectorName");
        if (string.IsNullOrWhiteSpace(collectorName))
        {
            return Fail($"{verb} requires args_json.collector_name");
        }

        if (!CollectorScheduleDefaults.All.ContainsKey(collectorName))
        {
            return Fail($"{verb}: unknown collector '{collectorName}'");
        }

        var flag = enabled ? "TRUE" : "FALSE";
        var successStatus = enabled ? "collector enabled" : "collector disabled";
        var serverId = command.TargetServerId ?? TryReadInt(command.ArgsJson, "server_id", "serverId");

        if (serverId is int scopedId)
        {
            return StoreWrite(
                "INSERT INTO config.config_collector_schedules (server_id, collector_name, enabled) VALUES ($1, $2, " + flag + ") " +
                "ON CONFLICT (server_id, collector_name) WHERE server_id IS NOT NULL DO UPDATE SET enabled = EXCLUDED.enabled",
                new object?[] { scopedId, collectorName }, successStatus);
        }

        return StoreWrite(
            "INSERT INTO config.config_collector_schedules (server_id, collector_name, enabled) VALUES (NULL, $1, " + flag + ") " +
            "ON CONFLICT (collector_name) WHERE server_id IS NULL DO UPDATE SET enabled = EXCLUDED.enabled",
            new object?[] { collectorName }, successStatus + " (fleet-wide)");
    }

    /// <summary>test_connect result mapping (pure): success carries the probe facts, failure the error.</summary>
    internal static (string ResultStatus, string ResultJson) MapProbeResult(ConnectionProbeResult probe)
    {
        if (probe.Success)
        {
            var json = JsonSerializer.Serialize(new
            {
                success = true,
                majorVersion = probe.MajorVersion,
                engineEdition = probe.EngineEdition,
                engineEditionDescription = probe.EngineEditionDescription,
                isAzureSqlDb = probe.IsAzureSqlDb,
                isAzureManagedInstance = probe.IsAzureManagedInstance,
                isAwsRds = probe.IsAwsRds,
                hasMsdbAccess = probe.HasMsdbAccess,
            });
            return ("connected", json);
        }

        return ("connection_failed", ErrorJson(probe.Error ?? "connection failed"));
    }

    private async Task ExecuteStoreWriteAsync(CommandPlan plan, CancellationToken cancellationToken)
    {
        await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(plan.Sql, connection);
        if (plan.Parameters is not null)
        {
            foreach (var parameter in plan.Parameters)
            {
                command.Parameters.AddWithValue(parameter ?? (object)DBNull.Value);
            }
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReportAsync(long commandId, CommandOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(ReportCommandSql, connection);
            command.Parameters.AddWithValue(commandId);
            command.Parameters.AddWithValue(outcome.Success ? "succeeded" : "failed");
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)outcome.ResultStatus ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)outcome.ResultJson ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Failure-isolated: a store blip writing the result must not kill the poller; the row stays
               in_progress and a later reader can see it never terminated. */
            _logger?.LogWarning("Could not write result for command {Id}: {Message}", commandId, ex.Message);
        }
    }

    private static MonitoredServer? DeserializeServer(string argsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<MonitoredServer>(argsJson, s_jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadString(string? json, params string[] names)
    {
        var value = TryReadProperty(json, names);
        return value.HasValue && value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    private static int? TryReadInt(string? json, params string[] names)
    {
        var value = TryReadProperty(json, names);
        return value.HasValue && value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var i) ? i : null;
    }

    private static JsonElement? TryReadProperty(string? json, string[] names)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in names)
            {
                if (document.RootElement.TryGetProperty(name, out var element))
                {
                    return element.Clone();
                }
            }
        }
        catch (JsonException)
        {
            /* Malformed args_json — the caller degrades to "missing". */
        }

        return null;
    }

    private static CommandPlan StoreWrite(string sql, object?[]? parameters, string successStatus) =>
        new(CommandKind.StoreWrite, sql, parameters, successStatus, null);

    private static CommandPlan Fail(string reason) =>
        new(CommandKind.Fail, null, null, string.Empty, reason);

    private static string ErrorJson(string error) => JsonSerializer.Serialize(new { success = false, error });

    private static string DetailJson(string detail) => JsonSerializer.Serialize(new { success = true, detail });
}

/// <summary>A row claimed from <c>config.config_command</c>.</summary>
public sealed record ClaimedCommand(long CommandId, string CommandType, int? TargetServerId, string? ArgsJson, string? RequestedBy);

/// <summary>The terminal outcome written back on a command row.</summary>
public sealed record CommandOutcome(bool Success, string ResultStatus, string? ResultJson);

/// <summary>How a command is carried out — the four execution shapes plus the fail-fast fallback.</summary>
public enum CommandKind
{
    /// <summary>A parameterized write to the <c>config.*</c> tables (the bump trigger drives the reload).</summary>
    StoreWrite,

    /// <summary><c>test_connect</c>: deserialize args_json, probe the server, report the facts/error.</summary>
    Probe,

    /// <summary><c>snapshot_now</c>: run the running loop's collectors for a server now (via the host).</summary>
    Snapshot,

    /// <summary><c>analyze_now</c>: force an immediate analysis pass for a server now (via the host).</summary>
    Analyze,

    /// <summary>Bad arguments or an unknown command_type — report failed without touching the store.</summary>
    Fail,
}

/// <summary>The resolved (pure) plan for one command — see <see cref="DarlingCommandExecutor.ResolvePlan"/>.</summary>
public sealed record CommandPlan(CommandKind Kind, string? Sql, object?[]? Parameters, string SuccessStatus, string? FailReason);

/// <summary>
/// The worker's callback surface for the two imperative commands that need the LIVE collection loop:
/// <c>snapshot_now</c> (run a server's collectors now) and <c>analyze_now</c> (force an analysis pass now).
/// Kept an interface so the executor's dispatch is testable with a fake host, and so the executor never
/// reaches into the worker's mutable loop state directly.
/// </summary>
public interface IDarlingCommandHost
{
    Task<CommandOutcome> SnapshotNowAsync(int serverId, CancellationToken cancellationToken);

    Task<CommandOutcome> AnalyzeNowAsync(int serverId, CancellationToken cancellationToken);
}
