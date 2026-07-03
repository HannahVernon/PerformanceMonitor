/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The service's configuration file (darling.json): the Postgres store and the monitored
/// servers. Headless plan D-M2-1 — deliberately minimal: no schedule knobs (the shared
/// CollectorScheduleDefaults apply; defaults over speculative config), integrated auth
/// recommended, SQL-auth passwords DPAPI-protected via the service's --encrypt-password verb.
/// Resolution order: explicit path → DARLING_CONFIG environment variable → darling.json next
/// to the service binary.
/// </summary>
public sealed class DarlingConfig
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [JsonPropertyName("postgres")]
    public PostgresConfig Postgres { get; set; } = new();

    [JsonPropertyName("servers")]
    public List<MonitoredServer> Servers { get; set; } = new();

    /// <summary>
    /// Capture execution-plan text into query_stats.query_plan_xml and
    /// query_store_stats.query_plan_text. Default TRUE for Darling: PostgreSQL TOAST compresses the
    /// plan text transparently (pglz), and TimescaleDB chunk compression squeezes it further, so
    /// plans are cheap to keep — unlike Lite, which stores to DuckDB/parquet and deliberately never
    /// captures them. Set false to skip plan capture (e.g. to shave storage across a very large
    /// fleet). Feeds <see cref="CollectorContext.CapturePlanXml"/> in the shared query_stats /
    /// query_store collectors.
    /// </summary>
    [JsonPropertyName("capturePlans")]
    public bool CapturePlans { get; set; } = true;

    /// <summary>
    /// The shared alert engine's enabled flags and thresholds (Phase-5 slice D). Every default
    /// mirrors Lite's <c>App.*</c> alert defaults exactly, so an empty section alerts like a
    /// fresh Lite install. Optional — omit it entirely for the defaults.
    /// </summary>
    [JsonPropertyName("alerts")]
    public AlertsConfig Alerts { get; set; } = new();

    /// <summary>
    /// SMTP delivery for fired alerts. Delivery is enabled when host + from + to are all set
    /// (no separate flag — defaults over speculative config); the password uses the same DPAPI
    /// --encrypt-password pattern as SQL auth. Optional.
    /// </summary>
    [JsonPropertyName("smtp")]
    public SmtpConfig Smtp { get; set; } = new();

    /// <summary>
    /// Teams/Slack incoming-webhook delivery for fired alerts. A channel is enabled when its
    /// URL is set. Optional.
    /// </summary>
    [JsonPropertyName("webhooks")]
    public WebhooksConfig Webhooks { get; set; } = new();

    /// <summary>
    /// The embedded MCP server (analysis slice AN4): the six analysis tools — the same tool
    /// surface Lite and the Dashboard expose — over Streamable HTTP on localhost. Default OFF:
    /// a headless service should not open a local port unless the operator asks for it (both
    /// apps default their MCP servers off too). Optional — omit the section entirely.
    /// </summary>
    [JsonPropertyName("mcp")]
    public McpConfig Mcp { get; set; } = new();

    public static string ResolveConfigPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("DARLING_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return Path.Combine(AppContext.BaseDirectory, "darling.json");
    }

    public static DarlingConfig Load(string? explicitPath = null)
    {
        var path = ResolveConfigPath(explicitPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Configuration file not found: {path}. Copy darling.sample.json to darling.json and edit it.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static DarlingConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<DarlingConfig>(json, s_jsonOptions);
        return config ?? throw new InvalidDataException("Configuration file parsed to null.");
    }

    /// <summary>
    /// Validates the configuration; returns human-readable problems (empty = valid).
    /// Plaintext passwords are accepted (dev convenience) but reported as warnings by the caller.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Postgres is null)
        {
            problems.Add("postgres section is required.");
        }
        else if (Postgres.Managed)
        {
            /* Managed mode DERIVES the connection string (localhost + port + the generated
               DPAPI-protected credential — see DarlingManagedPostgres); a hand-set string
               would silently win or silently lose depending on code order, so both together
               is a hard config error, not a precedence rule. */
            if (!string.IsNullOrWhiteSpace(Postgres.ConnectionString))
            {
                problems.Add("postgres.managed is true AND postgres.connectionString is set — pick one: " +
                    "managed mode derives the connection string itself (remove connectionString), or remove " +
                    "\"managed\" to use your own PostgreSQL via connectionString.");
            }

            if (Postgres.Port is < 1 or > 65535)
            {
                problems.Add($"postgres.port must be between 1 and 65535 (got {Postgres.Port}).");
            }
        }
        else if (string.IsNullOrWhiteSpace(Postgres.ConnectionString))
        {
            problems.Add("postgres.connectionString is required (or set postgres.managed = true to run the bundled server).");
        }

        if (Servers is null || Servers.Count == 0)
        {
            problems.Add("servers must contain at least one entry.");
            return problems;
        }

        for (int i = 0; i < Servers.Count; i++)
        {
            var server = Servers[i];
            var label = string.IsNullOrWhiteSpace(server.Name) ? $"servers[{i}]" : $"server '{server.Name}'";

            if (string.IsNullOrWhiteSpace(server.Host))
            {
                problems.Add($"{label}: host is required.");
            }

            if (server.UsesSqlAuth)
            {
                if (string.IsNullOrWhiteSpace(server.Username))
                {
                    problems.Add($"{label}: sql auth requires username.");
                }

                if (string.IsNullOrWhiteSpace(server.EncryptedPassword) && string.IsNullOrWhiteSpace(server.Password))
                {
                    problems.Add($"{label}: sql auth requires encryptedPassword (preferred; see --encrypt-password) or password.");
                }
            }
            else if (!string.Equals(server.Auth, "integrated", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{label}: auth must be 'integrated' or 'sql' (got '{server.Auth}').");
            }
        }

        return problems;
    }
}

