/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Fleet Overview page (#1562) — the NOC roll-up from GET /api/fleet. The API is PRE-BANDED: every band, status,
 * and the worst-first ranking are computed server-side by ServerHealthClassifier. This page ONLY renders them;
 * it never re-derives a threshold (R1). The amber "Awaiting first collection" status is rendered exactly as the
 * API reports it (band = Warning, status text verbatim) — never the red offline treatment.
 */

import { el, mount, apiGet, loadingStrip, errorStrip, emptyStrip, localTime, fmtInt, fmtPct, bandClass } from "../util.js";
import { VIZ, navigateServer } from "../panels.js";

const BAND_RANK = { Offline: 0, Critical: 1, Warning: 2, Healthy: 3 };

export async function renderFleet(main) {
  mount(main, [pageHead(null), loadingStrip("Loading fleet…")]);

  const res = await apiGet("/api/fleet");
  if (res.kind === "error") {
    mount(main, [pageHead(null), errorStrip(res.message)]);
    return;
  }

  const d = res.data;
  const nodes = [pageHead(d), rollup(d)];

  if (!d.total_servers) {
    nodes.push(
      emptyStrip("No servers are enabled yet. Add servers to darling.json and cards appear here as collection begins.")
    );
    mount(main, nodes);
    return;
  }

  const problems = d.critical_count + d.warning_count + d.offline_count;
  if (problems === 0) {
    nodes.push(
      el("div", { class: "all-healthy" }, [
        el("span", { class: "dot band-Healthy" }),
        "All " + d.total_servers + " server" + (d.total_servers === 1 ? "" : "s") + " healthy.",
      ])
    );
  } else {
    nodes.push(el("h3", { class: "section-title", text: "Needs attention" }));
    nodes.push(
      VIZ.bandlist(d, {
        rowsKey: "worst_servers",
        primaryKey: "display_name",
        bandKey: "band",
        bandLabelKey: "band_label",
        reasonKey: "reason",
        navKey: "display_name",
      })
    );
    if (d.additional_problem_count > 0) {
      nodes.push(el("div", { class: "muted", style: "margin:0.4rem 0 0.2rem", text: "+ " + d.additional_problem_count + " more need attention" }));
    }
  }

  nodes.push(el("h3", { class: "section-title", style: "margin-top:1.25rem", text: "Servers" }));
  const cards = [...d.cards].sort(
    (a, b) => (BAND_RANK[a.band] ?? 9) - (BAND_RANK[b.band] ?? 9) || a.display_name.localeCompare(b.display_name)
  );
  nodes.push(el("div", { class: "grid" }, cards.map(serverCard)));

  mount(main, nodes);
}

function pageHead(d) {
  return el("div", { class: "page-head" }, [
    el("h2", { text: "Fleet Overview" }),
    el("div", { class: "spacer" }),
    d ? el("div", { class: "meta", text: "Updated " + localTime(d.generated_at) }) : null,
  ]);
}

function rollup(d) {
  const tile = (num, lbl, cls) =>
    el("div", { class: "tile " + (cls || "") }, [
      el("div", { class: "num", text: fmtInt(num) }),
      el("div", { class: "lbl", text: lbl }),
    ]);
  return el("div", { class: "rollup" }, [
    tile(d.total_servers, "Servers"),
    tile(d.healthy_count, "Healthy", "healthy"),
    tile(d.warning_count, "Warning", "warning"),
    tile(d.critical_count, "Critical", "critical"),
    tile(d.offline_count, "Offline", "offline"),
    tile(d.total_blocking_events, "Blocking (window)"),
    tile(d.total_deadlocks, "Deadlocks (window)"),
  ]);
}

function serverCard(c) {
  const cls = bandClass(c.band);
  const statusLine = c.awaiting_first_collection
    ? el("div", { class: "status-line awaiting", text: c.status })
    : el("div", { class: "status-line", text: c.status + " · last collect " + localTime(c.last_collection) });

  return el(
    "div",
    { class: "server-card " + cls, onClick: () => navigateServer(c.server_name || c.display_name) },
    [
      el("div", { class: "head" }, [el("span", { class: "dot " + cls }), el("span", { class: "title", text: c.display_name })]),
      statusLine,
      metricBands(c),
    ]
  );
}

function metricBands(c) {
  const threadsValue =
    c.threads_severity === "Unknown"
      ? "n/a"
      : c.requests_waiting_for_threads > 0
      ? fmtInt(c.requests_waiting_for_threads) + " starved"
      : c.available_threads != null
      ? fmtInt(c.available_threads) + " free"
      : "ok";

  return el("div", { class: "metric-bands" }, [
    chip("CPU", c.total_cpu_percent != null || c.cpu_percent != null ? fmtPct(c.total_cpu_percent ?? c.cpu_percent) : "n/a", c.cpu_severity),
    chip("Threads", threadsValue, c.threads_severity),
    chip("Memory", c.has_memory_pressure ? fmtInt(c.memory_waiter_count) + " waiters" : "ok", c.memory_severity),
    chip("Blocking", fmtInt(c.blocking_count), c.blocking_severity),
    chip("Deadlocks", fmtInt(c.deadlock_count), c.deadlock_severity),
    chip("Collectors", c.failed_collector_count > 0 ? fmtInt(c.failed_collector_count) + " failing" : fmtInt(c.healthy_collector_count) + " ok", c.collector_severity),
  ]);
}

function chip(label, value, sev) {
  return el("div", { class: "metric-chip sev-" + (sev || "Unknown") }, [
    el("div", { class: "label", text: label }),
    el("div", { class: "value", text: value }),
  ]);
}
