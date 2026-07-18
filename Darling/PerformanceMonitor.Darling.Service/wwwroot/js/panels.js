/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * The panel renderer + viz registry (#1562) — the #1563 seam. Every data view on the server/alerts pages is a
 * PANEL DESCRIPTOR run through renderPanel({read, params, viz, span, ...}):
 *   read    — an MCP tool name (-> GET /api/read/{read}) or, via `path`, a raw API path (-> GET path)
 *   params  — query-string params
 *   viz     — one of the registry keys below: "table" | "line" | "stat" | "bandlist"
 *   span    — 2 to span both columns of a grid
 * Phase-1 pages hand-build descriptor ARRAYS (js/pages/*). #1563 later replaces those arrays with stored JSON
 * loaded through this same renderPanel — so nothing downstream of here is built now, but the seam is stable.
 *
 * R4 (XSS): every value reaches the DOM through util.el()'s text/textContent path — the table cells, stat
 * values, band rows, and chart tooltip never touch innerHTML, so query text / object names / wait types /
 * server names render inert.
 */

import {
  el,
  mount,
  loadingStrip,
  errorStrip,
  emptyStrip,
  readTool,
  apiGet,
  buildQuery,
  getPath,
  applyFormat,
  bandClass,
  sevClass,
} from "./util.js";
import { renderLineChart, SERIES_COLORS } from "./charts.js";

/**
 * Build a panel node. It returns immediately with a loading strip and fills itself once the fetch resolves,
 * mapping the three API response kinds (data / empty envelope / error) to the right UI.
 */
export function renderPanel(desc) {
  const body = el("div", { class: "panel-body" }, [loadingStrip()]);
  const panel = el("div", { class: "panel card" + (desc.span === 2 ? " span-2" : "") }, [
    el("h3", {}, [desc.title, desc.subtitle ? el("span", { class: "panel-sub", text: " " + desc.subtitle }) : null]),
    body,
  ]);
  loadPanel(desc, body);
  return panel;
}

async function loadPanel(desc, body) {
  const res = desc.read ? await readTool(desc.read, desc.params) : await apiGet(desc.path + buildQuery(desc.params));

  if (res.kind === "error") {
    mount(body, errorStrip(res.message));
    return;
  }
  if (res.kind === "empty") {
    mount(body, emptyStrip(res.message));
    return;
  }

  const render = VIZ[desc.viz];
  if (!render) {
    mount(body, errorStrip("Unknown visualization: " + desc.viz));
    return;
  }
  try {
    mount(body, render(res.data, desc));
  } catch (e) {
    mount(body, errorStrip("Could not render this panel: " + (e && e.message ? e.message : String(e))));
  }
}

/* ─────────────────────────── viz registry ─────────────────────────── */

/**
 * The viz registry — the four phase-1 renderers. Each is (data, desc) -> Node. Adding a viz here is the ONLY
 * change #1563 needs to grow the descriptor vocabulary.
 */
export const VIZ = {
  table: vizTable,
  line: vizLine,
  stat: vizStat,
  bandlist: vizBandlist,
};

/* table: desc = { rowsKey, columns:[{key,label,format,align,wrap,mono,sevKey,statusSev}] } */
function vizTable(data, desc) {
  const rows = getPath(data, desc.rowsKey) || [];
  if (!rows.length) return emptyStrip(desc.emptyText || "No rows in this window.");
  const cols = desc.columns;

  const head = el(
    "tr",
    {},
    cols.map((c) => el("th", { text: c.label, class: isNumericCol(c) ? "num" : null }))
  );
  const bodyRows = rows.map((row) => el("tr", {}, cols.map((c) => cell(row, c))));

  return el("div", { class: "table-wrap" }, [
    el("table", { class: "data" }, [el("thead", {}, [head]), el("tbody", {}, bodyRows)]),
  ]);
}

function isNumericCol(c) {
  return c.align === "right" || ["int", "num1", "num2", "ms", "mb", "pct"].includes(c.format);
}

function cell(row, c) {
  const raw = getPath(row, c.key);
  const cls = [];
  if (isNumericCol(c)) cls.push("num");
  if (c.wrap) cls.push("wrap");
  if (c.mono) cls.push("mono");
  if (c.sevKey) cls.push(sevClass(getPath(row, c.sevKey)));
  if (c.statusSev) cls.push(sevClass(statusToSev(raw)));
  const text = c.format ? applyFormat(c.format, raw) : raw == null || raw === "" ? "—" : String(raw);
  return el("td", { class: cls.join(" ") || null, text });
}

/* stat: desc = { stats:[{key,label,format}] } over the tool's top-level object */
function vizStat(data, desc) {
  return el(
    "div",
    { class: "stats" },
    desc.stats.map((s) =>
      el("div", { class: "stat" }, [
        el("div", { class: "value", text: applyFormat(s.format, getPath(data, s.key)) }),
        el("div", { class: "label", text: s.label }),
      ])
    )
  );
}

/* line: desc = { rowsKey, xKey, series:[{key,label,color?}], format? } */
function vizLine(data, desc) {
  const points = getPath(data, desc.rowsKey) || [];
  const series = desc.series.map((s, i) => ({
    key: s.key,
    label: s.label,
    color: s.color || SERIES_COLORS[i % SERIES_COLORS.length],
  }));
  const formatValue = desc.format ? (v) => applyFormat(desc.format, v) : (v) => String(Math.round(v));
  return renderLineChart({ points, xKey: desc.xKey, series, formatValue });
}

/* bandlist: desc = { rowsKey, primaryKey, bandKey, bandLabelKey?, reasonKey?, navKey?, emptyText? } */
function vizBandlist(data, desc) {
  const rows = getPath(data, desc.rowsKey) || [];
  if (!rows.length) return emptyStrip(desc.emptyText || "Nothing to show.");
  return el(
    "div",
    { class: "bandlist" },
    rows.map((r) => {
      const band = getPath(r, desc.bandKey);
      const props = { class: "row " + bandClass(band) };
      if (desc.navKey) {
        const target = getPath(r, desc.navKey);
        if (target) props.onClick = () => navigateServer(target);
      }
      return el("div", props, [
        el("span", { class: "dot " + bandClass(band) }),
        el("span", { class: "primary", text: getPath(r, desc.primaryKey) }),
        desc.reasonKey ? el("span", { class: "reason", text: getPath(r, desc.reasonKey) }) : null,
        band ? el("span", { class: "badge " + bandClass(band), text: getPath(r, desc.bandLabelKey) || band }) : null,
      ]);
    })
  );
}

/* ─────────────────────────── shared helpers ─────────────────────────── */

/** Set the hash route to a server's detail page. */
export function navigateServer(serverName) {
  location.hash = "#/server/" + encodeURIComponent(serverName);
}

/**
 * Map a collector/status STRING (already computed server-side) to a severity CSS class — this is coloring a
 * pre-computed label, not re-deriving a band from raw metrics (R1: the browser never re-computes thresholds).
 */
export function statusToSev(status) {
  switch (String(status || "").toUpperCase()) {
    case "HEALTHY":
    case "OK":
    case "SUCCESS":
    case "ONLINE":
      return "Healthy";
    case "STALE":
    case "WARNING":
    case "PERMISSIONS":
    case "SKIPPED":
      return "Warning";
    case "FAILING":
    case "ERROR":
    case "OFFLINE":
      return "Critical";
    default:
      return "Unknown";
  }
}