/// <summary>
/// The Postgres store — two modes. Unmanaged (default): <see cref="ConnectionString"/> points at
/// an existing PostgreSQL and the service never touches its lifecycle. Managed (the shipped
/// zero-admin default in darling.sample.json): <see cref="Managed"/> = true and the service
/// unpacks, initializes, starts, and stops its own bundled PostgreSQL + TimescaleDB via
/// <see cref="DarlingManagedPostgres"/>; the connection string is DERIVED
/// (localhost + <see cref="Port"/> + the generated DPAPI-protected credential), so setting
/// <see cref="ConnectionString"/> too is a validation error.
/// </summary>
public sealed class PostgresConfig
{
    [JsonPropertyName("connectionString")]
    public string ConnectionString { get; set; } = "";

    /// <summary>Run the bundled, service-managed PostgreSQL instead of pointing at an existing one.</summary>
    [JsonPropertyName("managed")]
    public bool Managed { get; set; }

    /// <summary>
    /// The managed server's loopback port. 5641 deliberately avoids PostgreSQL's default 5432 so
    /// the bundled instance can coexist with a PostgreSQL the machine already runs.
    /// </summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 5641;

    /// <summary>
    /// The managed server's data directory; null (the default) means
    /// %ProgramData%\PerformanceMonitorDarling\pg — a machine-wide, service-account-writable
    /// convention created with inherited ACLs.
    /// </summary>
    [JsonPropertyName("dataDirectory")]
    public string? DataDirectory { get; set; }
}

/// <summary>
/// The alert engine's config section. Defaults mirror Lite's <c>App.xaml.cs</c> alert defaults
/// member-for-member: cpu 80% (Total mode), blocking 1, deadlock 1, poison 500 ms, long-running
/// query 30 min, tempdb 80%, low disk 10% / 5 GB, job multiplier 3x, failed-job lookback 60 min,
/// cooldown 5 min. The long-running-query read shape (max results + the five noise filters) is
/// deliberately NOT configurable here — Lite's defaults (5 / all on) are hardcoded in
/// <see cref="DarlingAlertSettings"/> until someone actually needs a knob.
/// </summary>
public sealed class AlertsConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("cpuEnabled")]
    public bool CpuEnabled { get; set; } = true;

    [JsonPropertyName("cpuThresholdPercent")]
    public int CpuThresholdPercent { get; set; } = 80;

    /// <summary>"total" (Lite's default: SQL + other-process CPU) or "sql" (SQL process only).</summary>
    [JsonPropertyName("cpuMode")]
    public string CpuMode { get; set; } = "total";

    [JsonPropertyName("blockingEnabled")]
    public bool BlockingEnabled { get; set; } = true;

    [JsonPropertyName("blockingCountThreshold")]
    public int BlockingCountThreshold { get; set; } = 1;

    [JsonPropertyName("deadlockEnabled")]
    public bool DeadlockEnabled { get; set; } = true;

    [JsonPropertyName("deadlockCountThreshold")]
    public int DeadlockCountThreshold { get; set; } = 1;

    [JsonPropertyName("poisonWaitEnabled")]
    public bool PoisonWaitEnabled { get; set; } = true;

    [JsonPropertyName("poisonWaitThresholdMs")]
    public int PoisonWaitThresholdMs { get; set; } = 500;

    [JsonPropertyName("longRunningQueryEnabled")]
    public bool LongRunningQueryEnabled { get; set; } = true;

    [JsonPropertyName("longRunningQueryThresholdMinutes")]
    public int LongRunningQueryThresholdMinutes { get; set; } = 30;

    [JsonPropertyName("tempDbSpaceEnabled")]
    public bool TempDbSpaceEnabled { get; set; } = true;

    [JsonPropertyName("tempDbSpaceThresholdPercent")]
    public int TempDbSpaceThresholdPercent { get; set; } = 80;

    [JsonPropertyName("lowDiskEnabled")]
    public bool LowDiskEnabled { get; set; } = true;

    /// <summary>Alert when a volume's free space &lt; X% (0 disables this dimension).</summary>
    [JsonPropertyName("lowDiskThresholdPercent")]
    public int LowDiskThresholdPercent { get; set; } = 10;

    /// <summary>Alert when a volume's free space &lt; X GB (0 disables this dimension).</summary>
    [JsonPropertyName("lowDiskThresholdGb")]
    public int LowDiskThresholdGb { get; set; } = 5;

    [JsonPropertyName("longRunningJobEnabled")]
    public bool LongRunningJobEnabled { get; set; } = true;

    [JsonPropertyName("longRunningJobMultiplier")]
    public int LongRunningJobMultiplier { get; set; } = 3;

    [JsonPropertyName("failedJobEnabled")]
    public bool FailedJobEnabled { get; set; } = true;

    [JsonPropertyName("failedJobLookbackMinutes")]
    public int FailedJobLookbackMinutes { get; set; } = 60;

    /// <summary>Minimum minutes between repeated notifications for the same alert condition.</summary>
    [JsonPropertyName("cooldownMinutes")]
    public int CooldownMinutes { get; set; } = 5;

    /// <summary>Databases excluded from blocking/deadlock/long-running-query alert evaluation.</summary>
    [JsonPropertyName("excludedDatabases")]
    public List<string> ExcludedDatabases { get; set; } = new();
}

