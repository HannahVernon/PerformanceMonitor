/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.IO;

namespace Lite.Tests;

/// <summary>
/// Repo-source reader shared by the round-2 cross-app parity PIN tests. These pins compare Lite's and
/// Darling's source-of-truth tables/inventories, but no single CI-run test project references BOTH apps'
/// assemblies (<c>Lite.Tests</c> -> Lite only; <c>Darling.Tests</c> -> Darling only). Rather than pull a heavy
/// WPF/service assembly across the SKU boundary — and churn a locked-mode <c>packages.lock.json</c> — the
/// cross-app half is read from the checked-out source tree, the same walk-up-from-<c>bin</c> technique
/// <see cref="SharedCollectorDefaultsPinTests"/> already uses in CI to read
/// <c>Lite/config/ignored_wait_types.json</c>. Each parser is self-validated against the real in-memory object
/// where one is reachable, so a parser bug fails loudly instead of green-washing drift.
/// </summary>
internal static class ParitySource
{
    /// <summary>
    /// The repo root: the nearest ancestor of the test <c>bin</c> that holds the <c>Lite</c>, <c>Darling</c>,
    /// and <c>PerformanceMonitor.Collectors</c> folders (a signature no other directory in the tree carries).
    /// </summary>
    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(System.AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Lite")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Darling")) &&
                Directory.Exists(Path.Combine(dir.FullName, "PerformanceMonitor.Collectors")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the PerformanceMonitor repo root walking up from {System.AppContext.BaseDirectory}");
    }

    /// <summary>Reads a source file by its path relative to the repo root (forward slashes are normalized).</summary>
    internal static string ReadFile(string relativePath)
    {
        var full = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Repo file not found: {relativePath} (resolved to {full})");
        }

        return File.ReadAllText(full);
    }

    /// <summary>Returns the full paths of every <c>*.cs</c> file directly under a repo-relative directory.</summary>
    internal static string[] EnumerateCsFiles(string relativeDir)
    {
        var full = Path.Combine(RepoRoot(), relativeDir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Repo directory not found: {relativeDir} (resolved to {full})");
        }

        return Directory.GetFiles(full, "*.cs", SearchOption.TopDirectoryOnly);
    }
}
