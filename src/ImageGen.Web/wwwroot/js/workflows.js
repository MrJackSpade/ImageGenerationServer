// Workflows library index: every workflow on this machine, ready or not. Favorites pin to the top with a ★.
// Each row: ★ toggle | name + gen/edit badge + your tags | avg time | size | hide toggle. Click the name → the
// workflow's detail page. Star and hide are editable here; tags are edited on the detail page. Uses core.js.
//
// A workflow missing a model file is shown, greyed, with a ⚠ that opens a dialog for setting the files it needs.
// Models and workflows are two things: the models page binds files to slots, this page is the workflows and what
// each one is waiting for.
(function () {
  const $list = document.getElementById("workflowsList");
  if (!$list) return;

  let WORKFLOWS = [], STATUS = null, favs = new Set(), hidden = new Set(), tags = {}, removed = {};
  // False until the user's real preferences are in hand. Every write below sends the WHOLE set, so acting on
  // preferences we failed to load would overwrite them with whatever this page happened to start with.
  let prefsOk = false;
  let listError = "";
  const gb = b => b ? (b / 1073741824).toFixed(1) + " GB" : "";
  const secs = s => s ? fmtDuration(s) : "";
  const nameOf = m => m.friendlyName || m.id;

  // Which renderer patches can install a missing pack. A custom_node slot is not a file you pick, so without
  // this the dialog below offers an empty dropdown and no way forward — see PATCHES_BY_SLOT's use in slotField.
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
      // Not being able to offer an install is not a reason to fail the page: everything else still works.
      console.error("workflows: could not read the patch list", e);
    }
  }

  async function load() {
    const [rows, status, prefs] = await Promise.all([
      // A failed catalog call is not an empty catalog. Resolving it to [] would render as "No workflows are installed
      // on this machine" — a confident statement about the box, made because a fetch failed.
      fetch(`${GATEWAY}/workflows`).then(r => {
        if (!r.ok) throw new Error(`the catalog answered ${r.status}`);
        return r.json();
      }).catch(e => { listError = e.message || String(e); return null; }),
      // The complete picture, including what is NOT runnable and why. The list above is only what is ready.
      fetch(`${GATEWAY}/catalog/status`).then(r => r.ok ? r.json() : null).catch(e => { console.debug("catalog status unavailable:", e); return null; }),
      loadWorkflowPrefs(),
      loadPatches(),
    ]);
    WORKFLOWS = rows || [];
    STATUS = status;
    prefsOk = prefs.ok;
    favs = prefs.favs;
    hidden = prefs.hidden;
    tags = prefs.tags;
    removed = prefs.removed;
    render();
    openRequestedDialog();
  }

  // ?configure=<id> lands straight on the dialog. It is how the detail page sends someone here: that page can
  // only describe workflows that RUN, so an unconfigured one has to be fixed where the fixing happens.
  function openRequestedDialog() {
    const wanted = new URLSearchParams(location.search).get("configure");
    if (!wanted) return;
    const m = rows().find(w => w.id === wanted);
    if (m) openSlotDialog(m);
    // Drop the parameter so a reload, or a back-navigation, is not the dialog again.
    history.replaceState(null, "", location.pathname);
  }

  // One row per workflow the catalogue knows about, carrying whatever the eligible-list knew about it (timing,
  // size, kind) and what the status knows (whether it can run, and what it wants).
  function rows() {
    const byId = new Map(WORKFLOWS.map(w => [w.id, w]));
    if (!STATUS) return WORKFLOWS.map(w => ({ ...w, ready: true, missingSlots: [] }));
    return STATUS.workflows
      .map(s => ({ ...(byId.get(s.id) || { id: s.id, friendlyName: s.friendlyName }), ...s }));
  }

  function render() {
    if (listError) { $list.innerHTML = `<p class="muted">The workflow list couldn’t be loaded — ${escapeHtml(listError)}.</p>`; return; }
    const all = rows();
    if (!all.length) { $list.innerHTML = '<p class="muted">No workflows in the catalogue.</p>'; return; }
    const sorted = all.slice().sort((a, b) => {
      const af = favs.has(a.id) ? 0 : 1, bf = favs.has(b.id) ? 0 : 1;   // favorites first
      if (af !== bf) return af - bf;
      if (a.ready !== b.ready) return a.ready ? -1 : 1;                 // then what actually runs
      return nameOf(a).localeCompare(nameOf(b), undefined, { sensitivity: "base" });   // name, case-insensitive
    });
    $list.innerHTML = "";
    for (const m of sorted) $list.appendChild(row(m));
  }

  // The kind badge shows the config's specific resolved kind (#163): gen, edit, inpaint, outpaint, redraw, upscale,
  // effect, animate, or v2v. Generate keeps the "is-gen" color; every editor kind shares "is-edit". A missing kind
  // (an older payload) reads as generate.
  function kindBadge(kind) {
    const gen = !kind || kind === "generate";
    const label = gen ? "gen" : (kind === "videoedit" ? "v2v" : kind);
    return `<span class="listrow-badge ${gen ? "is-gen" : "is-edit"}">${label}</span>`;
  }

  function row(m) {
    const el = document.createElement("div");
    el.className = "listrow" + (hidden.has(m.id) ? " is-hidden" : "") + (m.ready === false ? " is-unavailable" : "");
    const t = computeWorkflowTags(m.card && m.card.tags, tags[m.id], removed[m.id]);
    const missing = (m.missingSlots || []).length;
    el.innerHTML =
      `<button class="listrow-star${favs.has(m.id) ? " on" : ""}" title="Favorite" aria-label="Favorite">★</button>`
      + `<a class="listrow-main" href="/settings/workflows/${encodeURIComponent(m.id)}">`
      + `<span class="listrow-name">${escapeHtml(nameOf(m))}</span>`
      + kindBadge(m.kind)
      + (m.isVariant ? `<span class="listrow-badge is-variant" title="A duplicate you created on this machine">variant</span>` : "")
      + (t.length ? `<span class="listrow-tags">${t.map(x => `<span class="wftag">${escapeHtml(x)}</span>`).join("")}</span>` : "")
      + `</a>`
      + (missing
        ? `<button class="listrow-warn" title="${missing} model file${missing === 1 ? "" : "s"} not set — click to set ${missing === 1 ? "it" : "them"}" aria-label="Set its models">⚠</button>`
        : "")
      + `<span class="listrow-stat" title="Average render time">${secs(m.avgSeconds)}</span>`
      + `<span class="listrow-stat" title="Workflow size">${gb(m.sizeBytes)}</span>`
      + `<button class="listrow-hide${hidden.has(m.id) ? " on" : ""}" title="${hidden.has(m.id) ? "Unhide from the picker" : "Hide from the picker"}" aria-label="Hide">${hidden.has(m.id) ? "🚫" : "👁"}</button>`;
    el.querySelector(".listrow-star").addEventListener("click", e => { e.preventDefault(); toggleFav(m.id); });
    el.querySelector(".listrow-hide").addEventListener("click", e => { e.preventDefault(); toggleHide(m.id); });
    const warn = el.querySelector(".listrow-warn");
    if (warn) warn.addEventListener("click", e => { e.preventDefault(); openSlotDialog(m); });

    // A workflow that cannot run has no detail page worth reaching: that page lists what a workflow IS from the
    // eligible set, which by definition does not contain this one, so following the link lands on "unavailable".
    // The thing anyone clicking it wants is the reason and the fix, which is this dialog.
    if (m.ready === false) {
      const link = el.querySelector(".listrow-main");
      link.setAttribute("title", "Not ready — click to set what it needs");
      link.addEventListener("click", e => {
        if (e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return;   // a deliberate new tab is still theirs
        e.preventDefault();
        openSlotDialog(m);
      });
    }
    return el;
  }

  // --- "set its models" dialog ---------------------------------------------------------------------
  // Every slot the workflow needs, not only the empty ones: a wrong binding is as much a reason it will not run,
  // and hunting for it on another page to correct it is the thing this replaces.
  let $modal = null;

  function slotById(id) { return (STATUS && STATUS.slots.find(s => s.id === id)) || null; }

  // One row of the dialog. A model slot is a file you point at; a CUSTOM NODE is not — it is a pack that is
  // either installed or it isn't, and a file dropdown for one is an empty list with no way forward. Where a
  // patch installs that pack, offer that instead.
  function slotField(s) {
    const patch = PATCHES_BY_SLOT.get(s.id);
    const effective = s.effectiveFile || s.boundFile;
    const label = `<span class="fld-label">${escapeHtml(s.label)}${effective ? "" : ' <span class="wf-slot-empty">not set</span>'}</span>`;

    if (patch) {
      const installed = patch.state === "Applied";
      const awaitingRestart = installed && patch.restartRequired;
      return `<div class="wf-slot">
          <span class="fld-label">${escapeHtml(s.label)}${installed
            ? (awaitingRestart ? ' <span class="wf-slot-empty">restart required</span>' : "")
            : ' <span class="wf-slot-empty">not installed</span>'}</span>
          ${installed
            ? (awaitingRestart
              ? `<div class="restart-required" role="alert"><strong>Restart ComfyUI to finish installing this node.</strong>
                   The files are on disk, but the running renderer has not loaded them yet. This workflow remains unavailable until it does.
                   <a class="link-btn" href="/settings/patches">Open renderer restart →</a></div>`
              : `<p class="settings-desc">Installed and loaded by the renderer.</p>`)
            : `<p class="settings-desc">${escapeHtml(patch.why)}</p>
               <button type="button" class="settings-btn slot-install" data-patch="${escapeHtml(patch.id)}">Install ${escapeHtml(patch.title)}</button>`}
        </div>`;
    }

    const source = s.source === "pinned" ? "Pinned to this workflow."
      : s.source === "shared" ? "Using shared default." : "No shared default has been set.";
    const action = s.source === "pinned"
      ? `<button type="button" class="settings-btn slot-use-shared" data-slot="${escapeHtml(s.id)}">Use shared default</button>`
      : s.source === "shared" && effective
        ? `<button type="button" class="settings-btn slot-pin-current" data-slot="${escapeHtml(s.id)}">Pin current model</button>`
        : "";
    const first = s.source === "unbound"
      ? `<p class="wf-slot-shared">The first selection will establish the shared default for workflows that inherit this slot.</p>`
      : "";
    return `<div class="wf-slot">
        ${label}
        <select class="fld-input slot-pick" data-slot="${escapeHtml(s.id)}">${slotOptionsHtml(s, false)}</select>
        <p class="settings-desc">${source}</p>${action}${first}
      </div>`;
  }

  // Applying a patch puts the pack on disk. ComfyUI imports custom nodes at startup ONLY, so nothing it
  // installs is usable until the renderer restarts — saying so is the difference between "it didn't work" and
  // "there is one more step".
  function wireInstallButtons(scope, onDone) {
    scope.querySelectorAll(".slot-install").forEach(btn => {
      btn.addEventListener("click", async () => {
        const label = btn.textContent;
        btn.disabled = true;
        btn.textContent = "Installing…";
        try {
          const r = await fetch("/api/comfy-patches/apply", {
            method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ id: btn.dataset.patch, overwrite: false }),
          });
          const body = await r.json().catch(() => ({}));
          if (!r.ok) { toast(body.error || "Couldn’t install it"); return; }
          window.alert("Installed on disk. Restart ComfyUI before using this workflow; the node is not loaded until ComfyUI starts again.");
          await loadPatches();
          if (onDone) onDone();
        } catch (e) {
          console.error("workflows: install failed", e);
          toast("Couldn’t install it");
        } finally { btn.disabled = false; btn.textContent = label; }
      });
    });
  }

  async function openSlotDialog(m) {
    if (!STATUS) { toast("The catalogue status isn’t loaded"); return; }
    // This call is intentionally made on EVERY open. The server compares the installed pack with ComfyUI's live
    // node registry, so the warning survives closing/reopening and disappears only after a real renderer restart.
    await loadPatches();
    if (!$modal) {
      $modal = document.createElement("div");
      $modal.className = "modal-overlay hidden";
      document.body.appendChild($modal);
      $modal.addEventListener("click", e => { if (e.target === $modal) closeDialog(); });
    }

    let configSlots;
    try {
      const response = await fetch(`${GATEWAY}/catalog/config/${encodeURIComponent(m.id)}/slots`);
      if (!response.ok) throw new Error(`the server answered ${response.status}`);
      configSlots = await response.json();
    } catch (e) {
      toast(`Couldn't load this workflow's models: ${e.message || e}`);
      return;
    }
    const scopedById = new Map(configSlots.map(s => [s.id, s]));
    const required = m.requiredSlots || m.missingSlots || [];
    const body = required.map(id => {
      const s = scopedById.get(id) || slotById(id);   // node packs are intentionally absent from configSlots
      if (!s) return `<p class="muted">${escapeHtml(id)} — unknown slot.</p>`;
      return slotField(s);
    }).join("");

    $modal.innerHTML = `<div class="modal-card wf-slot-card">
        <h3>${escapeHtml(nameOf(m))}</h3>
        <p class="settings-desc">Point each of these at a file ComfyUI can see. Anything it does not list is not on the renderer's disk.</p>
        ${body || '<p class="muted">This workflow needs no model files.</p>'}
        <div class="modal-actions"><button type="button" class="settings-btn" data-close>Done</button></div>
      </div>`;
    $modal.classList.remove("hidden");
    $modal.querySelector("[data-close]").addEventListener("click", closeDialog);
    // Re-open on the fresh patch state so the row it just installed stops offering the button.
    wireInstallButtons($modal, () => { dirty = true; openSlotDialog(m); });
    $modal.querySelectorAll(".slot-pick").forEach(sel => {
      sel.addEventListener("change", async () => {
        sel.disabled = true;
        try {
          const res = await fetch(`${GATEWAY}/catalog/config/${encodeURIComponent(m.id)}/binding`, {
            method: "PUT", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ slotId: sel.dataset.slot, fileName: sel.value || null }),
          });
          const answer = await res.json().catch(() => ({}));
          if (!res.ok) throw new Error(answer.error || res.status);
          toast(answer.result === "shared-created" ? "Established the shared default" : "Pinned to this workflow");
          dirty = true;
          await openSlotDialog(m);
        } catch (err) {
          toast(`Couldn't save that: ${err.message || err}`);
        } finally { sel.disabled = false; }
      });
    });
    $modal.querySelectorAll(".slot-pin-current").forEach(btn => {
      btn.addEventListener("click", () => {
        const sel = Array.from($modal.querySelectorAll(".slot-pick"))
          .find(candidate => candidate.dataset.slot === btn.dataset.slot);
        if (sel) sel.dispatchEvent(new Event("change"));
      });
    });
    $modal.querySelectorAll(".slot-use-shared").forEach(btn => {
      btn.addEventListener("click", async () => {
        btn.disabled = true;
        try {
          const res = await fetch(`${GATEWAY}/catalog/config/${encodeURIComponent(m.id)}/binding/${encodeURIComponent(btn.dataset.slot)}`, { method: "DELETE" });
          if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error || res.status);
          toast("Using shared default");
          dirty = true;
          await openSlotDialog(m);
        } catch (err) {
          toast(`Couldn't save that: ${err.message || err}`);
          btn.disabled = false;
        }
      });
    });
  }

  // A selection can make this workflow runnable (and a first shared default can unlock other inheritors), so the
  // list is rebuilt on close rather than after every change while the dialog is still open.
  let dirty = false;
  function closeDialog() {
    $modal.classList.add("hidden");
    if (dirty) { dirty = false; load(); }
  }

  // Both writes PUT the entire set, so both are gated on having actually loaded it.
  const canWritePrefs = () => prefsOk || (toast("Your saved preferences didn’t load — reload before changing them"), false);

  async function toggleFav(id) {
    if (!canWritePrefs()) return;
    const wasOn = favs.has(id);
    if (wasOn) favs.delete(id); else favs.add(id);
    render();
    try { await saveFavoriteWorkflows([...favs]); }
    catch (e) {
      if (wasOn) favs.add(id); else favs.delete(id);
      render(); console.error("save favorites failed:", e); toast("Couldn't save");
    }
  }
  async function toggleHide(id) {
    if (!canWritePrefs()) return;
    const wasOn = hidden.has(id);
    if (wasOn) hidden.delete(id); else hidden.add(id);
    render();
    try { await saveHiddenWorkflows([...hidden]); }
    catch (e) {
      if (wasOn) hidden.add(id); else hidden.delete(id);
      render(); console.error("save hidden failed:", e); toast("Couldn't save");
    }
  }

  load();
})();
