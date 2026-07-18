/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * The #1563 custom-view COMPOSER — the loopback-only editor for a view (name + description + an ordered panel
 * list). Each panel is a renderPanel descriptor built through the SAME viz registry the renderer uses, previewed
 * LIVE (debounced) through renderPanel so the operator sees exactly what will be stored.
 *
 *   read picker (grouped by catalog category) -> typed param inputs from the catalog (int/double -> number,
 *   bool -> checkbox, text -> input, server -> a fleet dropdown; a required param blocks preview + save until
 *   filled) -> viz picker -> width (span 1|2) -> title -> a vizcfg sub-editor SEEDED by client-side derivation
 *   (derive.js) from a live sample fetch, then hand-tunable (columns/series/stats + a format per field).
 *
 * SECURITY: editing is loopback-only, enforced SERVER-SIDE; can_edit==false renders a read-only notice (defence
 * in depth — the nav/renderer already hide the affordances). All user text reaches the DOM via el()/textContent
 * (R4 — el() throws on an html prop). Series COLOR is constrained to an <input type=color> (#rrggbb) + the
 * charts.js palette, NEVER free text, so it can't inject a style-attribute sink (reconciliation #4).
 *
 * The 60s poll in app.js SKIPS re-rendering this route (the editor-route poll guard), so an in-progress edit is
 * never clobbered by a background refresh.
 */

import { el, mount, apiGet, readTool } from "./util.js";
import { renderPanel, VIZ } from "./panels.js";
import { SERIES_COLORS, normalizeColor } from "./charts.js";
import * as api from "./views-api.js";
import * as derive from "./derive.js";

/** The FORMATTERS keys the format pickers offer (mirrors util.js FORMATTERS). */
const FORMAT_OPTIONS = ["text", "int", "num1", "num2", "pct", "ms", "mb", "time", "reltime", "bool"];
const PREVIEW_DEBOUNCE_MS = 350;

/* ─────────────────────────── entry ─────────────────────────── */

export async function renderEditor(main, id) {
  mount(main, el("div", { class: "strip loading", text: "Loading composer…" }));

  const session = await api.getSession();
  if (!session.can_edit) {
    mount(main, [
      backHead(id),
      el("div", { class: "strip empty" }, [
        "Editing custom views is only available on the machine running Darling (a loopback connection). " +
          "This browser is connected over the network, where views are read-only — you can still open and export them.",
      ]),
    ]);
    return;
  }

  const [catalog, fleet] = await Promise.all([api.getCatalog(), loadFleetOptions()]);

  let model;
  let editingId = null;
  let loadedVersion = null;

  if (id != null && id !== "new") {
    const res = await api.getView(id);
    if (res.kind !== "data" || !res.data) {
      mount(main, [backHead(id), el("div", { class: "strip error", text: res.message || "Could not load this view." })]);
      return;
    }
    editingId = res.data.id;
    loadedVersion = res.data.version;
    model = viewToModel(res.data);
  } else {
    model = { name: "", description: "", panels: [newPanel()] };
  }

  buildEditor(main, { model, editingId, loadedVersion, catalog, fleet });
}

/* Fleet server options for the `server` param dropdown (value = stored server_name, label = display name). */
async function loadFleetOptions() {
  const res = await apiGet("/api/fleet");
  if (res.kind !== "data" || !res.data) return [];
  return [...(res.data.cards || [])]
    .map((c) => ({ value: c.server_name || c.display_name, label: c.display_name }))
    .filter((o) => o.value)
    .sort((a, b) => a.label.localeCompare(b.label));
}

/* ─────────────────────────── model <-> stored definition ─────────────────────────── */

function newPanel() {
  return { read: "", params: {}, viz: "", span: 1, title: "", vizcfg: {} };
}

function viewToModel(view) {
  const def = view.definition || {};
  const panels = Array.isArray(def.panels) ? def.panels.map(descToPanel) : [];
  return {
    name: view.name || "",
    description: view.description || "",
    panels: panels.length ? panels : [newPanel()],
  };
}

/* A stored descriptor -> the editor's panel model. Everything beyond the fields the editor manages directly
   (title/read/params/viz/span) is preserved verbatim as `vizcfg` (rowsKey/columns/series/stats/unit/subtitle...)
   and spread back on save, so nothing is lost on an edit round-trip. */
function descToPanel(d) {
  const src = d && typeof d === "object" ? d : {};
  const { title, read, params, viz, span, ...vizcfg } = src;
  return {
    read: read || "",
    params: params && typeof params === "object" && !Array.isArray(params) ? { ...params } : {},
    viz: viz || "",
    span: span === 2 ? 2 : 1,
    title: title || "",
    vizcfg: vizcfg || {},
  };
}

function panelToDesc(p) {
  return {
    title: p.title || "",
    read: p.read,
    viz: p.viz,
    span: p.span === 2 ? 2 : 1,
    ...(p.vizcfg || {}),
    params: cleanParams(p.params),
  };
}

function modelToDefinition(model) {
  return { panels: model.panels.map(panelToDesc) };
}

/** Drop null / undefined / empty-string param values so an optional-left-blank param uses the tool's default. */
function cleanParams(params) {
  const out = {};
  for (const [k, v] of Object.entries(params || {})) {
    if (v === null || v === undefined || v === "") continue;
    out[k] = v;
  }
  return out;
}

/* ─────────────────────────── the editor shell ─────────────────────────── */

function buildEditor(main, ctx) {
  const { model } = ctx;

  const nameInput = el("input", {
    class: "editor-input",
    type: "text",
    placeholder: "View name (required)",
    "aria-label": "View name",
  });
  nameInput.value = model.name;
  nameInput.addEventListener("input", () => {
    model.name = nameInput.value;
    refreshSaveState();
  });

  const descInput = el("input", {
    class: "editor-input",
    type: "text",
    placeholder: "Description (optional)",
    "aria-label": "View description",
  });
  descInput.value = model.description;
  descInput.addEventListener("input", () => {
    model.description = descInput.value;
  });

  const panelsBox = el("div", { class: "panels-box" });
  const saveStatus = el("div", { class: "save-status" });
  const saveBtn = el("button", {
    class: "btn primary",
    type: "button",
    text: ctx.editingId != null ? "Save changes" : "Create view",
  });
  const cancelBtn = el("button", { class: "btn", type: "button", text: "Cancel" });

  const backHash = ctx.editingId != null ? "#/view/" + encodeURIComponent(ctx.editingId) : "#/views";
  cancelBtn.addEventListener("click", () => {
    location.hash = backHash;
  });
  saveBtn.addEventListener("click", onSave);

  const addBtn = el("button", { class: "btn", type: "button", text: "+ Add panel" });
  addBtn.addEventListener("click", () => {
    model.panels.push(newPanel());
    rebuildPanels();
  });

  const inner = { ...ctx, rebuildPanels, refreshSaveState };

  function rebuildPanels() {
    mount(panelsBox, model.panels.map((p, i) => buildPanelEditor(p, i, inner)));
    refreshSaveState();
  }

  function refreshSaveState() {
    const problem = saveBlocker(model, ctx.catalog);
    saveBtn.disabled = problem != null;
    mount(saveStatus, problem ? el("span", { class: "muted", text: problem }) : null);
  }

  async function onSave() {
    saveBtn.disabled = true;

    /* Auto-derive-on-save (B1): seed any field-less table/line/stat panel from a fresh live sample so the stored
       definition never carries an undefined columns/series/stats array (which would crash the shared renderer for
       EVERY seat). A panel with no live sample stays un-derived and is caught by the field-config backstop below. */
    mount(saveStatus, el("span", { class: "muted", text: "Preparing…" }));
    await ensureFieldConfigs(model, ctx.catalog);

    const problem = saveBlocker(model, ctx.catalog) || fieldConfigProblem(model);
    if (problem) {
      saveBtn.disabled = false;
      rebuildPanels(); // surface any field-config the auto-derive DID populate
      mount(saveStatus, el("span", { class: "strip error", text: problem }));
      return;
    }

    mount(saveStatus, el("span", { class: "muted", text: "Saving…" }));

    const body = {
      name: model.name.trim(),
      description: model.description ? model.description : null,
      definition: modelToDefinition(model),
    };

    let res;
    if (ctx.editingId != null) {
      body.version = ctx.loadedVersion;
      res = await api.updateView(ctx.editingId, body);
    } else {
      res = await api.createView(body);
    }

    if (res.kind === "data" && res.data) {
      location.hash = "#/view/" + encodeURIComponent(res.data.id);
      return;
    }

    saveBtn.disabled = false;
    handleSaveError(res, saveStatus, ctx, main);
  }

  mount(main, [
    el("div", { class: "page-head" }, [
      el("a", { href: backHash, text: "← Back" }),
      el("h2", { text: ctx.editingId != null ? "Edit view" : "New view" }),
    ]),
    el("div", { class: "editor-meta" }, [field("Name", nameInput), field("Description", descInput)]),
    el("h3", { class: "section-title", text: "Panels" }),
    panelsBox,
    addBtn,
    el("div", { class: "editor-footer" }, [saveBtn, cancelBtn, saveStatus]),
  ]);

  rebuildPanels();
}

/* On a 409 while EDITING, the row changed under us (stale version) or the name now collides — either way, offer
   to reload the latest (discarding local edits) so the operator re-applies against fresh state. */
function handleSaveError(res, saveStatus, ctx, main) {
  const parts = [el("span", { text: res.message || "Could not save the view." })];
  if (res.status === 409 && ctx.editingId != null) {
    const reload = el("button", { class: "btn small", type: "button", text: "Reload latest" });
    reload.addEventListener("click", () => renderEditor(main, ctx.editingId));
    parts.push(reload);
  }
  mount(saveStatus, el("div", { class: "strip error" }, parts));
}

/** The reason SAVE is blocked (name/panels/read/viz/required-params), or null when the view is savable. This is
 *  the button-DISABLING gate; the field-config requirement is enforced separately in onSave (see
 *  fieldConfigProblem) AFTER an auto-derive pass, so the Save click is never blocked from running that derive. */
function saveBlocker(model, catalog) {
  if (!model.name || !model.name.trim()) return "Enter a view name.";
  if (!model.panels.length) return "Add at least one panel.";
  for (let i = 0; i < model.panels.length; i++) {
    const p = model.panels[i];
    const n = i + 1;
    if (!p.read) return "Panel " + n + ": choose a read.";
    if (!p.viz) return "Panel " + n + ": choose a visualization.";
    const missing = missingRequired(p, catalog);
    if (missing.length) return "Panel " + n + ": fill required " + missing.join(", ") + ".";
  }
  return null;
}

/** Which vizzes need a load-bearing field array: table -> columns, line -> series, stat -> stats (bandlist has none). */
function needsFieldConfig(p) {
  return p.viz === "table" || p.viz === "line" || p.viz === "stat";
}

/** The panel's load-bearing field array for its viz, or null for a viz that has none. */
function fieldArray(p) {
  const c = p.vizcfg || {};
  if (p.viz === "table") return c.columns;
  if (p.viz === "line") return c.series;
  if (p.viz === "stat") return c.stats;
  return null;
}

/** Whether a table/line/stat panel has at least one configured field. */
function hasFieldConfig(p) {
  const arr = fieldArray(p);
  return Array.isArray(arr) && arr.length > 0;
}

/* Auto-derive-on-save feeder (B1): fetch a live sample for each field-less table/line/stat panel and seed its
   vizcfg via derive.js, so the author rarely hits the fieldConfigProblem backstop. Skips a panel with no read / an
   unfilled required param / no live sample — those the backstop reports. */
async function ensureFieldConfigs(model, catalog) {
  for (const p of model.panels) {
    if (!needsFieldConfig(p) || hasFieldConfig(p)) continue;
    if (!p.read || missingRequired(p, catalog).length) continue;
    const res = await readTool(p.read, cleanParams(p.params));
    if (res.kind === "data" && res.data) {
      p.vizcfg = derive.deriveVizConfig(res.data, p.viz, SERIES_COLORS);
    }
  }
}

/** The reason SAVE's field-config backstop trips (a table/line/stat panel still without fields), or null. */
function fieldConfigProblem(model) {
  for (let i = 0; i < model.panels.length; i++) {
    const p = model.panels[i];
    if (needsFieldConfig(p) && !hasFieldConfig(p)) {
      return (
        "Panel " + (i + 1) + ": add at least one field (use Auto-detect fields) or adjust the read so a sample is available."
      );
    }
  }
  return null;
}

/* ─────────────────────────── one panel editor ─────────────────────────── */

function buildPanelEditor(p, index, ctx) {
  const { catalog, fleet, model } = ctx;
  let lastSample = null;
  let previewTimer = null;

  const paramsBox = el("div", { class: "params-box" });
  const vizcfgBox = el("div", { class: "vizcfg-box" });
  const previewBox = el("div", { class: "panel-preview" });
  const bodyBox = el("div", { class: "panel-editor-body" });
  const headSub = el("span", { class: "panel-editor-sub" });

  const readSelect = buildReadSelect(catalog, p.read);
  readSelect.addEventListener("change", async () => {
    p.read = readSelect.value;
    p.params = defaultParams(catalog, p.read);
    p.vizcfg = {};
    rebuildParams();
    rebuildBody(); // reveal Parameters (or the "choose a read" hint) as prerequisites are met (M5)
    refreshHead();
    ctx.refreshSaveState();
    await refreshSampleAndDerive({ resetViz: true });
    rebuildBody(); // a viz may now be auto-suggested -> reveal Fields + Preview
    schedulePreview();
  });

  const vizSelect = buildVizSelect(catalog, p.viz);
  vizSelect.addEventListener("change", () => {
    p.viz = vizSelect.value;
    if (lastSample) p.vizcfg = derive.deriveVizConfig(lastSample, p.viz, SERIES_COLORS);
    rebuildVizcfg();
    rebuildBody(); // reveal Fields + Preview once a viz is chosen (M5)
    ctx.refreshSaveState();
    schedulePreview();
  });

  const spanSelect = el("select", { class: "editor-select", "aria-label": "Panel width" }, [
    el("option", { value: "1", text: "1 column" }),
    el("option", { value: "2", text: "2 columns (wide)" }),
  ]);
  spanSelect.value = String(p.span || 1);
  spanSelect.addEventListener("change", () => {
    p.span = spanSelect.value === "2" ? 2 : 1;
    schedulePreview();
  });

  const titleInput = el("input", { class: "editor-input", type: "text", placeholder: "Panel title", "aria-label": "Panel title" });
  titleInput.value = p.title;
  titleInput.addEventListener("input", () => {
    p.title = titleInput.value;
    refreshHead();
    schedulePreview();
  });

  /* "Auto-detect fields" first, then "Re-detect from sample" once a config exists — its real job is re-inspect-
     and-rebuild (it DISCARDS manual field edits), so the label distinguishes the first detect from a re-detect. */
  const autoBtn = el("button", { class: "btn small", type: "button" });
  function refreshAutoBtn() {
    autoBtn.textContent = hasVizcfg(p) ? "Re-detect from sample" : "Auto-detect fields";
  }
  autoBtn.addEventListener("click", async () => {
    await refreshSampleAndDerive({ resetViz: false, force: true });
    rebuildBody();
    schedulePreview();
  });
  const fieldsHelp = el("div", {
    class: "block-help",
    text: "Detected from a live sample; edit below or re-detect.",
  });

  const upBtn = el("button", { class: "btn small icon", type: "button", text: "↑", title: "Move up", "aria-label": "Move panel up" });
  const downBtn = el("button", { class: "btn small icon", type: "button", text: "↓", title: "Move down", "aria-label": "Move panel down" });
  const removeBtn = el("button", { class: "btn small danger", type: "button", text: "Remove", "aria-label": "Remove panel" });
  upBtn.disabled = index === 0;
  downBtn.disabled = index === model.panels.length - 1;
  upBtn.addEventListener("click", () => {
    swap(model.panels, index, index - 1);
    ctx.rebuildPanels();
  });
  downBtn.addEventListener("click", () => {
    swap(model.panels, index, index + 1);
    ctx.rebuildPanels();
  });
  removeBtn.addEventListener("click", () => {
    model.panels.splice(index, 1);
    if (!model.panels.length) model.panels.push(newPanel());
    ctx.rebuildPanels();
  });

  /* Stable section nodes composed by rebuildBody() — created once so their inputs keep focus/state across a
     progressive-disclosure rebuild. */
  const identityGrid = el("div", { class: "editor-grid" }, [
    field("Read", readSelect),
    field("Visualization", vizSelect),
    field("Width", spanSelect),
    field("Title", titleInput, "field-title"),
  ]);
  const paramsSection = labeledBlock("Parameters", paramsBox);
  const fieldsSection = labeledBlock(
    "Fields",
    el("div", { class: "vizcfg-wrap" }, [fieldsHelp, el("div", { class: "vizcfg-actions" }, [autoBtn]), vizcfgBox])
  );
  const previewSection = labeledBlock("Preview", previewBox);
  previewSection.classList.add("panel-preview-col");

  function rebuildParams() {
    mount(
      paramsBox,
      buildParamInputs(p, catalog, fleet, () => {
        refreshHead(); // a `server` param feeds the panel-header echo
        ctx.refreshSaveState();
        schedulePreview();
        maybeDeriveOnParamChange();
      })
    );
  }

  function rebuildVizcfg() {
    mount(vizcfgBox, buildVizcfgEditor(p, lastSample, schedulePreview));
    refreshAutoBtn();
  }

  /* The panel-header echo: "Panel N · <title or read> (<server param>)" so a long list of panels is scannable. */
  function refreshHead() {
    const label = p.title || p.read || "";
    const server = serverParamValue(p, catalog);
    let text = "";
    if (label) text += " · " + label;
    if (server) text += " (" + server + ")";
    headSub.textContent = text;
  }

  /* Progressive disclosure (M5) + two-column layout (M2): the identity row is always shown; Parameters appears
     once a read is chosen and Fields + the live Preview once a viz is chosen. With a preview present the body is a
     two-column grid (config left, sticky preview right) that collapses to stacked under ~900px. */
  function rebuildBody() {
    const config = [identityGrid];
    if (!p.read) {
      config.push(el("div", { class: "panel-hint", text: "Choose a read to begin." }));
    } else {
      config.push(paramsSection);
      if (p.viz) {
        config.push(fieldsSection);
      } else {
        config.push(el("div", { class: "panel-hint", text: "Choose a visualization to configure its fields and preview." }));
      }
    }
    const hasPreview = !!(p.read && p.viz);
    const kids = [el("div", { class: "panel-config" }, config)];
    if (hasPreview) kids.push(previewSection);
    bodyBox.className = "panel-editor-body" + (hasPreview ? " has-preview" : "");
    mount(bodyBox, kids);
  }

  /* If the operator finishes a previously-missing required param and there is no vizcfg yet, derive one. */
  async function maybeDeriveOnParamChange() {
    if (!p.read || hasVizcfg(p)) return;
    if (missingRequired(p, catalog).length) return;
    await refreshSampleAndDerive({ resetViz: !p.viz });
    rebuildBody();
  }

  /* Fetch one live sample with the current params and (re)seed the vizcfg from it. Skips when a required param
     is unfilled (nothing to fetch yet). `force` re-derives even when a config already exists (the button). */
  async function refreshSampleAndDerive({ resetViz, force }) {
    if (!p.read) return;
    if (missingRequired(p, catalog).length) {
      lastSample = null;
      return;
    }
    const res = await readTool(p.read, cleanParams(p.params));
    if (res.kind !== "data" || !res.data) {
      lastSample = null;
      if (force) mount(vizcfgBox, el("div", { class: "muted", text: sampleUnavailableText(res) }));
      return;
    }
    lastSample = res.data;
    if (resetViz || !p.viz) {
      p.viz = derive.suggestViz(lastSample);
      vizSelect.value = p.viz;
    }
    if (force || !hasVizcfg(p)) {
      p.vizcfg = derive.deriveVizConfig(lastSample, p.viz, SERIES_COLORS);
    }
    rebuildVizcfg();
    ctx.refreshSaveState();
  }

  /* Prime a sample for an EXISTING panel (populates the field dropdowns) WITHOUT clobbering its saved config. */
  async function primeSample() {
    if (!p.read || missingRequired(p, catalog).length) return;
    const res = await readTool(p.read, cleanParams(p.params));
    if (res.kind === "data" && res.data) {
      lastSample = res.data;
      rebuildVizcfg();
    }
  }

  function schedulePreview() {
    clearTimeout(previewTimer);
    previewTimer = setTimeout(renderPreview, PREVIEW_DEBOUNCE_MS);
  }

  function renderPreview() {
    const problem = previewBlocker(p, catalog);
    if (problem) {
      mount(previewBox, el("div", { class: "strip empty", text: problem }));
      return;
    }
    mount(previewBox, renderPanel(panelToDesc(p)));
  }

  rebuildParams();
  rebuildVizcfg();
  refreshHead();
  rebuildBody();
  primeSample();
  schedulePreview();

  return el("div", { class: "panel-editor card" }, [
    el("div", { class: "panel-editor-head" }, [
      el("span", { class: "panel-editor-title", text: "Panel " + (index + 1) }),
      headSub,
      el("div", { class: "spacer" }),
      upBtn,
      downBtn,
      removeBtn,
    ]),
    bodyBox,
  ]);
}

/** The reason a panel can't PREVIEW (missing read/viz/required param), or null when it can render. */
function previewBlocker(p, catalog) {
  if (!p.read) return "Choose a read to preview this panel.";
  if (!p.viz) return "Choose a visualization to preview this panel.";
  const missing = missingRequired(p, catalog);
  if (missing.length) {
    return "Fill required parameter" + (missing.length > 1 ? "s" : "") + ": " + missing.join(", ") + ".";
  }
  return null;
}

function sampleUnavailableText(res) {
  if (res.kind === "empty") return "No sample rows in this window yet — add fields manually or adjust the parameters.";
  if (res.kind === "error") return "Could not fetch a sample (" + res.message + ") — add fields manually.";
  return "No sample data — add fields manually.";
}

/* ─────────────────────────── read / viz pickers ─────────────────────────── */

function buildReadSelect(catalog, current) {
  const sel = el("select", { class: "editor-select", "aria-label": "Read" });
  sel.appendChild(el("option", { value: "", text: "— choose a read —" }));

  const byCategory = new Map();
  for (const r of catalog.reads || []) {
    if (!byCategory.has(r.category)) byCategory.set(r.category, []);
    byCategory.get(r.category).push(r);
  }
  for (const [category, reads] of [...byCategory.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
    const group = el("optgroup", { label: category });
    for (const r of reads.sort((a, b) => a.name.localeCompare(b.name))) {
      group.appendChild(el("option", { value: r.name, text: r.name, title: r.description || r.name }));
    }
    sel.appendChild(group);
  }

  sel.value = current || "";
  return sel;
}

function buildVizSelect(catalog, current) {
  const sel = el("select", { class: "editor-select", "aria-label": "Visualization" });
  sel.appendChild(el("option", { value: "", text: "— choose —" }));
  const vizList = catalog.viz && catalog.viz.length ? catalog.viz : Object.keys(VIZ);
  for (const v of vizList) sel.appendChild(el("option", { value: v, text: v }));
  sel.value = current || "";
  return sel;
}

/* ─────────────────────────── typed param inputs ─────────────────────────── */

function defaultParams(catalog, read) {
  const rd = readByName(catalog, read);
  const out = {};
  if (!rd) return out;
  for (const prm of rd.params || []) {
    if (prm.default !== null && prm.default !== undefined) out[prm.name] = prm.default;
  }
  return out;
}

function buildParamInputs(p, catalog, fleet, onChange) {
  const rd = readByName(catalog, p.read);
  if (!rd || !(rd.params || []).length) {
    return el("div", { class: "muted", text: p.read ? "This read takes no parameters." : "Choose a read first." });
  }
  return (rd.params || []).map((prm) => {
    const input = buildParamInput(prm, p.params[prm.name], fleet, (val) => {
      if (val === "" || val == null) delete p.params[prm.name];
      else p.params[prm.name] = val;
      onChange();
    });
    return el("label", { class: "param-field" + (prm.required ? " required" : "") }, [
      el("span", { class: "param-name" }, [prm.name, prm.required ? el("span", { class: "req", text: " *" }) : null]),
      input,
      hintFor(prm),
    ]);
  });
}

function buildParamInput(prm, currentValue, fleet, setValue) {
  switch (prm.type) {
    case "server": {
      const sel = el("select", { class: "editor-select", "aria-label": prm.name });
      sel.appendChild(el("option", { value: "", text: "(auto / all servers)" }));
      for (const o of fleet) sel.appendChild(el("option", { value: o.value, text: o.label }));
      sel.value = currentValue != null ? String(currentValue) : "";
      sel.addEventListener("change", () => setValue(sel.value));
      return sel;
    }
    case "bool": {
      const box = el("input", { type: "checkbox", class: "editor-check", "aria-label": prm.name });
      box.checked = currentValue != null ? currentValue === true || currentValue === "true" : !!prm.default;
      box.addEventListener("change", () => setValue(box.checked));
      return box;
    }
    case "int": {
      const inp = el("input", {
        class: "editor-input",
        type: "number",
        step: "1",
        "aria-label": prm.name,
        placeholder: prm.default != null ? String(prm.default) : "",
      });
      if (currentValue != null) inp.value = String(currentValue);
      inp.addEventListener("input", () => setValue(toNumber(inp.value, true)));
      return inp;
    }
    case "double": {
      const inp = el("input", {
        class: "editor-input",
        type: "number",
        step: "any",
        "aria-label": prm.name,
        placeholder: prm.default != null ? String(prm.default) : "",
      });
      if (currentValue != null) inp.value = String(currentValue);
      inp.addEventListener("input", () => setValue(toNumber(inp.value, false)));
      return inp;
    }
    default: {
      const inp = el("input", { class: "editor-input", type: "text", "aria-label": prm.name });
      if (currentValue != null) inp.value = String(currentValue);
      inp.addEventListener("input", () => setValue(inp.value));
      return inp;
    }
  }
}

function hintFor(prm) {
  let t = prm.type;
  if (prm.default !== null && prm.default !== undefined) t += " · default " + prm.default;
  return el("span", { class: "param-hint", text: t });
}

/* ─────────────────────────── vizcfg sub-editors ─────────────────────────── */

function buildVizcfgEditor(p, sample, onChange) {
  p.vizcfg = p.vizcfg || {};
  switch (p.viz) {
    case "table":
      return tableCfgEditor(p.vizcfg, sample, onChange);
    case "stat":
      return statCfgEditor(p.vizcfg, sample, onChange);
    case "line":
      return lineCfgEditor(p.vizcfg, sample, onChange);
    case "bandlist":
      return bandlistCfgEditor(p.vizcfg, sample, onChange);
    default:
      return el("div", { class: "muted", text: "Choose a visualization to configure its fields." });
  }
}

function tableCfgEditor(cfg, sample, onChange) {
  cfg.columns = cfg.columns || [];
  const arrayKeys = topLevelArrayKeys(sample);
  const rowsField = keyField(arrayKeys, cfg.rowsKey, (v) => {
    cfg.rowsKey = v;
    onChange();
  }, "rows key");
  const columns = itemList(
    "columns",
    cfg.columns,
    (col) => [
      keyField(rowKeys(sample, cfg.rowsKey), col.key, (v) => {
        col.key = v;
        onChange();
      }, "column key"),
      textField(col.label, (v) => {
        col.label = v;
        onChange();
      }, "column label"),
      formatSelect(col.format, (v) => {
        col.format = v;
        onChange();
      }),
    ],
    onChange,
    "+ Add column",
    () => ({ key: "", label: "", format: "text" })
  );
  return el("div", { class: "cfg" }, [field("Rows key", rowsField), labeledBlock("Columns", columns)]);
}

function statCfgEditor(cfg, sample, onChange) {
  cfg.stats = cfg.stats || [];
  const keys = topLevelScalarKeys(sample);
  const list = itemList(
    "stats",
    cfg.stats,
    (s) => [
      keyField(keys, s.key, (v) => {
        s.key = v;
        onChange();
      }, "stat key"),
      textField(s.label, (v) => {
        s.label = v;
        onChange();
      }, "stat label"),
      formatSelect(s.format, (v) => {
        s.format = v;
        onChange();
      }),
    ],
    onChange,
    "+ Add stat",
    () => ({ key: "", label: "", format: "text" })
  );
  return el("div", { class: "cfg" }, [labeledBlock("Stats", list)]);
}

function lineCfgEditor(cfg, sample, onChange) {
  cfg.series = cfg.series || [];
  const arrayKeys = topLevelArrayKeys(sample);
  const numericKeys = rowNumericKeys(sample, cfg.rowsKey);
  const rowsField = keyField(arrayKeys, cfg.rowsKey, (v) => {
    cfg.rowsKey = v;
    onChange();
  }, "rows key");
  const xField = keyField(rowKeys(sample, cfg.rowsKey), cfg.xKey, (v) => {
    cfg.xKey = v;
    onChange();
  }, "x-axis key");
  const series = itemList(
    "series",
    cfg.series,
    (s) => [
      keyField(numericKeys, s.key, (v) => {
        s.key = v;
        onChange();
      }, "series key"),
      textField(s.label, (v) => {
        s.label = v;
        onChange();
      }, "series label"),
      colorField(s.color, (v) => {
        s.color = v;
        onChange();
      }),
    ],
    onChange,
    "+ Add series",
    () => ({ key: "", label: "", color: SERIES_COLORS[0] })
  );
  const fmt = formatSelect(cfg.format || "text", (v) => {
    cfg.format = v === "text" ? undefined : v;
    onChange();
  });
  const unit = textField(cfg.unit, (v) => {
    cfg.unit = v || undefined;
    onChange();
  }, "unit");
  return el("div", { class: "cfg" }, [
    field("Rows key", rowsField),
    field("X-axis key", xField),
    labeledBlock("Series", series),
    field("Value format", fmt),
    field("Unit (optional)", unit),
  ]);
}

function bandlistCfgEditor(cfg, sample, onChange) {
  const arrayKeys = topLevelArrayKeys(sample);
  const rKeys = rowKeys(sample, cfg.rowsKey);
  const keyRow = (label, prop, aria) =>
    field(
      label,
      keyField(rKeys, cfg[prop], (v) => {
        cfg[prop] = v || undefined;
        onChange();
      }, aria)
    );
  return el("div", { class: "cfg" }, [
    field(
      "Rows key",
      keyField(arrayKeys, cfg.rowsKey, (v) => {
        cfg.rowsKey = v;
        onChange();
      }, "rows key")
    ),
    keyRow("Primary key", "primaryKey", "primary key"),
    keyRow("Band key", "bandKey", "band key"),
    keyRow("Band label key (optional)", "bandLabelKey", "band label key"),
    keyRow("Reason key (optional)", "reasonKey", "reason key"),
    keyRow("Nav key — server (optional)", "navKey", "nav key"),
  ]);
}

/* ─────────────────────────── small field builders ─────────────────────────── */

let _keyListId = 0;

/* A key input backed by a <datalist> of the sample's available keys — autocompletes from the sample while still
   allowing a hand-typed key (a sample is a subset; the operator may know a key not in the first row). */
function keyField(keys, current, onChange, ariaLabel) {
  const input = el("input", { class: "editor-input mono", type: "text", "aria-label": ariaLabel || "key" });
  if (current != null) input.value = String(current);
  input.addEventListener("input", () => onChange(input.value));
  if (keys && keys.length) {
    const id = "keys-" + ++_keyListId;
    const datalist = el("datalist", { id });
    for (const k of keys) datalist.appendChild(el("option", { value: k }));
    input.setAttribute("list", id);
    return el("span", { class: "keyfield" }, [input, datalist]);
  }
  return input;
}

function textField(current, onChange, ariaLabel) {
  const input = el("input", { class: "editor-input", type: "text", "aria-label": ariaLabel || "text" });
  if (current != null) input.value = String(current);
  input.addEventListener("input", () => onChange(input.value));
  return input;
}

function formatSelect(current, onChange) {
  const sel = el("select", { class: "editor-select", "aria-label": "format" });
  for (const f of FORMAT_OPTIONS) sel.appendChild(el("option", { value: f, text: f }));
  sel.value = current || "text";
  sel.addEventListener("change", () => onChange(sel.value));
  return sel;
}

/* Series color is CONSTRAINED (reconciliation #4): an <input type=color> only ever yields #rrggbb, and the
   palette buttons are charts.js constants — no free-text ever reaches the chart's style-attribute sink. */
function colorField(current, onChange) {
  const picker = el("input", { type: "color", class: "color-picker", "aria-label": "series color" });
  picker.value = normalizeColor(current);
  picker.addEventListener("input", () => onChange(picker.value));
  const palette = el(
    "span",
    { class: "palette" },
    SERIES_COLORS.map((c) =>
      el("button", {
        type: "button",
        class: "swatch-btn",
        title: c,
        style: "background:" + c,
        "aria-label": "use color " + c,
        onClick: () => {
          picker.value = c;
          onChange(c);
        },
      })
    )
  );
  return el("span", { class: "color-field" }, [picker, palette]);
}

/* A remove-able, add-able list of config rows (columns / series / stats). renderRow returns the row's field
   nodes; itemList appends the remove button and the add button, redrawing in place on any structural change. */
function itemList(className, items, renderRow, onChange, addLabel, makeNew) {
  const box = el("div", { class: "item-list " + className });

  function redraw() {
    const rows = items.map((item, i) => {
      const removeBtn = el("button", { class: "btn small danger icon", type: "button", text: "×", title: "Remove", "aria-label": "remove" });
      removeBtn.addEventListener("click", () => {
        items.splice(i, 1);
        redraw();
        onChange();
      });
      return el("div", { class: "item-row" }, [...renderRow(item, i), removeBtn]);
    });
    const addBtn = el("button", { class: "btn small", type: "button", text: addLabel });
    addBtn.addEventListener("click", () => {
      items.push(makeNew());
      redraw();
      onChange();
    });
    mount(box, [...rows, addBtn]);
  }

  redraw();
  return box;
}

function field(label, control, extraClass) {
  return el("label", { class: "editor-field" + (extraClass ? " " + extraClass : "") }, [
    el("span", { class: "field-label", text: label }),
    control,
  ]);
}

function labeledBlock(label, node) {
  return el("div", { class: "editor-block" }, [el("div", { class: "block-label", text: label }), node]);
}

function backHead(id) {
  return el("div", { class: "page-head" }, [
    el("a", { href: id != null && id !== "new" ? "#/view/" + encodeURIComponent(id) : "#/views", text: "← Back" }),
    el("h2", { text: "Composer" }),
  ]);
}

/* ─────────────────────────── sample-key helpers ─────────────────────────── */

function topLevelArrayKeys(sample) {
  if (!sample || typeof sample !== "object" || Array.isArray(sample)) return [];
  return Object.keys(sample).filter((k) => Array.isArray(sample[k]));
}

function topLevelScalarKeys(sample) {
  if (!sample || typeof sample !== "object" || Array.isArray(sample)) return [];
  return Object.keys(sample).filter((k) => sample[k] === null || typeof sample[k] !== "object");
}

function rowKeys(sample, rowsKey) {
  const first = firstRow(sample, rowsKey);
  return first ? Object.keys(first) : [];
}

function rowNumericKeys(sample, rowsKey) {
  const first = firstRow(sample, rowsKey);
  return first ? Object.keys(first).filter((k) => typeof first[k] === "number") : [];
}

function firstRow(sample, rowsKey) {
  const rows = sample && rowsKey ? sample[rowsKey] : null;
  return Array.isArray(rows) && rows.length && rows[0] && typeof rows[0] === "object" ? rows[0] : null;
}

/* ─────────────────────────── misc helpers ─────────────────────────── */

function readByName(catalog, name) {
  return (catalog.reads || []).find((r) => r.name === name) || null;
}

/* The value of a read's `server`-typed param (if any) — feeds the panel-header echo "Panel N · Title (SERVER)". */
function serverParamValue(p, catalog) {
  const rd = readByName(catalog, p.read);
  if (!rd) return "";
  const sp = (rd.params || []).find((prm) => prm.type === "server");
  if (!sp) return "";
  const v = p.params ? p.params[sp.name] : null;
  return v == null || v === "" ? "" : String(v);
}

function missingRequired(panel, catalog) {
  const rd = readByName(catalog, panel.read);
  if (!rd) return [];
  return (rd.params || [])
    .filter((prm) => prm.required && isEmpty(panel.params[prm.name]))
    .map((prm) => prm.name);
}

function hasVizcfg(p) {
  const c = p.vizcfg || {};
  return !!(
    (c.columns && c.columns.length) ||
    (c.series && c.series.length) ||
    (c.stats && c.stats.length) ||
    c.rowsKey ||
    c.primaryKey
  );
}

function isEmpty(v) {
  return v === null || v === undefined || v === "";
}

function toNumber(raw, isInt) {
  if (raw === "" || raw == null) return "";
  const n = isInt ? parseInt(raw, 10) : parseFloat(raw);
  return Number.isNaN(n) ? "" : n;
}

function swap(arr, i, j) {
  const tmp = arr[i];
  arr[i] = arr[j];
  arr[j] = tmp;
}
