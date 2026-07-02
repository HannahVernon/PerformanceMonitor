/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's read of darling.json — the SAME file the Darling service uses, but the viewer
/// only needs postgres.connectionString (it talks to the central store, never to monitored
/// SQL Servers). This intentionally duplicates a sliver of the service's DarlingConfig — the
/// resolution order and the postgres section — rather than referencing the service project;
/// a shared config library is not warranted for one string yet.
/// Resolution order matches the service exactly: explicit path → DARLING_CONFIG environment
/// variable → darling.json next to the binary. Comments and trailing commas are allowed,
/// property names are case-insensitive.
/// </summary>
public sealed class ViewerSettings
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ConnectionString { get; }

    private ViewerSettings(string connectionString)
    {
        ConnectionString = connectionString;
    }

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
        var connectionString = config?.Postgres?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidDataException("darling.json has no postgres.connectionString.");
        }

        return new ViewerSettings(connectionString);
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
    }
}
