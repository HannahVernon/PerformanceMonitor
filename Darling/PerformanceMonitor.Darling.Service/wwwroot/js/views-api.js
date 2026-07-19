/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * The #1563 custom-views API client — thin wrappers over util.js's apiGet/apiSend for the session probe, the
 * read catalog, and the view CRUD, plus a PURE client-side definition validator that mirrors the server's
 * ValidateDefinition (so a bad import/compose is caught before the round-trip — the backend re-validates as the
 * authority). The session ({can_edit}) and catalog are immutable per page-load, so they are fetched ONCE and
 * cached as promises; every caller shares the one in-flight request. Editing is available to any authenticated seat, so
 * can_edit is true for any request past the host auth gate; it only goes false if the session probe itself failed (fail-closed), and the UI hides every edit
 * affordance (render + export stay open to all).
 */

import { apiGet, apiSend } from "./util.js";

let _sessionPromise = null;
let _catalogPromise = null;

/** The session capability probe — resolves to {can_edit} (false on any transport failure: fail-closed UI). */
export function getSession() {
  if (!_sessionPromise) {
    _sessionPromise = apiGet("/api/session").then((r) =>
      r.kind === "data" && r.data && typeof r.data.can_edit === "boolean" ? r.data : { can_edit: false }
    );
  }
  return _sessionPromise;
}

/** An empty compose sub-catalog — the shell a consumer reads array fields off when the fetch failed. */
function emptyCompose() {
  return {
    measures: [],
    dimensions: [],
    annotationSources: [],
    universalDimensions: [],
    unitFamilies: [],
    aggregates: [],
    timeBuckets: [],
    filterOps: [],
    viz: [],
  };
}

/**
 * The catalog — {reads, viz} for the v1 read picker AND `compose` (measures/dimensions/unit-families/aggregates/
 * time-buckets/filter-ops/viz) for the v2 composer (#1563). One fetch backs both pickers; empty shells on failure so
 * every caller can read the array fields without a guard. The compose node is DATA-DRIVEN (B3 grows the measure list
 * server-side) — the composer never hardcodes a measure.
 */
export function getCatalog() {
  if (!_catalogPromise) {
    _catalogPromise = apiGet("/api/catalog").then((r) => {
      const d = r.kind === "data" && r.data ? r.data : null;
      const compose = d && d.compose ? { ...emptyCompose(), ...d.compose } : emptyCompose();
      return { reads: (d && d.reads) || [], viz: (d && d.viz) || [], compose };
    });
  }
  return _catalogPromise;
}

/** GET the bare-array list of view summaries (no definition). */
export function listViews() {
  return apiGet("/api/views");
}

/** GET one view in full (with its embedded definition). */
export function getView(id) {
  return apiGet("/api/views/" + encodeURIComponent(id));
}

/** POST a new view {name, description?, definition} — 201 + the full view, or 400/409(dup)/403. */
export function createView(body) {
  return apiSend("POST", "/api/views", body);
}

/** PUT an existing view {name, description?, definition, version} — 200 + the full view, or 400/404/409/403. */
export function updateView(id, body) {
  return apiSend("PUT", "/api/views/" + encodeURIComponent(id), body);
}

/** DELETE a view — 204, or 404/403. */
export function deleteView(id) {
  return apiSend("DELETE", "/api/views/" + encodeURIComponent(id));
}

/**
 * PURE client-side validation of a view definition against the cached catalog — a fast, precise pre-check on
 * import that mirrors the structural rules the server's ValidateDefinition enforces. A panel is EITHER a v2
 * COMPOSED panel (names a `source`: source/measure|ratio/viz checked against catalog.compose) or a v1 READ panel
 * (names a `read` on the allowlist, viz in the vocabulary, param keys ⊆ the read's params, required params present).
 * span 1|2 and a raw-`path` ban apply to both. Returns null when valid, else a caller-facing error string. The
 * backend re-validates as the authority (it owns the full compose cross-check); this only gives an immediate error.
 */
export function validateDefinition(def, catalog) {
  if (!def || typeof def !== "object" || Array.isArray(def)) {
    return "Definition must be a JSON object.";
  }
  if (!Array.isArray(def.panels)) {
    return "Definition must have a 'panels' array.";
  }
  if (def.panels.length === 0) {
    return "Definition must have at least one panel.";
  }
  if (def.variables != null && !Array.isArray(def.variables)) {
    return "Definition 'variables' must be an array.";
  }
  if (def.range != null && (typeof def.range !== "object" || Array.isArray(def.range))) {
    return "Definition 'range' must be an object.";
  }

  const reads = new Map((catalog.reads || []).map((r) => [r.name, r]));
  const vizSet = new Set(catalog.viz || []);
  const compose = catalog.compose || {};
  const sourceSet = new Set((compose.measures || []).map((m) => m.source));
  const measureByKey = new Map((compose.measures || []).map((m) => [m.key, m]));
  const composeVizSet = new Set(compose.viz || []);

  for (let i = 0; i < def.panels.length; i++) {
    const p = def.panels[i];
    const n = i + 1;
    if (!p || typeof p !== "object" || Array.isArray(p)) {
      return "Panel " + n + " must be an object.";
    }
    if (p.path != null) {
      return "Panel " + n + " uses raw 'path' mode, which is not allowed; use 'read' or 'source'.";
    }
    if (p.span != null && p.span !== 1 && p.span !== 2) {
      return "Panel " + n + " span must be 1 or 2.";
    }

    /* A composed panel names a source; a read panel names a read. */
    if (p.source != null) {
      if (!sourceSet.has(p.source)) {
        return "Panel " + n + " references unknown source '" + p.source + "'.";
      }
      if (p.measure != null && p.ratio != null) {
        return "Panel " + n + " must set exactly one of 'measure' or 'ratio', not both.";
      }
      const key = p.measure != null ? p.measure : p.ratio;
      const m = measureByKey.get(key);
      if (!m) {
        return "Panel " + n + " references unknown measure '" + (key || "") + "'.";
      }
      if (m.source !== p.source) {
        return "Panel " + n + " measure '" + m.key + "' is not on source '" + p.source + "'.";
      }
      if (!p.viz || !composeVizSet.has(p.viz)) {
        return "Panel " + n + " has an unknown or missing chart type '" + (p.viz || "") + "'.";
      }
      continue;
    }

    if (!p.read) {
      return "Panel " + n + " is missing a read or source.";
    }
    const rd = reads.get(p.read);
    if (!rd) {
      return "Panel " + n + " references unknown read '" + p.read + "'.";
    }
    if (!p.viz) {
      return "Panel " + n + " is missing a visualization.";
    }
    if (!vizSet.has(p.viz)) {
      return "Panel " + n + " has unknown visualization '" + p.viz + "'.";
    }

    const params = p.params || {};
    if (typeof params !== "object" || Array.isArray(params)) {
      return "Panel " + n + " params must be an object.";
    }
    const allowed = new Set((rd.params || []).map((x) => x.name));
    for (const k of Object.keys(params)) {
      if (!allowed.has(k)) {
        return "Panel " + n + " has an unknown parameter '" + k + "' for read '" + p.read + "'.";
      }
    }
    for (const rp of rd.params || []) {
      if (rp.required && (params[rp.name] == null || params[rp.name] === "")) {
        return "Panel " + n + " is missing the required parameter '" + rp.name + "'.";
      }
    }
  }

  return null;
}