/// <summary>
/// SMTP alert delivery. Port/SSL/cooldown defaults mirror Lite's (587 / SSL on / 15 minutes).
/// </summary>
public sealed class SmtpConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 587;

    [JsonPropertyName("useSsl")]
    public bool UseSsl { get; set; } = true;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>DPAPI-LocalMachine-protected SMTP password, base64 — produced by --encrypt-password.</summary>
    [JsonPropertyName("encryptedPassword")]
    public string? EncryptedPassword { get; set; }

    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    /// <summary>Comma-separated recipient list.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>Email/webhook channel cooldown between repeated alerts (Lite's default 15).</summary>
    [JsonPropertyName("emailCooldownMinutes")]
    public int EmailCooldownMinutes { get; set; } = 15;
}

/// <summary>
/// The embedded MCP server's config. Port 5152 keeps the product's local MCP family
/// non-colliding on one machine (Dashboard 5150, Lite 5151, Darling 5152).
/// </summary>
public sealed class McpConfig
{
    /// <summary>Default OFF — the headless twin of both apps' mcp_enabled=false default.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 5152;
}

/// <summary>Teams/Slack incoming-webhook alert delivery; a channel is enabled by a non-empty URL.</summary>
public sealed class WebhooksConfig
{
    [JsonPropertyName("teamsUrl")]
    public string TeamsUrl { get; set; } = "";

    [JsonPropertyName("teamsProxy")]
    public string TeamsProxy { get; set; } = "";

    [JsonPropertyName("slackUrl")]
    public string SlackUrl { get; set; } = "";

    [JsonPropertyName("slackProxy")]
    public string SlackProxy { get; set; } = "";
}

public sealed class MonitoredServer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>Azure SQL Database: the one database this entry monitors (feeds the storage-name identity).</summary>
    [JsonPropertyName("database")]
    public string? Database { get; set; }

    /// <summary>"integrated" (default, recommended for a service account) or "sql".</summary>
    [JsonPropertyName("auth")]
    public string Auth { get; set; } = "integrated";

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>DPAPI-LocalMachine-protected password, base64 — produced by --encrypt-password.</summary>
    [JsonPropertyName("encryptedPassword")]
    public string? EncryptedPassword { get; set; }

    /// <summary>Plaintext password — dev convenience only; the service logs a warning when used.</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("readOnlyIntent")]
    public bool ReadOnlyIntent { get; set; }

    [JsonPropertyName("trustServerCertificate")]
    public bool TrustServerCertificate { get; set; }

    /// <summary>Mandatory (default) | Strict | Optional — unknown values fail closed to Mandatory.</summary>
    [JsonPropertyName("encryptMode")]
    public string EncryptMode { get; set; } = "Mandatory";

    [JsonPropertyName("multiSubnetFailover")]
    public bool MultiSubnetFailover { get; set; }

    [JsonPropertyName("excludedDatabases")]
    public List<string> ExcludedDatabases { get; set; } = new();

    [JsonIgnore]
    public bool UsesSqlAuth => string.Equals(Auth, "sql", StringComparison.OrdinalIgnoreCase);

    /// <summary>Display name falls back to the host.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    /// <summary>
    /// The canonical storage identity (host[:database][:RO]) — hashed to server_id via the shared
    /// ServerIdHelper, so this Darling entry derives the same id Lite would for the same server.
    /// </summary>
    [JsonIgnore]
    public string StorageName => PerformanceMonitor.Common.ServerIdHelper.BuildStorageName(Host, Database, ReadOnlyIntent);
}
