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
using System.Text.RegularExpressions;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1562: the web dashboard ships AIR-GAPPED — every asset under <c>wwwroot</c> must be fully self-contained,
/// with zero off-origin references (no CDN, no remote fonts, no protocol-relative includes). This CI scan is the
/// enforcement: it walks the shipped <c>wwwroot</c> (the transitively-copied build output, the same location the
/// assets test asserts) and fails, naming the file + line, on any absolute <c>http(s)://</c> URL or a
/// protocol-relative <c>//host</c> in a fetchable context (<c>src=</c>/<c>href=</c>/<c>url()</c>/<c>@import</c>/
/// module <c>from</c>). The lone allowlisted literal is the SVG XML namespace identifier
/// (<c>http://www.w3.org/2000/svg</c>), which <c>createElementNS</c> requires and which is never dereferenced.
/// </summary>
public sealed class DarlingWebSelfContainmentTests
{
    /// <summary>The only <c>http(s)</c> literals allowed in wwwroot: XML namespace identifiers (never fetched).</summary>
    private static readonly string[] AllowedNamespaceUris = { "http://www.w3.org/2000/svg" };

    /// <summary>A protocol-relative <c>//host</c> reference sitting in a context that would cause a fetch.</summary>
    private static readonly Regex ProtocolRelative = new(
        @"(?:\b(?:src|href)\s*=\s*[""']?|url\(\s*[""']?|@import\s+[""']?|\bfrom\s+[""'])//",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void Wwwroot_HasNoOffOriginReferences()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Assert.True(Directory.Exists(root), $"wwwroot was not copied to the build output ({root}).");

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var reason = FindOffOrigin(lines[i]);
                if (reason is not null)
                {
                    violations.Add($"{rel}:{i + 1}: {reason} -> {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "wwwroot must be fully self-contained (the air-gap rule). Off-origin references found:\n" +
                string.Join("\n", violations));
    }

    /// <summary>
    /// Pure line classifier (unit-testable without the filesystem): returns a reason when the line carries an
    /// off-origin reference, else null. An absolute <c>http(s)://</c> URL fails unless it is exactly an allowlisted
    /// namespace URI; a protocol-relative <c>//host</c> in a fetchable context fails.
    /// </summary>
    internal static string? FindOffOrigin(string line)
    {
        foreach (var scheme in new[] { "http://", "https://" })
        {
            var idx = 0;
            while ((idx = line.IndexOf(scheme, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var url = ExtractUrl(line, idx);
                if (!AllowedNamespaceUris.Contains(url, StringComparer.OrdinalIgnoreCase))
                {
                    return $"off-origin URL '{url}'";
                }

                idx += scheme.Length;
            }
        }

        return ProtocolRelative.IsMatch(line) ? "protocol-relative reference (//host)" : null;
    }

    private static string ExtractUrl(string line, int start)
    {
        var end = start;
        while (end < line.Length)
        {
            var ch = line[end];
            if (char.IsWhiteSpace(ch) || ch is '"' or '\'' or ')' or '<' or '>' or '`')
            {
                break;
            }

            end++;
        }

        return line[start..end];
    }

    [Theory]
    [InlineData("const SVG_NS = \"http://www.w3.org/2000/svg\";", false)] // allowlisted namespace, not a fetch
    [InlineData("  // a plain comment with two slashes", false)]
    [InlineData("import { el } from \"./util.js\";", false)]
    [InlineData("<link rel=\"stylesheet\" href=\"css/app.css\" />", false)]
    [InlineData("<a href=\"#/fleet\">Fleet</a>", false)]
    [InlineData("<script src=\"https://cdn.example.com/x.js\"></script>", true)] // absolute off-origin
    [InlineData("@import url(https://fonts.example.com/f.css);", true)]
    [InlineData("<img src=\"//evil.example.com/pixel.gif\">", true)] // protocol-relative
    [InlineData("background: url('//cdn.example.com/bg.png');", true)]
    public void FindOffOrigin_FlagsExactlyTheOffOriginLines(string line, bool shouldFlag)
    {
        Assert.Equal(shouldFlag, FindOffOrigin(line) is not null);
    }
}
