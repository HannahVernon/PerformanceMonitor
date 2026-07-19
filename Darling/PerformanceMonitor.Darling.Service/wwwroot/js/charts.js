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
 * Render a multi-series time chart into a returned `.chart` node.
 * spec: { points, xKey, series:[{key,label,color}], formatValue?, clampMax?, unit?, mode? }
 *   points    — array of row objects; each row[xKey] is a naive-UTC ISO string, each row[series.key] a number.
 *   clampMax  — cap the y-axis top at this value (percentage charts pass 100 so the domain never exceeds 100).
 *   unit      — a short y-axis unit caption ("%", "ms", "ms/s", ...).
 *   mode      — "line" (default; plain polylines), "area" (each series filled to the baseline), or "stacked"
 *               (series stacked on one another — the "parts of a whole over time" view; the y-domain is the
 *               per-bucket stack SUM). "line"/absent is byte-for-byte the original behavior.
 */
export function renderLineChart(spec) {
  const { points, xKey, series, formatValue = (v) => String(v), clampMax = null, unit = null, mode = "line" } = spec;
  const stacked = mode === "stacked";
  const filled = mode === "area" || stacked;

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

  /* A numeric reader: null/NaN reads as null for line/area (a gap), or 0 for stacked (a continuous baseline). */
  const readVal = (r, key) => {
    const v = r[key];
    return v == null || isNaN(v) ? (stacked ? 0 : null) : Number(v);
  };

  /* Stacked pre-pass: cumulative top per series at each row (stackTops[i][k] = sum of series 0..k at row i). */
  const stackTops = stacked ? rows.map(() => new Array(series.length).fill(0)) : null;

  /* y domain. Stacked: 0..max stack sum. Line/area: across every series (0-baselined; non-negative metrics). */
  let dataMax = -Infinity;
  let dataMin = Infinity;
  if (stacked) {
    for (let i = 0; i < rows.length; i++) {
      let running = 0;
      for (let k = 0; k < series.length; k++) {
        running += readVal(rows[i].r, series[k].key);
        stackTops[i][k] = running;
      }
      if (running > dataMax) dataMax = running;
    }
    dataMin = 0;
    if (dataMax === -Infinity) dataMax = 1;
  } else {
    for (const { r } of rows) {
      for (const s of series) {
        const v = readVal(r, s.key);
        if (v == null) continue;
        if (v > dataMax) dataMax = v;
        if (v < dataMin) dataMin = v;
      }
    }
    if (dataMax === -Infinity) return el("div", { class: "chart" }, [emptyStrip("No numeric values to chart.")]);
    dataMin = Math.min(0, dataMin);
  }
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
  const baseY = plotY(0);

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

  const xs = rows.map((p) => scaleX(p.t.getTime()));

  if (stacked) {
    /* Filled bands drawn top series first so lower bands paint over the seams; each band is bounded above by its
       own cumulative top and below by the previous series' cumulative top (the x-axis for series 0). */
    for (let k = series.length - 1; k >= 0; k--) {
      const top = [];
      const bottom = [];
      for (let i = 0; i < rows.length; i++) {
        top.push(xs[i] + "," + plotY(stackTops[i][k]));
        bottom.push(xs[i] + "," + plotY(k === 0 ? 0 : stackTops[i][k - 1]));
      }
      bottom.reverse();
      root.appendChild(
        svg("polygon", {
          class: "series-area",
          points: top.concat(bottom).join(" "),
          fill: normalizeColor(series[k].color),
          "fill-opacity": "0.72",
        })
      );
    }
  } else {
    /* Area fill (each series to the baseline) then the line on top; or just the line. Nulls drop the gap. */
    for (const s of series) {
      const linePts = [];
      for (let i = 0; i < rows.length; i++) {
        const v = readVal(rows[i].r, s.key);
        if (v == null) continue;
        linePts.push(xs[i] + "," + plotY(v));
      }
      if (linePts.length < 2) continue;
      if (filled) {
        const first = linePts[0].split(",")[0];
        const last = linePts[linePts.length - 1].split(",")[0];
        root.appendChild(
          svg("polygon", {
            class: "series-area",
            points: `${first},${baseY} ${linePts.join(" ")} ${last},${baseY}`,
            fill: normalizeColor(s.color),
            "fill-opacity": "0.15",
          })
        );
      }
      root.appendChild(svg("polyline", { class: "series-line", points: linePts.join(" "), stroke: normalizeColor(s.color) }));
    }
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

    /* Dots sit at each series' plotted position: its cumulative top when stacked, its own value otherwise. */
    while (hoverDots.firstChild) hoverDots.removeChild(hoverDots.firstChild);
    for (let k = 0; k < series.length; k++) {
      const v = readVal(r, series[k].key);
      if (!stacked && v == null) continue;
      const cy = stacked ? plotY(stackTops[idx][k]) : plotY(v);
      hoverDots.appendChild(svg("circle", { class: "hover-dot", cx: px, cy, r: 3.5, fill: normalizeColor(series[k].color) }));
    }
    hoverDots.style.display = "";

    /* Tooltip is built with textContent only (values may include untrusted series labels). */
    while (tooltip.firstChild) tooltip.removeChild(tooltip.firstChild);
    tooltip.appendChild(el("div", { class: "t-time", text: t.toLocaleString() }));
    for (const s of series) {
      const v = r[s.key];
      tooltip.appendChild(
        el("div", { class: "t-row" }, [
          el("span", { class: "swatch", style: "background:" + normalizeColor(s.color) }),
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

/**
 * Render a horizontal RANKED bar chart into a returned `.chart` node (the compose "bar" viz — a topN result).
 * spec: { items:[{label,value,color?}], formatValue?, unit? } — items already ordered (value DESC) + bounded by
 * the query's topN. Bars scale to the largest value; each carries a native SVG <title> for the full label+value.
 * Rendered at a per-row height so a tall list stays readable (the container scrolls; the SVG never squashes).
 */
export function renderBarChart(spec) {
  const { items, formatValue = (v) => String(v) } = spec;
  const rows = (items || []).filter((d) => d && d.value != null && !isNaN(d.value));
  if (!rows.length) return el("div", { class: "chart" }, [emptyStrip("No values to chart.")]);

  const shown = rows.slice(0, MAX_BARS);
  const maxVal = Math.max(0, ...shown.map((d) => Number(d.value)));
  const domainMax = maxVal > 0 ? maxVal : 1;

  const rowH = 26;
  const gap = 8;
  const labelW = 220;
  const valueW = 110;
  const barLeft = labelW + 8;
  const barW = W - barLeft - valueW;
  const height = M.t + shown.length * (rowH + gap);

  const root = svg("svg", { viewBox: `0 0 ${W} ${height}`, preserveAspectRatio: "xMinYMin meet", role: "img" });

  shown.forEach((d, i) => {
    const y = M.t + i * (rowH + gap);
    const val = Number(d.value);
    const w = Math.max(1, (val / domainMax) * barW);
    const color = normalizeColor(d.color);

    const label = svg("text", { class: "bar-label", x: labelW, y: y + rowH * 0.7, "text-anchor": "end" });
    label.textContent = trunclabel(d.label);

    const track = svg("rect", { class: "bar-track", x: barLeft, y, width: barW, height: rowH, rx: 3 });
    const bar = svg("rect", { class: "bar", x: barLeft, y, width: w, height: rowH, rx: 3, fill: color });
    const title = svg("title");
    title.textContent = (d.label == null || d.label === "" ? "—" : String(d.label)) + " · " + formatValue(val);
    bar.appendChild(title);

    const value = svg("text", { class: "bar-value", x: barLeft + w + 6, y: y + rowH * 0.7 });
    value.textContent = formatValue(val);

    root.appendChild(label);
    root.appendChild(track);
    root.appendChild(bar);
    root.appendChild(value);
  });

  const chart = el("div", { class: "chart chart-bar" }, [root]);
  if (rows.length > MAX_BARS) {
    chart.appendChild(el("div", { class: "chart-note", text: `Showing the top ${MAX_BARS} of ${rows.length}.` }));
  }
  return chart;
}

/**
 * Render a RANKED donut chart into a returned `.chart` node (the compose "pie" viz). Slices beyond MAX_SLICES are
 * pooled into an "Other" wedge so a long tail stays legible; the center carries the grand total. A legend with
 * per-slice values sits below. Slice/legend text is textContent-only (labels may be untrusted).
 */
export function renderPieChart(spec) {
  const { items, formatValue = (v) => String(v) } = spec;
  const rows = (items || [])
    .filter((d) => d && d.value != null && !isNaN(d.value) && Number(d.value) > 0)
    .map((d) => ({ label: d.label == null || d.label === "" ? "—" : String(d.label), value: Number(d.value), color: d.color }));
  if (!rows.length) return el("div", { class: "chart" }, [emptyStrip("No positive values to chart.")]);

  /* Pool the long tail into "Other" so the donut never fans into unreadable slivers. */
  let slices = rows;
  if (rows.length > MAX_SLICES) {
    const head = rows.slice(0, MAX_SLICES - 1);
    const tail = rows.slice(MAX_SLICES - 1);
    const otherValue = tail.reduce((sum, d) => sum + d.value, 0);
    slices = head.concat([{ label: `Other (${tail.length})`, value: otherValue, color: NEUTRAL_SLICE }]);
  }
  slices.forEach((d, i) => {
    if (!d.color) d.color = CATEGORICAL_COLORS[i % CATEGORICAL_COLORS.length];
  });

  const total = slices.reduce((sum, d) => sum + d.value, 0);
  const size = 260;
  const cx = size / 2;
  const cy = size / 2;
  const rOuter = size / 2 - 6;
  const rInner = rOuter * 0.58;

  const root = svg("svg", { viewBox: `0 0 ${size} ${size}`, class: "pie-svg", role: "img" });
  let angle = -90;
  for (const d of slices) {
    /* Clamp a lone 100% slice below a full turn — a 360° SVG arc (start == end point) renders nothing. */
    const sweep = Math.min((d.value / total) * 360, 359.999);
    const path = svg("path", { class: "pie-slice", d: donutArc(cx, cy, rOuter, rInner, angle, angle + sweep), fill: normalizeColor(d.color) });
    const title = svg("title");
    title.textContent = `${d.label} · ${formatValue(d.value)} (${Math.round((d.value / total) * 100)}%)`;
    path.appendChild(title);
    root.appendChild(path);
    angle += sweep;
  }

  const centerTotal = svg("text", { class: "pie-center", x: cx, y: cy - 2, "text-anchor": "middle" });
  centerTotal.textContent = formatValue(total);
  const centerLabel = svg("text", { class: "pie-center-label", x: cx, y: cy + 14, "text-anchor": "middle" });
  centerLabel.textContent = "total";
  root.appendChild(centerTotal);
  root.appendChild(centerLabel);

  const legend = el(
    "div",
    { class: "chart-legend pie-legend" },
    slices.map((d) =>
      el("span", { class: "item" }, [
        el("span", { class: "swatch dot", style: "background:" + normalizeColor(d.color) }),
        el("span", { text: d.label }),
        el("span", { class: "leg-val", text: formatValue(d.value) }),
      ])
    )
  );

  return el("div", { class: "chart chart-pie" }, [el("div", { class: "pie-wrap" }, [root]), legend]);
}

/** Bounds on how much a ranked chart draws before it stops reading (the container scrolls a bar list; a pie pools). */
const MAX_BARS = 30;
const MAX_SLICES = 9;

/** Truncate a bar's category label to keep it inside the label gutter. */
function trunclabel(s) {
  const flat = String(s == null || s === "" ? "—" : s);
  return flat.length > 30 ? flat.slice(0, 29) + "…" : flat;
}

/** SVG path `d` for a donut wedge from a1° to a2° (0° = 3 o'clock, angles increase clockwise in SVG's y-down space). */
function donutArc(cx, cy, rOuter, rInner, a1, a2) {
  const p = (r, a) => {
    const rad = (a * Math.PI) / 180;
    return [cx + r * Math.cos(rad), cy + r * Math.sin(rad)];
  };
  const large = a2 - a1 > 180 ? 1 : 0;
  const [ox1, oy1] = p(rOuter, a1);
  const [ox2, oy2] = p(rOuter, a2);
  const [ix2, iy2] = p(rInner, a2);
  const [ix1, iy1] = p(rInner, a1);
  return (
    `M ${ox1} ${oy1} A ${rOuter} ${rOuter} 0 ${large} 1 ${ox2} ${oy2} ` +
    `L ${ix2} ${iy2} A ${rInner} ${rInner} 0 ${large} 0 ${ix1} ${iy1} Z`
  );
}

function buildLegend(series) {
  return el(
    "div",
    { class: "chart-legend" },
    series.map((s) =>
      el("span", { class: "item" }, [
        el("span", { class: "swatch", style: "background:" + normalizeColor(s.color) }),
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

/**
 * The wider categorical palette for composed multi-series / bar / pie charts (#1563) — the five SERIES_COLORS
 * (so the family reads the same) plus five more cool/neutral hues, staying clear of the ok/warn/err severity
 * colors for the same reason. All #rrggbb literals (air-gap safe); cycled when a chart has more categories.
 */
export const CATEGORICAL_COLORS = [
  "#2eaef1", "#4dd0e1", "#b39ddb", "#7f8fa6", "#e0e0e0",
  "#64b5f6", "#9575cd", "#4fc3f7", "#ba68c8", "#90a4ae",
];

/** The muted color for a pie's pooled "Other" wedge — a neutral gray that recedes behind the real categories. */
const NEUTRAL_SLICE = "#5b626e";

/**
 * Presentation guard (defense-in-depth behind the server-side ValidateDefinition authority): a series color
 * reaches a style="background:<color>" sink in the tooltip/legend swatches, so a stored definition's color must
 * be a #rrggbb hex literal (case-insensitive). Anything else — a named color, a short #abc, a CSS function that
 * would fetch off-origin and defeat the air-gap, or a non-string — falls back to a safe palette default. Mirrors
 * the composer's normalizeColor (editor.js imports this one) so the two can never drift.
 */
export function normalizeColor(c) {
  return typeof c === "string" && /^#[0-9a-fA-F]{6}$/.test(c) ? c : SERIES_COLORS[0];
}

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
