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

        if (string.IsNullOrWhiteSpace(Postgres?.ConnectionString))
        {
            problems.Add("postgres.connectionString is required.");
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

public sealed class PostgresConfig
{
    [JsonPropertyName("connectionString")]
    public string ConnectionString { get; set; } = "";
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
