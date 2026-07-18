/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The web dashboard's HTTP API surface (#1562). <see cref="DarlingWebHostService"/> owns the host, the
/// pipeline order, and the auth gate; this class owns the ROUTES. The two are split so the host (Builder 1's
/// foundation) and the endpoints (Builder 2) evolve independently against the stable
/// <see cref="MapAll"/> seam.
///
/// <para><b>SEAM CONTRACT (Builder 2 owns filling this in):</b> <see cref="MapAll"/> is called ONCE from the
/// web host's pipeline, AFTER the network-mode auth middleware and BEFORE UseDefaultFiles/UseStaticFiles, with
/// the least-privilege VIEWER-role <see cref="NpgsqlDataSource"/> the host built. Builder 2 adds the read
/// endpoints here (e.g. <c>/api/read/{tool}</c> 1:1 with the MCP tool catalog, the pre-banded <c>/api/fleet</c>
/// and enriched <c>/api/server/{id}</c> DTOs, <c>/api/alerts</c>) — every route under <c>/api/*</c> so the
/// static-file surface (<c>/</c>, <c>/index.html</c>, <c>/css/*</c>, <c>/js/*</c>) never collides. Do NOT add a
/// route at <c>/</c> (that path is the SPA's, served by UseDefaultFiles → index.html). Reuse the PUBLIC STATIC
/// tool methods directly (attributes are inert) so there is zero SQL/projection drift from the MCP surface;
/// sniff a leading '{' to pass a serialized object through as application/json and map the bare
/// "Error during ..." string to HTTP 500.</para>
/// </summary>
public static class DarlingWebEndpoints
{
    /// <summary>
    /// Maps the web dashboard's HTTP endpoints onto <paramref name="app"/>, reading from
    /// <paramref name="postgres"/> (the VIEWER-role store pool). PHASE-1 STUB: only the <c>/api/ping</c> health
    /// probe is wired — Builder 2 replaces this body with the real read endpoints. Keep every route under
    /// <c>/api/*</c>.
    /// </summary>
    public static void MapAll(WebApplication app, NpgsqlDataSource postgres)
    {
        /* Builder 2: the VIEWER-role data source the read endpoints query. Referenced here so the parameter is
           part of the stable seam signature; discard until the real endpoints consume it. */
        _ = postgres;

        /* Liveness probe — proves the host is up and the pipeline (auth in network mode, then routing) reaches
           the API surface. Returns {"status":"ok"}. Builder 2 keeps this and adds the read endpoints beside it. */
        app.MapGet("/api/ping", () => Results.Json(new { status = "ok" }));
    }
}
