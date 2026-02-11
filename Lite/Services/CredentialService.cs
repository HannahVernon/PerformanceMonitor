/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using CredentialManagement;
using Microsoft.Extensions.Logging;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Secure credential storage service using Windows Credential Manager.
/// Credentials are encrypted by Windows using DPAPI and stored per-user.
/// NEVER stores passwords in plain text.
/// </summary>
public class CredentialService
{
    private const string CredentialPrefix = "PerformanceMonitorLite_";
    private readonly ILogger<CredentialService>? _logger;

    public CredentialService(ILogger<CredentialService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Saves SQL Server credentials to Windows Credential Manager.
    /// Credentials are encrypted automatically by Windows.
    /// </summary>
    public bool SaveCredential(string serverId, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("Server ID cannot be null or empty", nameof(serverId));
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username cannot be null or empty", nameof(username));
        }

        if (password == null)
        {
            throw new ArgumentNullException(nameof(password));
        }

        try
        {
            using var credential = new Credential
            {
                Target = GetCredentialTarget(serverId),
                Username = username,
                Password = password,
                Type = CredentialType.Generic,
                PersistanceType = PersistanceType.LocalComputer,
                Description = "SQL Server credentials for Performance Monitor Lite"
            };

            return credential.Save();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save credential for server {ServerId}", serverId);
            return false;
        }
    }

    /// <summary>
    /// Retrieves SQL Server credentials from Windows Credential Manager.
    /// </summary>
    public (string Username, string Password)? GetCredential(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("Server ID cannot be null or empty", nameof(serverId));
        }

        try
        {
            using var credential = new Credential
            {
                Target = GetCredentialTarget(serverId),
                Type = CredentialType.Generic
            };

            if (credential.Load())
            {
                return (credential.Username, credential.Password);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load credential for server {ServerId}", serverId);
        }

        return null;
    }

    /// <summary>
    /// Deletes SQL Server credentials from Windows Credential Manager.
    /// Should be called when a server is removed.
    /// </summary>
    public bool DeleteCredential(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("Server ID cannot be null or empty", nameof(serverId));
        }

        try
        {
            using var credential = new Credential
            {
                Target = GetCredentialTarget(serverId),
                Type = CredentialType.Generic
            };

            return credential.Delete();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to delete credential for server {ServerId}", serverId);
            return false;
        }
    }

    /// <summary>
    /// Checks if credentials exist for a server.
    /// </summary>
    public bool CredentialExists(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return false;
        }

        try
        {
            using var credential = new Credential
            {
                Target = GetCredentialTarget(serverId),
                Type = CredentialType.Generic
            };

            return credential.Exists();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to check credential existence for server {ServerId}", serverId);
            return false;
        }
    }

    /// <summary>
    /// Updates existing credentials. If credentials don't exist, creates them.
    /// </summary>
    public bool UpdateCredential(string serverId, string username, string password)
    {
        DeleteCredential(serverId);
        return SaveCredential(serverId, username, password);
    }

    /// <summary>
    /// Generates the credential target name for Windows Credential Manager.
    /// </summary>
    private static string GetCredentialTarget(string serverId)
    {
        return $"{CredentialPrefix}{serverId}";
    }
}
