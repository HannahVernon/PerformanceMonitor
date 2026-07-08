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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Guards that the viewer is fully theme-able: the Dark, Light, and CoolBreeze dictionaries define the
/// SAME set of resource keys, and every theme token the viewer XAML actually references resolves in ALL
/// three. This is the automated backstop for "Light / CoolBreeze mode has no missing resources" — a token
/// that exists only in Dark (the historical failure mode: the app looked right in Dark only) fails here
/// instead of silently rendering a control unstyled after a theme switch.
///
/// Text-parses the XAML (no WPF apartment / pack-URI loading needed) and locates the Viewer project via
/// <see cref="CallerFilePathAttribute"/> — the tests read source that sits next to this file in the repo.
/// </summary>
public sealed class ThemeCompletenessTests
{
    private static readonly string[] ThemeRelativePaths =
    {
        "Themes/DarkTheme.xaml",
        "Themes/LightTheme.xaml",
        "Themes/CoolBreezeTheme.xaml",
    };

    /// <summary>Matches a named resource definition: <c>x:Key="Name"</c> where Name is a plain identifier
    /// (skips system keys like <c>x:Key="{x:Static SystemColors.HighlightBrushKey}"</c>).</summary>
    private static readonly Regex KeyDefinition =
        new("x:Key=\"([A-Za-z_][A-Za-z0-9_]*)\"", RegexOptions.Compiled);

    /// <summary>Matches a simple resource reference: <c>{DynamicResource Name}</c> / <c>{StaticResource Name}</c>.
    /// The identifier-only capture skips nested markup like <c>{StaticResource {x:Type Button}}</c>.</summary>
    private static readonly Regex ResourceReference =
        new(@"\{(?:Dynamic|Static)Resource\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}", RegexOptions.Compiled);

    [Fact]
    public void Light_and_CoolBreeze_define_the_same_keys_as_Dark()
    {
        var dark = KeysIn(ThemeRelativePaths[0]);
        var light = KeysIn(ThemeRelativePaths[1]);
        var coolBreeze = KeysIn(ThemeRelativePaths[2]);

        Assert.False(dark.Count == 0, "Parsed zero keys from DarkTheme.xaml — the test cannot find the theme files.");

        var missingFromLight = dark.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extraInLight = light.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(
            missingFromLight.Count == 0 && extraInLight.Count == 0,
            $"LightTheme.xaml key set differs from DarkTheme.xaml.\n" +
            $"  Missing from Light (would render unstyled in Light): {Join(missingFromLight)}\n" +
            $"  Extra in Light (not in Dark): {Join(extraInLight)}");

        var missingFromCool = dark.Except(coolBreeze).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extraInCool = coolBreeze.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(
            missingFromCool.Count == 0 && extraInCool.Count == 0,
            $"CoolBreezeTheme.xaml key set differs from DarkTheme.xaml.\n" +
            $"  Missing from CoolBreeze: {Join(missingFromCool)}\n" +
            $"  Extra in CoolBreeze: {Join(extraInCool)}");
    }

    [Fact]
    public void Every_theme_token_referenced_in_viewer_xaml_resolves_in_all_three_themes()
    {
        var viewerDir = ViewerDirectory();
        var dark = KeysIn(ThemeRelativePaths[0]);
        var light = KeysIn(ThemeRelativePaths[1]);
        var coolBreeze = KeysIn(ThemeRelativePaths[2]);

        /* Only consider references that ARE theme tokens (defined in Dark) — this ignores locally-defined
           window styles, ViewerDarkTheme aliases, and WPF system keys, so a real "token in Dark but not
           Light/CoolBreeze" is the only thing that can fail here. */
        var offenders = new List<string>();
        foreach (var xaml in Directory.EnumerateFiles(viewerDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (xaml.Contains(Path.Combine("obj", ""), StringComparison.OrdinalIgnoreCase) ||
                xaml.Contains(Path.Combine("bin", ""), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(xaml);
            foreach (Match m in ResourceReference.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (!dark.Contains(key))
                {
                    continue; // not a theme token (local style / system / cross-assembly)
                }

                if (!light.Contains(key))
                {
                    offenders.Add($"{Path.GetFileName(xaml)} references '{key}', which is missing from LightTheme.xaml");
                }
                if (!coolBreeze.Contains(key))
                {
                    offenders.Add($"{Path.GetFileName(xaml)} references '{key}', which is missing from CoolBreezeTheme.xaml");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Theme tokens referenced by the viewer XAML do not resolve in every theme:\n  " +
            string.Join("\n  ", offenders.Distinct()));
    }

    private static HashSet<string> KeysIn(string relativePath)
    {
        var path = Path.Combine(ViewerDirectory(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var text = File.ReadAllText(path);
        return KeyDefinition.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The Viewer project directory, resolved from this test file's compile-time path (it lives at
    /// <c>Darling/Darling.Tests/</c>; the Viewer is the sibling <c>Darling/PerformanceMonitor.Darling.Viewer/</c>).</summary>
    private static string ViewerDirectory([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(testDir, "..", "PerformanceMonitor.Darling.Viewer"));
    }

    private static string Join(IReadOnlyCollection<string> keys) =>
        keys.Count == 0 ? "(none)" : string.Join(", ", keys);
}
