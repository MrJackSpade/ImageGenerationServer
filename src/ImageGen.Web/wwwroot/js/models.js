// models.js — the models page: which file on this machine fills each catalogue slot.
//
// Models only. Which WORKFLOWS a missing file leaves unavailable is the workflow library's business, not this page's
// — they are two concepts and they get two pages; the library greys out what it cannot run and offers the fix in
// place.
//
// The catalogue ships no filenames, because a filename is a fact about a disk rather than about a model. What the
// app knows is a slot and how to recognise it; what THIS machine knows is which file fills it. Recognised slots
// arrive already bound; everything else is bound here.
//
// JSON only — /forge/catalog/status returns data and this builds the DOM from it.
(function () {
  "use strict";

  const $status = document.getElementById("modelsStatus");
  const $slots = document.getElementById("slotList");
  if (!$slots) return;

  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  let state = null;

  // Which renderer patches can install a missing pack, keyed by the catalogue slot each one satisfies.
  let PATCHES_BY_SLOT = new Map();
  async function loadPatches() {
    try {
      const r = await fetch("/api/comfy-patches", { headers: { Accept: "application/json" } });
      if (!r.ok) return;
      const body = await r.json();
      const map = new Map();
      for (const p of body.patches || []) for (const slot of p.provides || []) map.set(slot, p);
      PATCHES_BY_SLOT = map;
    } catch (e) {
      // Not being able to offer an install does not stop the page doing its actual job.
      console.error("models: could not read the patch list", e);
    }
  }

  async function load() {
    const [res] = await Promise.all([
      fetch("/forge/catalog/status", { headers: { Accept: "application/json" } }),
      loadPatches(),
    ]);
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      $status.textContent = body.error || "Could not read the catalogue status.";
      return;
    }
    state = await res.json();
    render();
  }

  function summary() {
    const bound = state.slots.filter((s) => s.boundFile).length;
    const auto = state.slots.filter((s) => s.isAuto).length;
    return `${bound} of ${state.slots.length} models found (${auto} recognised automatically)`;
  }

  function slotRow(s) {
    // A custom node is not a file you point at — it is a pack that is either installed or it isn't, so the
    // dropdown for one is an empty list with no way forward. Where a renderer patch installs that pack, offer
    // that instead. Nothing here can install a model FILE: patches carry code, never weights.
    const patch = PATCHES_BY_SLOT.get(s.id);
    if (patch) {
      const installed = patch.state === "Applied";
      return `<div class="mrow" data-slot="${esc(s.id)}">
        <div class="mrow-head">
          <span class="listrow-name">${esc(s.label)}</span>
          <span class="listrow-stat" title="${esc(patch.why)}">${installed ? "installed" : "—"}</span>
        </div>
        ${installed
          ? '<span class="muted">Installed by a renderer patch.</span>'
          : `<button type="button" class="settings-btn slot-install" data-patch="${esc(patch.id)}">Install ${esc(patch.title)}</button>`}
      </div>`;
    }

    // Candidates first and marked, then everything else of the same kind. A slot may be bound to any file of its
    // kind, not only to something a pattern recognised — the patterns pre-fill, they do not restrict. Each group is
    // A–Z, case-insensitive (matching #84's ordering).
    const byName = (a, b) => a.localeCompare(b, undefined, { sensitivity: "base" });
    const candidates = s.candidates.slice().sort(byName);
    const rest = s.available.filter((f) => !s.candidates.includes(f)).sort(byName);
    const opt = (f, tag) =>
      `<option value="${esc(f)}"${f === s.boundFile ? " selected" : ""}>${esc(f)}${tag || ""}</option>`;

    const badge = !s.boundFile
      ? '<span class="listrow-stat" title="Nothing bound">—</span>'
      : s.isAuto
        ? '<span class="listrow-stat" title="Recognised automatically; change it if it is wrong">auto</span>'
        : '<span class="listrow-stat" title="You chose this">set</span>';

    // The kind is the group heading now, so it does not repeat on every row.
    return `<div class="mrow" data-slot="${esc(s.id)}">
      <div class="mrow-head">
        <span class="listrow-name">${esc(s.label)}</span>
        ${badge}
      </div>
      <select class="slot-pick" data-slot="${esc(s.id)}" title="${esc(s.id)}">
        <option value="">— not set —</option>
        ${candidates.map((f) => opt(f, " (recognised)")).join("")}
        ${rest.map((f) => opt(f, "")).join("")}
      </select>
    </div>`;
  }

  // What each slot kind is called, and the order the groups read in: the things you generate with first, then
  // what they need, then the extras. A kind with no slots renders no heading.
  const KIND_LABELS = {
    Unet: "Diffusion models",
    UnetGguf: "Diffusion models (GGUF)",
    Checkpoint: "Checkpoints (all-in-one)",
    TextEncoder: "Text encoders",
    Vae: "VAEs",
    Lora: "LoRAs",
    ControlNet: "ControlNets",
    MotionModel: "Motion models",
    ClipVision: "CLIP vision",
    IpAdapter: "IP-Adapters",
    UpscaleModel: "Upscalers",
    LatentUpscaleModel: "Latent upscalers",
    SeedVr2: "SeedVR2",
    HunyuanImage3: "HunyuanImage 3",
    CustomNode: "Custom nodes",
  };
  const KIND_ORDER = Object.keys(KIND_LABELS);

  // Grouped by kind, which now names one loader's file list each.
  function groupedSlots() {
    const seen = new Map();
    for (const s of state.slots) {
      const k = s.kind;
      if (!seen.has(k)) seen.set(k, []);
      seen.get(k).push(s);
    }
    const order = [...KIND_ORDER, ...[...seen.keys()].filter((k) => !KIND_ORDER.includes(k))];
    return order
      .filter((k) => seen.has(k))
      .map((k) => ({
        kind: k,
        label: KIND_LABELS[k] || k,
        slots: seen.get(k).sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: "base" })),   // A–Z, case-insensitive
      }));
  }

  function render() {
    $status.textContent = summary();
    $slots.innerHTML = groupedSlots().map((g) => {
      const missing = g.slots.filter((s) => !s.boundFile).length;
      return `<section class="slot-group">
        <h3 class="slot-group-h">${esc(g.label)}
          <span class="slot-group-n">${g.slots.length - missing}/${g.slots.length}</span></h3>
        ${g.slots.map(slotRow).join("")}
      </section>`;
    }).join("");
    $slots.querySelectorAll(".slot-pick").forEach((sel) => {
      sel.addEventListener("change", async () => {
        sel.disabled = true;
        try {
          const res = await fetch("/forge/catalog/binding", {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ slotId: sel.dataset.slot, fileName: sel.value || null }),
          });
          if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error || res.status);
          // Reload rather than patching in place: binding a slot can make a workflow ready, and the whole point
          // of this page is showing that consequence.
          await load();
        } catch (err) {
          $status.textContent = `Could not save that: ${err.message || err}`;
          sel.disabled = false;
        }
      });
    });

    // Installing a pack puts it on disk; ComfyUI imports custom nodes at startup ONLY, so its nodes are not
    // usable until the renderer restarts. Saying so is the difference between "it didn't work" and "one more step".
    $slots.querySelectorAll(".slot-install").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const label = btn.textContent;
        btn.disabled = true;
        btn.textContent = "Installing…";
        try {
          const r = await fetch("/api/comfy-patches/apply", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ id: btn.dataset.patch, overwrite: false }),
          });
          const body = await r.json().catch(() => ({}));
          if (!r.ok) { $status.textContent = body.error || "Could not install it."; return; }
          $status.textContent = body.note || "Installed — restart the renderer for its nodes to load.";
          await load();
        } catch (err) {
          $status.textContent = `Could not install it: ${err.message || err}`;
        } finally { btn.disabled = false; btn.textContent = label; }
      });
    });
  }

  load();
})();
