/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * The Custom Views v2 render path (#1563) — the twin of panels.js's v1 renderPanel, for COMPOSED (`source`) panels.
 * A composed panel is not a GET read; it is a spec {source, measure|ratio, aggregate, unit, timeBucket|topN, filters,
 * groupBy, viz} that the composer previews and a saved view renders by POSTing to /api/compose/run (with the view's
 * server/time/variable scope) and charting the returned {sql, rows}. The compiler emits three row shapes, keyed by
 * mode (ComposeCompiler): TIME SERIES `{bucket, <groupDim>…, value}` (bucket ASC), RANKED `{<groupDim>…, value}`
 * (value DESC, LIMIT topN), and SCALAR `{value}` (one row). This module shapes those rows for charts.js and formats
 * every value in the panel's chosen unit (the value is already scaled server-side, so we format — never re-scale).
 *
 * R4 (XSS): every value — dimension values, series/category labels, the compiled SQL — reaches the DOM through
 * util.el()'s text/textContent path (never innerHTML), so an untrusted wait type / object name / server name renders
 * inert. The chart SVG lives entirely in charts.js (one SVG_NS occurrence, the air-gap allowlist); this file has no SVG.
 */

import { el, mount, loadingStrip, errorStrip, emptyStrip, disclosure, fmtInt, fmtNum, apiSend } from "./util.js";
import { renderLineChart, renderBarChart, renderPieChart, CATEGORICAL_COLORS } from "./charts.js";

/** The most series a time chart draws before pooling the rest into a "+N more" note (readability + palette size). */
const MAX_SERIES = 8;

/* ─────────────────────────── panel card + fill ─────────────────────────── */

/**
 * A composed panel as a self-filling `.panel.card` (the saved-view grid's unit) — matches panels.js's renderPanel
 * shape (title + span-2 + a body that shows a loading strip, then the chart / a state). `scope` is the view-level
 * run context {server, hours, variables, values}; flipping it and re-rendering re-scopes every panel at once.
 */
export function renderComposedPanelCard(panelSpec, scope) {
  const body = el("div", { class: "panel-body" }, [loadingStrip()]);
  const panel = el("div", { class: "panel card" + (panelSpec.span === 2 ? " span-2" : "") }, [
    el("h3", {}, [panelSpec.title || measureLabel(panelSpec)]),
    body,
  ]);
  renderComposedInto(body, panelSpec, scope);
  return panel;
}

/** Run a composed panel and fill `body` with the chart (or the empty/error state). Used by the card + the live preview. */
export async function renderComposedInto(body, panelSpec, scope) {
  mount(body, loadingStrip());
  const res = await runCompose(panelSpec, scope);
  if (res.kind === "error") {
    mount(body, errorStrip(res.message || "Could not run this panel."));
    return;
  }
  const data = res.data || {};
  try {
    mount(body, renderComposedResult(data, panelSpec));
  } catch (e) {
    mount(body, errorStrip("Could not render this panel: " + (e && e.message ? e.message : String(e))));
  }
}

/** POST the composed panel to /api/compose/run with the view scope; returns the apiSend result ({sql, rows} on data). */
export function runCompose(panelSpec, scope) {
  return apiSend("POST", "/api/compose/run", buildRunBody(panelSpec, scope));
}

/** Build the /api/compose/run body from a panel spec + scope (a per-panel `hours` overrides the view range). */
function buildRunBody(panelSpec, scope) {
  const body = { panel: toRunPanel(panelSpec) };
  const s = scope || {};
  if (Array.isArray(s.variables) && s.variables.length) body.variables = s.variables;
  if (s.values && Object.keys(s.values).length) body.values = s.values;
  if (s.server != null) body.server = s.server;
  if (s.hours != null) body.hours = s.hours;
  if (panelSpec.hours != null) body.hours = panelSpec.hours;
  return body;
}

/** The exact compose fields the run endpoint reads — never the frontend-only keys (kind/title/span/hours). */
function toRunPanel(p) {
  const out = { source: p.source, viz: p.viz };
  if (p.measure != null) out.measure = p.measure;
  if (p.ratio != null) out.ratio = p.ratio;
  if (p.aggregate != null && p.aggregate !== "") out.aggregate = p.aggregate;
  if (p.unit != null && p.unit !== "") out.unit = p.unit;
  if (p.timeBucket != null && p.timeBucket !== "" && p.timeBucket !== "none") out.timeBucket = p.timeBucket;
  if (p.topN != null && p.topN !== "") out.topN = p.topN;
  if (Array.isArray(p.filters) && p.filters.length) out.filters = p.filters;
  if (Array.isArray(p.groupBy) && p.groupBy.length) out.groupBy = p.groupBy;
  return out;
}

/* ─────────────────────────── result -> chart ─────────────────────────── */

/**
 * Shape a run result ({sql, rows}) into the panel body: the chart for the panel's viz, any partial-fleet / capped
 * note, and a folded "compiled SQL" disclosure (read-only transparency). Empty rows are the honest NO-DATA state
 * (not an error). Returns an array of nodes for the caller to mount.
 */
export function renderComposedResult(result, panelSpec) {
  const rows = Array.isArray(result.rows) ? result.rows : [];
  const nodes = [];

  if (!rows.length) {
    nodes.push(emptyStrip("No data in this window."));
    nodes.push(sqlDisclosure(result.sql));
    return nodes;
  }

  const unit = panelSpec.unit || "";
  const fmt = (v) => formatComposedValue(v, unit);
  const groupDims = Array.isArray(panelSpec.groupBy) ? panelSpec.groupBy : [];

  switch (panelSpec.viz) {
    case "line":
    case "area":
    case "stacked": {
      const { points, series, hidden } = pivotTimeSeries(rows, groupDims, measureLabel(panelSpec));
      nodes.push(
        renderLineChart({
          points,
          xKey: "bucket",
          series,
          formatValue: fmt,
          unit: axisUnit(unit),
          clampMax: unit === "percent" ? 100 : null,
          mode: panelSpec.viz,
        })
      );
      if (hidden > 0) nodes.push(el("div", { class: "chart-note", text: `+${hidden} more series not shown.` }));
      break;
    }
    case "bar":
      nodes.push(renderBarChart({ items: rankedItems(rows, groupDims), formatValue: fmt, unit: axisUnit(unit) }));
      break;
    case "pie":
      nodes.push(renderPieChart({ items: rankedItems(rows, groupDims), formatValue: fmt }));
      break;
    case "stat":
      nodes.push(renderScalar(rows, panelSpec, fmt));
      break;
    case "table":
    default:
      nodes.push(renderComposedTable(rows, unit));
      break;
  }

  const partial = partialFleetNote(groupDims);
  if (partial) nodes.push(partial);
  nodes.push(sqlDisclosure(result.sql));
  return nodes;
}

/* ─────────────────────────── row shaping ─────────────────────────── */

/**
 * Pivot TIME SERIES rows into charts.js's {points, series} shape. With no group-by it is one series (the measure);
 * with a group-by, one series per distinct dimension combination, values re-keyed per bucket. Series are capped to
 * MAX_SERIES by total magnitude (the rest reported as `hidden`) so a high-cardinality group stays legible.
 */
export function pivotTimeSeries(rows, groupDims, label) {
  if (!groupDims.length) {
    const points = rows.map((r) => ({ bucket: r.bucket, value: numOrNull(r.value) }));
    return { points, series: [{ key: "value", label, color: CATEGORICAL_COLORS[0] }], hidden: 0 };
  }

  const totals = new Map(); // seriesKey -> running total (for the top-N cut)
  const labels = new Map(); // seriesKey -> display label
  const byBucket = new Map(); // bucket -> point object
  const order = []; // seriesKey first-seen order (stable colors)

  for (const r of rows) {
    /* "s:"-prefixed so a group value equal to "bucket"/"value" can never collide with a point object's own keys. */
    const key = "s:" + JSON.stringify(groupDims.map((d) => (r[d] == null ? "" : String(r[d]))));
    if (!labels.has(key)) {
      labels.set(key, comboLabel(r, groupDims));
      totals.set(key, 0);
      order.push(key);
    }
    const v = numOrNull(r.value);
    totals.set(key, totals.get(key) + (v || 0));
    let pt = byBucket.get(r.bucket);
    if (!pt) {
      pt = { bucket: r.bucket };
      byBucket.set(r.bucket, pt);
    }
    pt[key] = v;
  }

  let keptKeys = order;
  let hidden = 0;
  if (order.length > MAX_SERIES) {
    keptKeys = [...order].sort((a, b) => totals.get(b) - totals.get(a)).slice(0, MAX_SERIES);
    hidden = order.length - keptKeys.length;
  }

  const series = keptKeys.map((key, i) => ({
    key,
    label: labels.get(key),
    color: CATEGORICAL_COLORS[i % CATEGORICAL_COLORS.length],
  }));
  return { points: [...byBucket.values()], series, hidden };
}

/** Shape RANKED rows into charts.js's items[] ({label, value, color}); the category is the group-by combo. */
export function rankedItems(rows, groupDims) {
  const dims = groupDims.length ? groupDims : otherColumns(rows[0]);
  return rows.map((r, i) => ({
    label: dims.length ? comboLabel(r, dims) : "value",
    value: numOrNull(r.value),
    color: CATEGORICAL_COLORS[i % CATEGORICAL_COLORS.length],
  }));
}

/** A group-by combo's display label — the dimension values joined by " / " (a null/empty value reads as "(none)"). */
function comboLabel(row, dims) {
  return dims
    .map((d) => {
      const v = row[d];
      return v == null || v === "" ? "(none)" : String(v);
    })
    .join(" / ");
}

/** The non-`value` columns of a row (the fallback category keys when a ranked result carries no declared group-by). */
function otherColumns(row) {
  return row ? Object.keys(row).filter((k) => k !== "value" && k !== "bucket") : [];
}

/* ─────────────────────────── scalar + table renderers ─────────────────────────── */

/** A single-aggregate SCALAR result as one big stat tile (the same .stats/.stat markup the v1 stat viz uses). */
function renderScalar(rows, panelSpec, fmt) {
  const value = rows[0] ? rows[0].value : null;
  return el("div", { class: "stats" }, [
    el("div", { class: "stat" }, [
      el("div", { class: "value", text: fmt(value) }),
      el("div", { class: "label", text: panelSpec.title || measureLabel(panelSpec) }),
    ]),
  ]);
}

/** Any result as a table of its returned columns — bucket localized, value formatted in the unit, dims as text. */
function renderComposedTable(rows, unit) {
  const cols = Object.keys(rows[0] || {});
  if (!cols.length) return emptyStrip("No columns to show.");
  const head = el(
    "tr",
    {},
    cols.map((c) => el("th", { text: columnLabel(c), class: c === "value" ? "num" : null }))
  );
  const bodyRows = rows.map((row) =>
    el(
      "tr",
      {},
      cols.map((c) => {
        if (c === "value") return el("td", { class: "num", text: formatComposedValue(row[c], unit) });
        if (c === "bucket") return el("td", { text: localBucket(row[c]) });
        const v = row[c];
        return el("td", { text: v == null || v === "" ? "—" : String(v) });
      })
    )
  );
  return el("div", { class: "table-wrap" }, [
    el("table", { class: "data" }, [el("thead", {}, [head]), el("tbody", {}, bodyRows)]),
  ]);
}

/** A table header for a result column: "Value" gains its unit, "bucket" -> "Time", a dim name is humanized. */
function columnLabel(c) {
  if (c === "bucket") return "Time";
  if (c === "value") return "Value";
  return humanize(c);
}

/** Localize a naive-UTC bucket timestamp for the table's Time column. */
function localBucket(s) {
  if (!s || typeof s !== "string") return "—";
  const hasZone = /[zZ]$|[+\-]\d\d:?\d\d$/.test(s);
  const d = new Date(hasZone ? s : s + "Z");
  return isNaN(d.getTime()) ? String(s) : d.toLocaleString();
}

/* ─────────────────────────── formatting ─────────────────────────── */

/**
 * Format a composed value already scaled to `unit` (the compiler did the conversion, so we FORMAT, never re-scale):
 * percent -> "n.n%", count -> integer, ratio -> a 0..1 fraction, bytes/pages -> integer + unit, any other unit
 * (ms/s/min/kb/mb/gb/…) -> "n.n unit", and a bare number when there is no unit.
 */
export function formatComposedValue(v, unit) {
  if (v == null || isNaN(v)) return "—";
  const n = Number(v);
  if (unit === "percent") return fmtNum(n, 1) + "%";
  if (unit === "count") return fmtInt(n);
  if (unit === "ratio") return fmtNum(n, 3);
  if (unit === "bytes" || unit === "pages") return fmtInt(n) + " " + unit;
  if (unit) return fmtNum(n, 1) + " " + unit;
  return fmtNum(n, 1);
}

/** The short y-axis unit caption: "%" for percent, the bare unit for a real unit, null for count/ratio (unitless-ish). */
function axisUnit(unit) {
  if (unit === "percent") return "%";
  if (!unit || unit === "count" || unit === "ratio") return null;
  return unit;
}

/* ─────────────────────────── small helpers ─────────────────────────── */

/** The measure/ratio's catalog key as a fallback panel title (the composer usually sets a real title). */
function measureLabel(panelSpec) {
  return panelSpec.title || panelSpec.measure || panelSpec.ratio || "Metric";
}

/** snake_case -> Title Case, mirroring derive.js's humanizeKey (kept local so this module imports nothing DOM-y). */
function humanize(key) {
  return String(key == null ? "" : key)
    .replace(/_/g, " ")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

/** A number or null (never NaN) — the chart readers treat null as a gap. */
function numOrNull(v) {
  if (v == null || v === "") return null;
  const n = Number(v);
  return isNaN(n) ? null : n;
}

/** A soft note when a panel groups by server: a fleet-wide group only lists servers that collect the measure. */
function partialFleetNote(groupDims) {
  if (!groupDims.includes("server")) return null;
  return el("div", { class: "chart-note", text: "Only servers collecting this measure appear." });
}

/** The folded, read-only "compiled SQL" disclosure (transparency; progressive disclosure keeps it out of the way). */
function sqlDisclosure(sql) {
  if (!sql) return null;
  return disclosure("View compiled SQL", el("pre", { class: "compiled-sql", text: sql }), { max: 40 });
}
