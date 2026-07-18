/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Server detail page (#1562) — the per-server drill-down reached by clicking a fleet card. Most sections are
 * plain panel DESCRIPTORS run through renderPanel (the #1563 seam). Two are composites the page owns because
 * they chain reads or reshape rows: Wait Stats (the table + a trend of the HEAVIEST wait) and File I/O (the
 * flat per-(time,database) trend pivoted into one read-latency series per database).
 */

import { el, mount, readTool, loadingStrip, errorStrip, emptyStrip } from "../util.js";
import { renderPanel, VIZ } from "../panels.js";
import { renderLineChart, SERIES_COLORS } from "../charts.js";

export function renderServer(main, server) {
  const head = el("div", { class: "page-head" }, [
    el("a", { href: "#/fleet", text: "← Fleet" }),
    el("h2", { text: server }),
  ]);

  mount(main, [
    head,
    renderPanel({ title: "Overview", read: "get_server_summary", params: { server }, viz: "stat", stats: OVERVIEW_STATS }),
    renderPanel({
      title: "CPU Utilization",
      subtitle: "last 4h",
      read: "get_cpu_utilization",
      params: { server, hours_back: 4 },
      viz: "line",
      rowsKey: "samples",
      xKey: "sample_time",
      series: CPU_SERIES,
      format: "pct",
    }),
    waitsPanel(server),
    renderPanel({
      title: "Active Queries",
      subtitle: "last 1h",
      read: "get_active_queries",
      params: { server, hours_back: 1, limit: 50 },
      viz: "table",
      rowsKey: "queries",
      columns: ACTIVE_COLUMNS,
    }),
    renderPanel({
      title: "Memory Trend",
      subtitle: "last 24h",
      read: "get_memory_trend",
      params: { server, hours_back: 24 },
      viz: "line",
      rowsKey: "trend",
      xKey: "time",
      series: MEMORY_SERIES,
      format: "mb",
    }),
    fileIoPanel(server),
    renderPanel({
      title: "Collection Health",
      subtitle: "trailing 7 days",
      read: "get_collection_health",
      params: { server },
      viz: "table",
      rowsKey: "collectors",
      columns: COLLECTOR_COLUMNS,
    }),
  ]);
}

/* ─────────────────────────── composites ─────────────────────────── */

function panelShell(title, subtitle) {
  const body = el("div", { class: "panel-body" }, [loadingStrip()]);
  const panel = el("div", { class: "panel card" }, [
    el("h3", {}, [title, subtitle ? el("span", { class: "panel-sub", text: " " + subtitle }) : null]),
    body,
  ]);
  return { panel, body };
}

/* Wait Stats table + a trend line for the single heaviest wait type. */
function waitsPanel(server) {
  const { panel, body } = panelShell("Wait Stats", "last 24h, with a trend for the heaviest wait");
  (async () => {
    const res = await readTool("get_wait_stats", { server, hours_back: 24 });
    if (res.kind === "error") return mount(body, errorStrip(res.message));
    if (res.kind === "empty") return mount(body, emptyStrip(res.message));

    const waits = res.data.waits || [];
    const parts = [VIZ.table(res.data, { rowsKey: "waits", columns: WAIT_COLUMNS })];

    if (waits.length) {
      const top = waits[0].wait_type;
      parts.push(el("div", { class: "muted", style: "margin-top:0.85rem;margin-bottom:0.3rem", text: "Trend — " + top }));
      const trend = await readTool("get_wait_trend", { server, wait_type: top, hours_back: 24 });
      if (trend.kind === "data") {
        parts.push(
          renderLineChart({
            points: trend.data.trend || [],
            xKey: "time",
            series: [
              { key: "wait_time_ms_per_second", label: "Wait ms/s", color: "var(--accent)" },
              { key: "signal_wait_time_ms_per_second", label: "Signal ms/s", color: "var(--warn)" },
            ],
            formatValue: (v) => Math.round(v).toLocaleString(),
          })
        );
      } else {
        parts.push(trend.kind === "empty" ? emptyStrip(trend.message) : errorStrip(trend.message));
      }
    }
    mount(body, parts);
  })();
  return panel;
}

/* File I/O latency: pivot the flat per-(time, database) trend into one read-latency series per database. */
function fileIoPanel(server) {
  const { panel, body } = panelShell("File I/O Latency", "avg read latency per database, last 24h");
  (async () => {
    const res = await readTool("get_file_io_trend", { server, hours_back: 24 });
    if (res.kind === "error") return mount(body, errorStrip(res.message));
    if (res.kind === "empty") return mount(body, emptyStrip(res.message));

    const { points, series } = pivot(res.data.trend || [], {
      xKey: "time",
      seriesKey: "database_name",
      valueKey: "avg_read_latency_ms",
    });
    if (!series.length) return mount(body, emptyStrip("No file I/O samples in this window."));
    mount(body, renderLineChart({ points, xKey: "time", series, formatValue: (v) => Math.round(v) + " ms" }));
  })();
  return panel;
}

/** Reshape flat rows into per-series points, keeping the top `maxSeries` series by peak value. */
function pivot(rows, { xKey, seriesKey, valueKey }, maxSeries = 8) {
  const byTime = new Map();
  const peak = new Map();
  for (const r of rows) {
    const t = r[xKey];
    const name = r[seriesKey];
    const v = r[valueKey];
    if (t == null || name == null) continue;
    if (!byTime.has(t)) byTime.set(t, { [xKey]: t });
    byTime.get(t)[name] = v;
    peak.set(name, Math.max(peak.get(name) ?? -Infinity, v ?? -Infinity));
  }
  const names = [...peak.keys()].sort((a, b) => peak.get(b) - peak.get(a)).slice(0, maxSeries);
  const points = [...byTime.values()].sort((a, b) => String(a[xKey]).localeCompare(String(b[xKey])));
  const series = names.map((n, i) => ({ key: n, label: n, color: SERIES_COLORS[i % SERIES_COLORS.length] }));
  return { points, series };
}

/* ─────────────────────────── descriptors ─────────────────────────── */

const OVERVIEW_STATS = [
  { key: "cpu_percent", label: "CPU", format: "pct" },
  { key: "memory_mb", label: "Memory", format: "mb" },
  { key: "blocking_count", label: "Blocking (recent)", format: "int" },
  { key: "deadlock_count", label: "Deadlocks (recent)", format: "int" },
  { key: "last_collection", label: "Last collection", format: "time" },
];

const CPU_SERIES = [
  { key: "sql_server_cpu", label: "SQL CPU %", color: "var(--accent)" },
  { key: "other_process_cpu", label: "Other %", color: "var(--warn)" },
  { key: "total_cpu", label: "Total %", color: "var(--err)" },
  { key: "idle_cpu", label: "Idle %", color: "var(--muted)" },
];

const MEMORY_SERIES = [
  { key: "total_server_memory_mb", label: "Total Server", color: "var(--accent)" },
  { key: "target_server_memory_mb", label: "Target", color: "var(--warn)" },
  { key: "buffer_pool_mb", label: "Buffer Pool", color: "var(--ok)" },
  { key: "plan_cache_mb", label: "Plan Cache", color: "var(--dim)" },
];

const WAIT_COLUMNS = [
  { key: "wait_type", label: "Wait Type" },
  { key: "total_wait_time_ms", label: "Total Wait", format: "ms" },
  { key: "resource_wait_ms", label: "Resource", format: "ms" },
  { key: "total_signal_wait_ms", label: "Signal", format: "ms" },
  { key: "waiting_tasks", label: "Tasks", format: "int" },
  { key: "signal_wait_pct", label: "Signal %", format: "num1" },
];

const ACTIVE_COLUMNS = [
  { key: "collection_time", label: "Time", format: "time" },
  { key: "session_id", label: "SPID", format: "int" },
  { key: "database_name", label: "Database" },
  { key: "status", label: "Status" },
  { key: "cpu_time_ms", label: "CPU", format: "ms" },
  { key: "elapsed_time_formatted", label: "Elapsed" },
  { key: "wait_type", label: "Wait" },
  { key: "blocking_session_id", label: "Blocked by", format: "int" },
  { key: "query_text", label: "Query", wrap: true, mono: true },
];

const COLLECTOR_COLUMNS = [
  { key: "collector", label: "Collector" },
  { key: "status", label: "Status", statusSev: true },
  { key: "total_runs", label: "Runs", format: "int" },
  { key: "errors", label: "Errors", format: "int" },
  { key: "failure_rate_pct", label: "Failure %", format: "num1" },
  { key: "avg_duration_ms", label: "Avg Dur", format: "ms" },
  { key: "last_success", label: "Last Success", format: "time" },
  { key: "last_error", label: "Last Error", wrap: true },
];
