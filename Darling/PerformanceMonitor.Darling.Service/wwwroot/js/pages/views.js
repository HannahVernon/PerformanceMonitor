/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Custom Views (#1563): the list page and the renderer. A view is stored JSON {panels:[<renderPanel descriptor>]}
 * authored in the composer (editor.js); this page RENDERS it by mapping each stored panel through the UNMODIFIED
 * renderPanel into a `.panel-grid` — so a stored dashboard uses the exact same viz registry the built-in pages do
 * (per-panel isolation is free: renderPanel error-strips its own fetch/throw/unknown-viz). Before calling
 * renderPanel, each panel's read is checked against the cached catalog and its viz against the VIZ registry, so a
 * stale/unknown read renders a clean "unknown read" strip instead of an opaque 404 inside the panel.
 *
 * Edit affordances (New / Edit / Delete / Import) are shown ONLY when the session reports can_edit (editing is
 * loopback-only, enforced server-side); RENDER and EXPORT are available to every seat. All user text (names,
 * descriptions) reaches the DOM through el()/textContent (R4 — never innerHTML).
 */

import { el, mount, loadingStrip, errorStrip, emptyStrip, relTime } from "../util.js";
import { renderPanel, VIZ } from "../panels.js";
import * as api from "../views-api.js";

/* ─────────────────────────── list page ─────────────────────────── */

export async function renderViewList(main) {
  mount(main, [listHead(false), loadingStrip("Loading views…")]);

  const [session, res] = await Promise.all([api.getSession(), api.listViews()]);
  const canEdit = !!session.can_edit;

  if (res.kind === "error") {
    mount(main, [listHead(canEdit), errorStrip(res.message)]);
    return;
  }

  const views = Array.isArray(res.data) ? res.data : [];
  const nodes = [listHead(canEdit)];

  if (canEdit) {
    nodes.push(importPanel());
  }

  if (!views.length) {
    nodes.push(
      emptyStrip(
        canEdit
          ? "No custom views yet. Click “New view” to compose one from the read catalog, or paste an exported view above."
          : "No custom views yet. Views are composed on the machine running Darling (loopback)."
      )
    );
    mount(main, nodes);
    return;
  }

  nodes.push(el("div", { class: "view-cards" }, views.map((v) => viewCard(v))));
  mount(main, nodes);
}

function listHead(canEdit) {
  return el("div", { class: "page-head" }, [
    el("h2", { text: "Custom Views" }),
    el("div", { class: "spacer" }),
    canEdit ? el("a", { class: "btn primary", href: "#/view/new", text: "New view" }) : null,
  ]);
}

function viewCard(v) {
  const meta =
    "v" + v.version + " · updated " + relTime(v.updated_at) + (v.updated_by ? " by " + v.updated_by : "");
  return el("a", { class: "view-card card", href: "#/view/" + encodeURIComponent(v.id) }, [
    el("div", { class: "vc-name", text: v.name }),
    v.description ? el("div", { class: "vc-desc", text: v.description }) : null,
    el("div", { class: "vc-meta", text: meta }),
  ]);
}

/* Paste-to-import (can_edit only): parse -> client-validate against the catalog -> POST (backend re-validates).
   Accepts an exported view {name, description?, definition} or a bare definition {panels:[...]} plus a name. */
function importPanel() {
  const textarea = el("textarea", {
    class: "import-box",
    rows: "4",
    placeholder: 'Paste an exported view JSON here — {"name": "...", "definition": {"panels": [...]}}',
    "aria-label": "Paste exported view JSON",
  });
  const status = el("div", { class: "import-status" });
  const importBtn = el("button", { class: "btn", type: "button", text: "Import" });

  importBtn.addEventListener("click", async () => {
    mount(status, null);
    const raw = textarea.value.trim();
    if (!raw) {
      mount(status, errorStrip("Paste a view's JSON first."));
      return;
    }

    let parsed;
    try {
      parsed = JSON.parse(raw);
    } catch (e) {
      mount(status, errorStrip("That is not valid JSON: " + (e && e.message ? e.message : String(e))));
      return;
    }

    const def = parsed && parsed.definition ? parsed.definition : parsed && parsed.panels ? parsed : null;
    if (!def) {
      mount(status, errorStrip("The JSON must contain a 'definition' (or be a definition with 'panels')."));
      return;
    }
    const name = (parsed.name || "").trim();
    if (!name) {
      mount(status, errorStrip('The imported JSON has no "name". Add a top-level "name" field and try again.'));
      return;
    }

    const catalog = await api.getCatalog();
    const invalid = api.validateDefinition(def, catalog);
    if (invalid) {
      mount(status, errorStrip(invalid));
      return;
    }

    importBtn.disabled = true;
    mount(status, el("div", { class: "strip loading", text: "Importing…" }));
    const result = await api.createView({ name, description: parsed.description || null, definition: def });
    importBtn.disabled = false;
    if (result.kind === "data" && result.data) {
      location.hash = "#/view/" + encodeURIComponent(result.data.id);
      return;
    }
    mount(status, errorStrip(result.message || "Could not import the view."));
  });

  return el("details", { class: "import-panel" }, [
    el("summary", { text: "Import a view" }),
    el("div", { class: "import-body" }, [textarea, el("div", { class: "import-actions" }, [importBtn]), status]),
  ]);
}

/* ─────────────────────────── renderer ─────────────────────────── */

export async function renderView(main, id) {
  mount(main, loadingStrip("Loading view…"));

  const [session, catalog, res] = await Promise.all([api.getSession(), api.getCatalog(), api.getView(id)]);
  if (res.kind === "error") {
    mount(main, [el("div", { class: "page-head" }, [backToViews(), el("h2", { text: "View" })]), errorStrip(res.message)]);
    return;
  }

  const view = res.data || {};
  const def = view.definition || {};
  const panels = Array.isArray(def.panels) ? def.panels : [];
  const canEdit = !!session.can_edit;
  const readSet = new Set((catalog.reads || []).map((r) => r.name));

  const status = el("div", { class: "view-status" });
  const head = el("div", { class: "page-head" }, [
    backToViews(),
    el("h2", { text: view.name || "View" }),
    view.description ? el("div", { class: "meta", text: view.description }) : null,
    el("div", { class: "spacer" }),
    exportButton(view),
    canEdit ? el("a", { class: "btn small", href: "#/view/" + encodeURIComponent(id) + "/edit", text: "Edit" }) : null,
    canEdit ? deleteButton(id, status) : null,
  ]);

  if (!panels.length) {
    mount(main, [head, status, emptyStrip("This view has no panels yet.")]);
    return;
  }

  const grid = el("div", { class: "panel-grid" }, panels.map((p) => panelOrError(p, readSet)));
  mount(main, [head, status, grid]);
}

function backToViews() {
  return el("a", { href: "#/views", text: "← Views" });
}

/* Pre-check the panel against the catalog + viz registry so an unknown read/viz is a clean strip, not a 404. */
function panelOrError(p, readSet) {
  if (!p || typeof p !== "object") {
    return panelErrorCard("Invalid panel", "This panel is not an object.");
  }
  if (p.path != null) {
    return panelErrorCard(p.title, "This panel uses raw 'path' mode, which is not supported.");
  }
  if (!p.read || !readSet.has(p.read)) {
    return panelErrorCard(p.title, "Unknown read '" + (p.read || "") + "'. It may have been renamed or removed.");
  }
  if (!p.viz || !VIZ[p.viz]) {
    return panelErrorCard(p.title, "Unknown visualization '" + (p.viz || "") + "'.");
  }
  return renderPanel(p);
}

function panelErrorCard(title, message) {
  return el("div", { class: "panel card" }, [
    el("h3", {}, [title || "Panel"]),
    el("div", { class: "panel-body" }, [errorStrip(message)]),
  ]);
}

/* Export = a runtime Blob download of {name, description?, definition} (re-importable). Available to every seat;
   the Blob URL is created at runtime (never a source literal), so the air-gap scan stays green. */
function exportButton(view) {
  const btn = el("button", { class: "btn small", type: "button", text: "Export" });
  btn.addEventListener("click", () => {
    const payload = { name: view.name, description: view.description || null, definition: view.definition || {} };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = el("a", { href: url, download: safeFileName(view.name) + ".json" });
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  });
  return btn;
}

function safeFileName(name) {
  const cleaned = String(name || "view").replace(/[^A-Za-z0-9._-]+/g, "_").replace(/^_+|_+$/g, "");
  return cleaned || "view";
}

function deleteButton(id, status) {
  const btn = el("button", { class: "btn small danger", type: "button", text: "Delete" });
  btn.addEventListener("click", async () => {
    if (!window.confirm("Delete this view? This cannot be undone.")) {
      return;
    }
    btn.disabled = true;
    mount(status, el("div", { class: "strip loading", text: "Deleting…" }));
    const res = await api.deleteView(id);
    if (res.kind === "data") {
      location.hash = "#/views";
      return;
    }
    btn.disabled = false;
    mount(status, errorStrip(res.message || "Could not delete the view."));
  });
  return btn;
}
