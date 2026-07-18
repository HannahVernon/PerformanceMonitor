/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Darling Web entry point (#1562): the hash router, the sidebar server list, and the refresh loop. Routes:
 *   #/fleet            — Fleet Overview (default)
 *   #/server/{name}    — one server's detail
 *   #/alerts           — fleet-wide Alert History
 * The refresh loop re-renders the active page every 60s and PAUSES while the tab is hidden (the interval skips
 * work when document.hidden), refreshing once immediately when the tab becomes visible again.
 */

import { el, mount, apiGet, bandClass, localTime } from "./util.js";
import { navigateServer } from "./panels.js";
import { renderFleet } from "./pages/fleet.js";
import { renderServer } from "./pages/server.js";
import { renderAlerts } from "./pages/alerts.js";

const POLL_MS = 60000;

const main = document.getElementById("main");
const serverList = document.getElementById("server-list");
const statusbar = document.getElementById("statusbar");

/* ─────────────────────────── routing ─────────────────────────── */

function currentRoute() {
  const h = location.hash || "#/fleet";
  if (h.startsWith("#/server/")) return { name: "server", param: decodeURIComponent(h.slice("#/server/".length)) };
  if (h.startsWith("#/alerts")) return { name: "alerts" };
  return { name: "fleet" };
}

function route() {
  const r = currentRoute();
  setActiveNav(r);
  if (r.name === "server") renderServer(main, r.param);
  else if (r.name === "alerts") renderAlerts(main);
  else renderFleet(main);
}

function setActiveNav(r) {
  document.querySelectorAll(".nav a").forEach((a) => a.classList.toggle("active", a.dataset.route === r.name));
  updateServerActive(r);
}

function updateServerActive(r) {
  serverList.querySelectorAll(".server-item").forEach((item) => {
    const active = r.name === "server" && (item.dataset.server === r.param || item.dataset.display === r.param);
    item.classList.toggle("active", active);
  });
}

/* ─────────────────────────── sidebar ─────────────────────────── */

async function refreshSidebar() {
  const res = await apiGet("/api/fleet");
  if (res.kind !== "data") {
    mount(serverList, el("div", { class: "muted", style: "padding:0.5rem 1.25rem", text: res.kind === "error" ? "Fleet unavailable" : "" }));
    updateStatusBar(null);
    return;
  }

  const cards = [...(res.data.cards || [])].sort((a, b) => a.display_name.localeCompare(b.display_name));
  const r = currentRoute();
  mount(
    serverList,
    cards.map((c) => {
      const target = c.server_name || c.display_name;
      const active = r.name === "server" && (r.param === c.server_name || r.param === c.display_name);
      return el(
        "div",
        {
          class: "server-item" + (active ? " active" : ""),
          dataset: { server: target, display: c.display_name },
          onActivate: () => navigateServer(target),
        },
        [el("span", { class: "dot " + bandClass(c.band) }), el("span", { class: "name", text: c.display_name })]
      );
    })
  );
  updateStatusBar(res.data);
}

/* ─────────────────────────── status bar ─────────────────────────── */

/* A fixed footer mirroring the WPF viewer's status bar: fleet server count, collectors healthy/failing across
   the fleet, and the last refresh time. Built from the SAME /api/fleet response the sidebar just read (no extra
   round-trip). Store size has no web endpoint, so it is deliberately omitted here. */
function updateStatusBar(d) {
  if (!statusbar) return;
  if (!d) {
    mount(statusbar, el("span", { class: "sb-item muted", text: "Fleet unavailable" }));
    return;
  }
  let healthy = 0;
  let failing = 0;
  for (const c of d.cards || []) {
    healthy += c.healthy_collector_count || 0;
    failing += c.failed_collector_count || 0;
  }
  const servers = d.total_servers || 0;
  mount(statusbar, [
    el("span", { class: "sb-item", text: servers + (servers === 1 ? " server" : " servers") }),
    el("span", { class: "sb-sep", text: "·" }),
    el("span", { class: "sb-item", text: healthy + " collectors healthy · " + failing + " failing" }),
    el("span", { class: "sb-sep", text: "·" }),
    el("span", { class: "sb-item", text: "Updated " + localTime(d.generated_at) }),
  ]);
}

/* ─────────────────────────── refresh loop ─────────────────────────── */

function refresh() {
  refreshSidebar();
  route();
}

function start() {
  window.addEventListener("hashchange", route);
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) refresh();
  });
  setInterval(() => {
    if (!document.hidden) refresh();
  }, POLL_MS);

  refreshSidebar();
  route();
}

start();
