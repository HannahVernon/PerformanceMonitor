/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1562: the web dashboard's static assets must land in the build output. The service csproj declares
/// <c>&lt;Content Include="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" /&gt;</c>, and the web host
/// pins its content/web root to <see cref="AppContext.BaseDirectory"/> — so if the copy silently stops
/// working, a Windows service would 404 every static file in production only (the load-bearing footgun). This
/// test fails the build instead. The content flows transitively from the referenced service project into the
/// test's own output directory, so the assertion runs against the test bin.
/// </summary>
public sealed class DarlingWebAssetsTests
{
    [Fact]
    public void Wwwroot_IndexHtml_IsCopiedToTheBuildOutput()
    {
        var indexPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        Assert.True(File.Exists(indexPath),
            $"wwwroot/index.html was not copied to the build output ({indexPath}). Check the " +
            "<Content Include=\"wwwroot\\**\\*\"> item in PerformanceMonitor.Darling.Service.csproj.");
    }
}
