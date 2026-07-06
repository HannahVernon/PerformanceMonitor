/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the viewer's per-user preferences store — the persisted DEFAULTS (default time range + auto-refresh
/// on/off + interval) that seed a newly-opened server tab's toolbar. Covers the three behaviors the store
/// promises: defaults when the file is missing, a lossless save/load round-trip, and clamping an
/// out-of-range value back to its default (a corrupt / hand-edited / forward-version file must never leave
/// a toolbar combo unselected). Each store points at a unique temp file so nothing touches the real profile.
/// </summary>
public sealed class ViewerPreferencesStoreTests : IDisposable
{
    private readonly string _tempFile =
        Path.Combine(Path.GetTempPath(), $"viewer-preferences-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsHistoricalToolbarDefaults()
    {
        var store = new ViewerPreferencesStore(_tempFile);

        var prefs = store.Load();

        /* The default-constructed values reproduce the toolbar's old hardcoded XAML defaults exactly:
           index 3 = Last 24 hours, auto-refresh ON, interval index 1 = 1 minute. */
        Assert.Equal(3, prefs.DefaultTimeRangeIndex);
        Assert.True(prefs.AutoRefreshEnabled);
        Assert.Equal(1, prefs.AutoRefreshIntervalIndex);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllThreeValues()
    {
        var store = new ViewerPreferencesStore(_tempFile);
        store.Save(new ViewerPreferences
        {
            DefaultTimeRangeIndex = 0,      // Last 1 hour
            AutoRefreshEnabled = false,
            AutoRefreshIntervalIndex = 2,   // 5 minutes
        });

        /* A fresh store on the same path proves the values survived a real serialize → file → deserialize. */
        var reloaded = new ViewerPreferencesStore(_tempFile).Load();

        Assert.Equal(0, reloaded.DefaultTimeRangeIndex);
        Assert.False(reloaded.AutoRefreshEnabled);
        Assert.Equal(2, reloaded.AutoRefreshIntervalIndex);
    }

    [Fact]
    public void Load_ClampsOutOfRangeIndices_BackToDefaults()
    {
        /* Persist deliberately invalid indices (Custom=5 is not a valid default; 99 is off the interval combo)
           by writing the JSON directly, then confirm Load normalizes both back to their defaults while
           leaving the valid auto-refresh flag untouched. */
        File.WriteAllText(
            _tempFile,
            """
            {
              "DefaultTimeRangeIndex": 5,
              "AutoRefreshEnabled": false,
              "AutoRefreshIntervalIndex": 99
            }
            """);

        var prefs = new ViewerPreferencesStore(_tempFile).Load();

        Assert.Equal(3, prefs.DefaultTimeRangeIndex);       // clamped 5 -> 24h default
        Assert.Equal(1, prefs.AutoRefreshIntervalIndex);    // clamped 99 -> 1m default
        Assert.False(prefs.AutoRefreshEnabled);             // in-range value preserved
    }

    [Fact]
    public void Load_WhenFileCorrupt_ReturnsDefaults()
    {
        File.WriteAllText(_tempFile, "{ not valid json");

        var prefs = new ViewerPreferencesStore(_tempFile).Load();

        Assert.Equal(3, prefs.DefaultTimeRangeIndex);
        Assert.True(prefs.AutoRefreshEnabled);
        Assert.Equal(1, prefs.AutoRefreshIntervalIndex);
    }

    [Fact]
    public void Save_CreatesTheAppDataDirectory_WhenItDoesNotExist()
    {
        /* Point at a nested path whose directory does not exist yet — Save must create it (first-run). */
        var nestedDir = Path.Combine(Path.GetTempPath(), $"viewer-prefs-dir-{Guid.NewGuid():N}");
        var nestedFile = Path.Combine(nestedDir, "viewer-preferences.json");
        try
        {
            new ViewerPreferencesStore(nestedFile).Save(new ViewerPreferences());

            Assert.True(File.Exists(nestedFile));
        }
        finally
        {
            if (Directory.Exists(nestedDir))
            {
                Directory.Delete(nestedDir, recursive: true);
            }
        }
    }
}
