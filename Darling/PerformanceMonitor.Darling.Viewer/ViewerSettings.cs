/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's read of darling.json — the SAME file the Darling service uses, but the viewer
/// only needs the postgres section (it talks to the central store, never to monitored
/// SQL Servers). This intentionally duplicates a sliver of the service's DarlingConfig — the
/// resolution order and the postgres section — rather than referencing the service project;
/// a shared config library is not warranted for one section yet.
/// Resolution order matches the service — explicit path → DARLING_CONFIG environment
/// variable → darling.json next to the binary — plus one viewer-only fallback: the parent
/// directory, because the release zip puts the viewer in a viewer\ subfolder under the
/// service root where the operator's darling.json lives. Comments and trailing commas are
/// allowed, property names are case-insensitive.
/// Managed mode (<c>postgres.managed = true</c>, the shipped default): the SERVICE bootstraps
/// and owns the bundled Postgres; the viewer only derives the same connection string —
/// localhost + port + the darling user with the password the service generated on first run,
/// unprotected from the DPAPI-LocalMachine credential file beside the data directory. The
/// derivation constants (user/database name, credential path convention, DPAPI entropy) are
/// the service's <c>DarlingManagedPostgres</c>/<c>DarlingSecrets</c> values, duplicated under
/// the same sliver rule and pinned against the service by a Darling.Tests round-trip test.
/// </summary>
public sealed class ViewerSettings
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /* The service's DarlingSecrets entropy — must match byte-for-byte or the viewer cannot
       read the managed credential (pinned by ViewerSettingsTests). */
    private static readonly byte[] s_dpapiEntropy = Encoding.UTF8.GetBytes("PerformanceMonitor.Darling.v1");

    public string ConnectionString { get; }

    private ViewerSettings(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <param name="explicitPath">A path handed on the command line; wins outright.</param>
    /// <param name="baseDirectory">The viewer binary's directory; null means AppContext.BaseDirectory (tests pass a temp directory).</param>
    public static string ResolveConfigPath(string? explicitPath = null, string? baseDirectory = null)
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

        baseDirectory ??= AppContext.BaseDirectory;
        var besideBinary = Path.Combine(baseDirectory, "darling.json");
        if (File.Exists(besideBinary))
        {
            return besideBinary;
        }

        /* The release zip ships the viewer in a viewer\ subfolder under the service root, and
           the operator's darling.json lives beside the SERVICE exe — so when there is nothing
           beside the viewer, probe one level up. Miss both and we still return the
           beside-binary path, so the not-found hint names the viewer's own directory. */
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)));
        if (!string.IsNullOrEmpty(parent))
        {
            var besideService = Path.Combine(parent, "darling.json");
            if (File.Exists(besideService))
            {
                return besideService;
            }
        }

        return besideBinary;
    }

    /// <summary>
    /// Loads the resolved config file, or returns null when it does not exist (the window shows
    /// the copy-the-sample hint instead of crashing). Malformed content still throws — the
    /// caller turns that into a readable message too.
    /// </summary>
    public static ViewerSettings? TryLoad(string? explicitPath = null)
    {
        var path = ResolveConfigPath(explicitPath);
        if (!File.Exists(path))
        {
            return null;
        }

        return Parse(File.ReadAllText(path));
    }

    public static ViewerSettings Parse(string json)
    {
        var config = JsonSerializer.Deserialize<ConfigDto>(json, s_jsonOptions);

        if (config?.Postgres?.Managed == true)
        {
            return new ViewerSettings(DeriveManagedConnectionString(config.Postgres));
        }

        var connectionString = config?.Postgres?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidDataException("darling.json has no postgres.connectionString (and postgres.managed is not true).");
        }

        return new ViewerSettings(connectionString);
    }

    /// <summary>
    /// The managed-mode derivation, mirroring the service: data directory = configured or
    /// %ProgramData%\PerformanceMonitorDarling\pg, credential = pg-credential.dpapi beside it,
    /// connection = localhost + port + darling/darling. A missing credential means the service
    /// has not completed its first run yet — a readable message, because the main window shows
    /// parse failures to the user.
    /// </summary>
    private static string DeriveManagedConnectionString(PostgresDto postgres)
    {
        var dataDirectory = string.IsNullOrWhiteSpace(postgres.DataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PerformanceMonitorDarling", "pg")
            : Path.GetFullPath(postgres.DataDirectory);

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dataDirectory));
        var credentialPath = string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "pg-credential.dpapi");
        if (credentialPath is null || !File.Exists(credentialPath))
        {
            throw new InvalidDataException(
                "darling.json uses the managed Postgres (postgres.managed = true), but its credential file " +
                $"({credentialPath ?? "beside " + dataDirectory}) does not exist yet. Start the PerformanceMonitor Darling " +
                "service once — its first run initializes the bundled Postgres and writes the credential.");
        }

        var protectedBytes = Convert.FromBase64String(File.ReadAllText(credentialPath).Trim());
        var password = Encoding.UTF8.GetString(
            ProtectedData.Unprotect(protectedBytes, s_dpapiEntropy, DataProtectionScope.LocalMachine));

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = postgres.Port,
            Username = "darling",
            Password = password,
            Database = "darling",
        };
        return builder.ConnectionString;
    }

    private sealed class ConfigDto
    {
        [JsonPropertyName("postgres")]
        public PostgresDto? Postgres { get; set; }
    }

    private sealed class PostgresDto
    {
        [JsonPropertyName("connectionString")]
        public string? ConnectionString { get; set; }

        [JsonPropertyName("managed")]
        public bool Managed { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; } = 5641;

        [JsonPropertyName("dataDirectory")]
        public string? DataDirectory { get; set; }
    }
}
