/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Dependency-free inline SVG line charts for Darling Web (#1562) — multi-series polylines, theme colors via
 * CSS vars / the caller's series color, and a mousemove tooltip. No chart library, no build step, no remote
 * anything (the WPF viewer uses ScottPlot; this is the honest phase-1 browser equivalent).
 *
 * NOTE (air-gap): SVG_NS is the W3C XML *namespace identifier* required by createElementNS — it is never
 * dereferenced over the network. The self-containment test (DarlingWebSelfContainmentTests) allowlists exactly
 * this string; keep it as the single occurrence in wwwroot.
 */

import { el, parseUtc, axisTime, emptyStrip } from "./util.js";

const SVG_NS = "http://www.w3.org/2000/svg";

/* viewBox geometry — the SVG scales to its container width via CSS (width:100%, height:auto). */
const W = 1000;
const H = 320;
/* Top margin leaves headroom for the y-axis unit caption to sit fully clear of the top tick's label. */
const M = { l: 58, r: 16, t: 26, b: 30 };
const PLOT_W = W - M.l - M.r;
const PLOT_H = H - M.t - M.b;
const Y_TICKS = 4;

function svg(tag, attrs) {
  const node = document.createElementNS(SVG_NS, tag);
  if (attrs) for (const [k, v] of Object.entries(attrs)) if (v != null) node.setAttribute(k, String(v));
  return node;
}

/**
 * Render a multi-series line chart into a returned `.chart` node.
 * spec: { points, xKey, series:[{key,label,color}], formatValue?:(v)=>string, clampMax?:number, unit?:string }
 *   points    — array of row objects; each row[xKey] is a naive-UTC ISO string, each row[series.key] a number.
 *   clampMax  — cap the y-axis top at this value (percentage charts pass 100 so the domain never exceeds 100).
 *   unit      — a short y-axis unit caption ("%", "ms", "ms/s", ...).
 */
export function renderLineChart(spec) {
  const { points, xKey, series, formatValue = (v) => String(v), clampMax = null, unit = null } = spec;

  /* Parse + sort the x axis (naive UTC -> real Date via parseUtc). */
  const rows = (points || [])
    .map((r) => ({ t: parseUtc(r[xKey]), r }))
    .filter((p) => p.t)
    .sort((a, b) => a.t - b.t);

  if (rows.length < 2) {
    return el("div", { class: "chart" }, [emptyStrip("Not enough data points to chart yet.")]);
  }

  const tMin = rows[0].t.getTime();
  const tMax = rows[rows.length - 1].t.getTime();
  const spanMs = tMax - tMin;

  /* y domain across every series; 0-baselined for these non-negative metrics (honest scale). */
  let dataMax = -Infinity;
  let dataMin = Infinity;
  for (const { r } of rows) {
    for (const s of series) {
      const v = r[s.key];
      if (v == null || isNaN(v)) continue;
      if (v > dataMax) dataMax = v;
      if (v < dataMin) dataMin = v;
    }
  }
  if (dataMax === -Infinity) return el("div", { class: "chart" }, [emptyStrip("No numeric values to chart.")]);
  dataMin = Math.min(0, dataMin);
  if (dataMax === dataMin) dataMax = dataMin + 1;

  /* Nice-rounded domain so gridline labels land on round values; percentage charts (clampMax=100) cap the top
     at 100 and never exceed it, so a 96% reading no longer rounds the axis up to a "120%" tick. */
  const scale = niceScale(dataMin, dataMax, Y_TICKS, clampMax);
  const yMin = scale.min;
  const yMax = scale.max;

  const scaleX = (t) => M.l + ((t - tMin) / (spanMs || 1)) * PLOT_W;
  const scaleY = (v) => M.t + (1 - (v - yMin) / (yMax - yMin)) * PLOT_H;
  /* Plotted points clamp into the plot box so a value above a clamped (pct) domain can't draw outside it. */
  const plotY = (v) => Math.max(M.t, Math.min(M.t + PLOT_H, scaleY(v)));

  const root = svg("svg", { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: "none", role: "img" });

  /* Horizontal gridlines + y labels (on the nice tick values). */
  const axis = svg("g", { class: "axis" });
  for (const val of scale.ticks) {
    const y = scaleY(val);
    axis.appendChild(svg("line", { class: "grid-line", x1: M.l, y1: y, x2: W - M.r, y2: y }));
    const label = svg("text", { x: M.l - 8, y: y + 4, "text-anchor": "end" });
    label.textContent = formatValue(val);
    axis.appendChild(label);
  }

  /* Y-axis unit caption. Skipped for "%" (the tick labels already carry the unit, and stacking a caption
     on the top tick collided with its label — the design review's must-fix); for bare-number axes it sits
     in the extra top headroom reserved above (well clear of the top tick's label). */
  if (unit && unit !== "%") {
    const cap = svg("text", { class: "axis-unit", x: M.l - 8, y: 11, "text-anchor": "end" });
    cap.textContent = unit;
    axis.appendChild(cap);
  }

  /* Vertical gridlines + x labels (6 ticks). The label widens to include the calendar date when the domain
     spans more than one day, so a window crossing midnight is unambiguous even if it is under 24h wide. */
  const crossesDay = rows[0].t.toDateString() !== rows[rows.length - 1].t.toDateString();
  const X_TICKS = 5;
  for (let i = 0; i <= X_TICKS; i++) {
    const t = tMin + (spanMs * i) / X_TICKS;
    const x = scaleX(t);
    axis.appendChild(svg("line", { class: "grid-line", x1: x, y1: M.t, x2: x, y2: M.t + PLOT_H }));
    const label = svg("text", {
      x: Math.min(Math.max(x, M.l + 2), W - M.r - 2),
      y: H - 8,
      "text-anchor": i === 0 ? "start" : i === X_TICKS ? "end" : "middle",
    });
    label.textContent = axisTime(new Date(t), crossesDay);
    axis.appendChild(label);
  }
  root.appendChild(axis);

  /* One polyline per series (nulls dropped so a gap does not draw to zero). */
  for (const s of series) {
    const pts = [];
    for (const { t, r } of rows) {
      const v = r[s.key];
      if (v == null || isNaN(v)) continue;
      pts.push(scaleX(t.getTime()) + "," + plotY(v));
    }
    if (pts.length < 2) continue;
    root.appendChild(svg("polyline", { class: "series-line", points: pts.join(" "), stroke: s.color }));
  }

  /* Hover overlay: a transparent rect over the plot capturing mousemove. */
  const hoverLine = svg("line", { class: "hover-line", y1: M.t, y2: M.t + PLOT_H, style: "display:none" });
  root.appendChild(hoverLine);
  const hoverDots = svg("g", { style: "display:none" });
  root.appendChild(hoverDots);
  const overlay = svg("rect", { x: M.l, y: M.t, width: PLOT_W, height: PLOT_H, fill: "transparent" });
  root.appendChild(overlay);

  const chart = el("div", { class: "chart" }, [root]);
  const tooltip = el("div", { class: "chart-tooltip" });
  chart.appendChild(tooltip);
  chart.appendChild(buildLegend(series));

  const xs = rows.map((p) => scaleX(p.t.getTime()));

  overlay.addEventListener("mousemove", (ev) => {
    const rect = root.getBoundingClientRect();
    const vbX = ((ev.clientX - rect.left) / rect.width) * W;
    let idx = 0;
    let best = Infinity;
    for (let i = 0; i < xs.length; i++) {
      const d = Math.abs(xs[i] - vbX);
      if (d < best) {
        best = d;
        idx = i;
      }
    }
    const { t, r } = rows[idx];
    const px = xs[idx];

    hoverLine.setAttribute("x1", px);
    hoverLine.setAttribute("x2", px);
    hoverLine.style.display = "";

    while (hoverDots.firstChild) hoverDots.removeChild(hoverDots.firstChild);
    for (const s of series) {
      const v = r[s.key];
      if (v == null || isNaN(v)) continue;
      hoverDots.appendChild(svg("circle", { class: "hover-dot", cx: px, cy: plotY(v), r: 3.5, fill: s.color }));
    }
    hoverDots.style.display = "";

    /* Tooltip is built with textContent only (values may include untrusted series labels). */
    while (tooltip.firstChild) tooltip.removeChild(tooltip.firstChild);
    tooltip.appendChild(el("div", { class: "t-time", text: t.toLocaleString() }));
    for (const s of series) {
      const v = r[s.key];
      tooltip.appendChild(
        el("div", { class: "t-row" }, [
          el("span", { class: "swatch", style: "background:" + s.color }),
          el("span", { text: s.label }),
          el("span", { class: "t-val", text: v == null || isNaN(v) ? "—" : formatValue(v) }),
        ])
      );
    }
    const renderedX = (px / W) * rect.width;
    tooltip.style.display = "block";
    tooltip.style.left = Math.min(renderedX + 12, rect.width - tooltip.offsetWidth - 4) + "px";
    tooltip.style.top = "8px";
  });

  overlay.addEventListener("mouseleave", () => {
    hoverLine.style.display = "none";
    hoverDots.style.display = "none";
    tooltip.style.display = "none";
  });

  return chart;
}

function buildLegend(series) {
  return el(
    "div",
    { class: "chart-legend" },
    series.map((s) =>
      el("span", { class: "item" }, [
        el("span", { class: "swatch", style: "background:" + s.color }),
        el("span", { text: s.label }),
      ])
    )
  );
}

/**
 * A NEUTRAL categorical ramp for chart series — deliberately NOT the ok/warn/err severity colors, so a chart's
 * lines never imply a health state (the alert/band palette stays severity-only). Distinct, colorblind-tolerant.
 */
export const SERIES_COLORS = ["#2eaef1", "#4dd0e1", "#b39ddb", "#7f8fa6", "#e0e0e0"];

/** Classic "nice number" rounding (Heckbert): the round-friendly value at or just past `range`. */
function niceNum(range, round) {
  if (!(range > 0) || !isFinite(range)) return 1;
  const exp = Math.floor(Math.log10(range));
  const frac = range / Math.pow(10, exp);
  let nf;
  if (round) {
    nf = frac < 1.5 ? 1 : frac < 3 ? 2 : frac < 7 ? 5 : 10;
  } else {
    nf = frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10;
  }
  return nf * Math.pow(10, exp);
}

/**
 * A "nice" y-axis over [min, max]: rounded bounds and evenly-spaced tick values that land on round numbers.
 * `clampMax` caps the top (percentage charts pass 100 so the axis never exceeds 100%).
 */
function niceScale(min, max, maxTicks, clampMax) {
  const range = niceNum(max - min || 1, false);
  const step = niceNum(range / Math.max(1, maxTicks), true) || 1;
  const niceMin = Math.floor(min / step) * step;
  let niceMax = Math.ceil(max / step) * step;
  if (clampMax != null && niceMax > clampMax) niceMax = clampMax;
  const n = Math.max(1, Math.round((niceMax - niceMin) / step));
  const ticks = [];
  for (let i = 0; i <= n; i++) {
    const v = niceMin + i * step;
    ticks.push(v > niceMax ? niceMax : v);
  }
  return { min: niceMin, max: niceMax, step, ticks };
}
