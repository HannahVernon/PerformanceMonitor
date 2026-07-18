/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Alert History page (#1562) — the fleet-wide alert log from get_alert_history (server omitted = whole fleet,
 * each row naming its server). A filter box narrows the already-fetched rows by server name client-side (no
 * re-fetch). Detail/error text is untrusted and renders through the table viz's textContent path (R4).
 */

import { el, mount, readTool, loadingStrip, errorStrip, emptyStrip } from "../util.js";
import { VIZ } from "../panels.js";

const ALERT_COLUMNS = [
  { key: "alert_time", label: "Time", format: "time" },
  { key: "server_name", label: "Server" },
  { key: "metric_name", label: "Metric" },
  { key: "current_value", label: "Value", format: "num1" },
  { key: "threshold_value", label: "Threshold", format: "num1" },
  { key: "notification_type", label: "Channel" },
  { key: "alert_sent", label: "Sent", format: "bool" },
  { key: "muted", label: "Muted", format: "bool" },
  { key: "send_error", label: "Delivery Error", wrap: true },
  { key: "detail_text", label: "Detail", wrap: true },
];

export async function renderAlerts(main) {
  const filter = el("input", {
    class: "filter-box",
    type: "text",
    placeholder: "Filter by server…",
    "aria-label": "Filter alerts by server",
  });

  const tableBox = el("div", {});
  mount(main, [
    el("div", { class: "page-head" }, [
      el("h2", { text: "Alert History" }),
      el("div", { class: "meta", text: "fleet-wide · last 24h" }),
      el("div", { class: "spacer" }),
      filter,
    ]),
    tableBox,
  ]);

  mount(tableBox, loadingStrip("Loading alerts…"));
  const res = await readTool("get_alert_history", { hours_back: 24, limit: 200 });
  if (res.kind === "error") return mount(tableBox, errorStrip(res.message));
  if (res.kind === "empty") return mount(tableBox, emptyStrip(res.message));

  const alerts = res.data.alerts || [];

  const draw = () => {
    const q = filter.value.trim().toLowerCase();
    const rows = q ? alerts.filter((a) => String(a.server_name || "").toLowerCase().includes(q)) : alerts;
    if (!rows.length) {
      mount(tableBox, emptyStrip(q ? 'No alerts match "' + filter.value + '".' : "No alerts in this window."));
      return;
    }
    mount(tableBox, VIZ.table({ alerts: rows }, { rowsKey: "alerts", columns: ALERT_COLUMNS }));
  };

  filter.addEventListener("input", draw);
  draw();
}
