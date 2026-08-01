// A single workflow's page: its info (architecture/summary), kind, average render time, file size, a ★ favorite
// toggle, a hide-from-picker toggle, an editable list of your custom tags, and a recents grid of images this
// workflow produced (history filtered by its config id, reusing the imgcard markup + lightbox). Uses core.js.
(function () {
  const $root = document.getElementById("workflowDetail");
  if (!$root) return;
  const id = $root.dataset.id;

  let workflow = null, STATUS = null, favs = new Set(), hidden = new Set(), hiddenApi = new Set(), tags = {};
  // As on the library page: the star/hide/tag writes each send the WHOLE set, so none of them may run against
  // preferences that were never loaded.
  let prefsOk = false;
  let listError = "";
  const gb = b => b ? (b / 1073741824).toFixed(1) + " GB" : "—";
  const fmtDate = ts => { try { return new Date(ts).toLocaleDateString(undefined, { month: "short", day: "numeric" }); } catch (_) { return ""; } };

  async function load() {
    const [rows, status, prefs] = await Promise.all([
      fetch(`${GATEWAY}/workflows`).then(r => {
        if (!r.ok) throw new Error(`the catalog answered ${r.status}`);
        return r.json();
      }).catch(e => { listError = e.message || String(e); return null; }),
      // The complete picture, including what is NOT runnable and why — the eligible list above cannot tell an
      // unconfigured workflow from one that does not exist.
      fetch(`${GATEWAY}/catalog/status`).then(r => r.ok ? r.json() : null).catch(() => null),
      loadWorkflowPrefs(),
    ]);
    workflow = (rows || []).find(m => m.id === id) || null;
    STATUS = status;
    prefsOk = prefs.ok;
    favs = prefs.favs;
    hidden = prefs.hidden;
    hiddenApi = prefs.hiddenApi;
    tags = prefs.tags;
    renderInfo();
    if (workflow) { loadSettings(); loadRecents(); }
  }

  function renderInfo() {
    // "Not installed here" is a claim about the MACHINE, and it was being made whenever the catalog call failed —
    // the empty list a failed fetch resolved to is indistinguishable from a list this id is genuinely absent from.
    if (listError) {
      $root.innerHTML = `<div class="wf-head"><h2>Workflow unavailable</h2></div>`
        + `<p class="muted">The workflow catalog couldn’t be loaded — ${escapeHtml(listError)}. `
        + `<a href="/settings/workflows">Back to the library</a>.</p>`;
      return;
    }
    if (!workflow) {
      // "Not installed here" was said about anything absent from the ELIGIBLE list, which is mostly wrong: the
      // common reason a workflow is not eligible is that a model slot it needs is unset, and it is very much
      // installed. The status list knows the difference, and the fix lives in the library's dialog.
      const known = STATUS && STATUS.workflows.find(w => w.id === id);
      $root.innerHTML = known
        ? `<div class="wf-head"><h2>${escapeHtml(known.friendlyName || id)}</h2></div>`
          + `<p class="muted">This workflow can't run yet — ${known.missingSlots.length} model slot`
          + `${known.missingSlots.length === 1 ? " is" : "s are"} not set. `
          + `<a href="/settings/workflows?configure=${encodeURIComponent(id)}">Set what it needs →</a></p>`
        : `<div class="wf-head"><h2>Workflow unavailable</h2></div>`
          + `<p class="muted">No workflow with that id is in the catalogue. <a href="/settings/workflows">Back to the library</a>.</p>`;
      return;
    }
    const c = workflow.card || {};
    const fav = favs.has(workflow.id), hid = hidden.has(workflow.id), hidApi = hiddenApi.has(workflow.id);
    $root.innerHTML =
      `<div class="wf-head">`
      + `<button id="mdStar" class="listrow-star${fav ? " on" : ""}" title="Favorite" aria-label="Favorite">★</button>`
      + `<h2>${escapeHtml(workflow.friendlyName || workflow.id)}</h2>`
      + `<span class="listrow-badge ${workflow.kind === "edit" ? "is-edit" : "is-gen"}">${workflow.kind === "edit" ? "edit" : "gen"}</span>`
      + `<button id="mdHide" class="wf-toggle${hid ? " on" : ""}">${hid ? "Unhide from picker" : "Hide from picker"}</button>`
      + `<button id="mdHideApi" class="wf-toggle${hidApi ? " on" : ""}">${hidApi ? "Unhide from API" : "Hide from API"}</button>`
      + `</div>`
      + `<div class="wf-meta">`
      + `<span>⏱ ${workflow.avgSeconds ? fmtDuration(workflow.avgSeconds) + " avg" : "no timing yet"}</span>`
      + `<span>💾 ${gb(workflow.sizeBytes)}</span>`
      + (c.architecture ? `<span>${escapeHtml(c.architecture)}</span>` : "")
      + `</div>`
      + (c.summary ? `<p class="wf-summary">${escapeHtml(c.summary)}</p>` : "")
      + `<div class="wf-tags-edit"><label class="fld-label">Your tags</label>`
      + `<div id="mdTags" class="wftag-list"></div>`
      + `<form id="mdTagForm" class="wftag-add"><input id="mdTagInput" placeholder="add a tag…" maxlength="40" autocomplete="off"><button type="submit">Add</button></form></div>`
      + `<div class="hist-head" style="margin-top:22px"><h2>Settings for this machine</h2></div>`
      + `<div id="mdSettings"><p class="muted">Loading…</p></div>`
      + `<div class="hist-head" style="margin-top:22px"><h2>Recent from this workflow</h2></div>`
      + `<div id="mdRecents" class="cardgrid"><p class="muted">Loading…</p></div>`;
    document.getElementById("mdStar").addEventListener("click", toggleFav);
    document.getElementById("mdHide").addEventListener("click", toggleHide);
    document.getElementById("mdHideApi").addEventListener("click", toggleHideApi);
    document.getElementById("mdTagForm").addEventListener("submit", addTag);
    renderTags();
  }

  function renderTags() {
    const box = document.getElementById("mdTags"); if (!box) return;
    const list = tags[workflow.id] || [];
    box.innerHTML = list.length
      ? list.map((t, i) => `<span class="wftag editable">${escapeHtml(t)}<button data-i="${i}" class="wftag-x" aria-label="Remove">✕</button></span>`).join("")
      : '<span class="muted">No tags yet — add labels to organize this workflow in the picker.</span>';
    box.querySelectorAll(".wftag-x").forEach(b => b.addEventListener("click", () => removeTag(+b.dataset.i)));
  }

  // Every write here PUTs the whole set (tags map, favorites array, hidden array), so all three are gated on the
  // preferences having actually loaded — writing over an unknown value is how the stored one gets lost.
  const canWritePrefs = () => prefsOk || (toast("Your saved preferences didn’t load — reload before changing them"), false);

  const persistTags = () => saveWorkflowTags(tags).catch(() => toast("Couldn't save tags"));
  async function addTag(e) {
    e.preventDefault();
    if (!canWritePrefs()) return;
    const inp = document.getElementById("mdTagInput"), v = (inp.value || "").trim();
    if (!v) return;
    const list = tags[workflow.id] || (tags[workflow.id] = []);
    if (!list.includes(v)) list.push(v);
    inp.value = ""; renderTags(); await persistTags();
  }
  async function removeTag(i) {
    if (!canWritePrefs()) return;
    const list = tags[workflow.id] || []; list.splice(i, 1);
    if (!list.length) delete tags[workflow.id];
    renderTags(); await persistTags();
  }

  async function toggleFav() {
    if (!canWritePrefs()) return;
    if (favs.has(workflow.id)) favs.delete(workflow.id); else favs.add(workflow.id);
    document.getElementById("mdStar").classList.toggle("on", favs.has(workflow.id));
    try { await saveFavoriteWorkflows([...favs]); } catch (_) { toast("Couldn't save"); }
  }
  async function toggleHide() {
    if (!canWritePrefs()) return;
    const on = !hidden.has(workflow.id);
    if (on) hidden.add(workflow.id); else hidden.delete(workflow.id);
    const h = document.getElementById("mdHide");
    h.classList.toggle("on", on); h.textContent = on ? "Unhide from picker" : "Hide from picker";
    try { await saveHiddenWorkflows([...hidden]); } catch (_) { toast("Couldn't save"); }
  }
  async function toggleHideApi() {
    if (!canWritePrefs()) return;
    const on = !hiddenApi.has(workflow.id);
    if (on) hiddenApi.add(workflow.id); else hiddenApi.delete(workflow.id);
    const h = document.getElementById("mdHideApi");
    h.classList.toggle("on", on); h.textContent = on ? "Unhide from API" : "Hide from API";
    try { await saveHiddenApiWorkflows([...hiddenApi]); } catch (_) { toast("Couldn't save"); }
  }

  // --- this machine's settings for this workflow ---------------------------------------------------
  // What it renders with here, against what the catalogue shipped. Saving writes one override; clearing a field
  // removes it and the shipped value comes back. The render size for each aspect lives here rather than being a
  // second workflow per size, which is what the "-hq" siblings were.
  let settingsData = null;
  const eff = (s) => (s.override !== null && s.override !== undefined ? s.override : s.shipped);
  const overridden = (s) => s.override !== null && s.override !== undefined;

  async function saveSetting(key, value) {
    const r = await fetch(`${GATEWAY}/catalog/override`, {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ configId: id, key: `param.${key}`, value }),
    });
    if (!r.ok) { toast("Couldn't save that"); return false; }
    toast(value === null || value === "" ? "Reset to the shipped value" : "Saved");
    await loadSettings();
    return true;
  }

  function settingRow(s) {
    const row = document.createElement("div");
    row.className = "wf-setting" + (overridden(s) ? " is-overridden" : "");

    const label = document.createElement("label");
    label.className = "fld-label";
    label.textContent = s.label;
    if (s.help) { row.title = s.help; label.title = s.help; }
    if (overridden(s)) {
      const tag = document.createElement("button");
      tag.type = "button"; tag.className = "wf-reset";
      tag.textContent = "reset";
      tag.title = `The catalogue ships ${JSON.stringify(s.shipped)}`;
      tag.addEventListener("click", () => saveSetting(s.key, null));
      label.appendChild(document.createTextNode(" ")); label.appendChild(tag);
    }
    row.appendChild(label);

    if (s.type === "aspect") { row.appendChild(aspectEditor(s, settingsData && settingsData.resolution)); return row; }

    let input;
    if (s.type === "bool") {
      input = document.createElement("input"); input.type = "checkbox"; input.className = "fld-input";
      input.checked = String(eff(s)) === "true";
      input.addEventListener("change", () => saveSetting(s.key, String(input.checked)));
    } else if (s.choices && s.choices.length) {
      input = document.createElement("select"); input.className = "fld-input";
      for (const c of s.choices) {
        const o = document.createElement("option"); o.value = c; o.textContent = c;
        if (String(eff(s)) === c) o.selected = true;
        input.appendChild(o);
      }
      input.addEventListener("change", () => saveSetting(s.key, input.value));
    } else {
      input = document.createElement("input");
      input.type = s.type === "int" || s.type === "double" ? "number" : "text";
      input.className = "fld-input";
      if (s.min != null) input.min = s.min;
      if (s.max != null) input.max = s.max;
      if (s.step != null) input.step = s.step;
      input.value = eff(s) == null ? "" : eff(s);
      input.addEventListener("blur", () => {
        const was = eff(s) == null ? "" : String(eff(s));
        if (input.value === was) return;
        saveSetting(s.key, input.value);
      });
    }
    row.appendChild(input);
    return row;
  }

  // The render size, as three width/height pairs. Nobody should have to type a JSON object into a form to make
  // their pictures bigger.
  //
  // Bounded by the MODEL's declared envelope, not by anything invented here. 130 of the configurations publish a
  // resolution block — HunyuanVideo 1.5 720p wants a minimum side of 480 in multiples of 16 — and a box that
  // offered 64 in steps of 8 would be contradicting the model it is configuring. Without a declaration the
  // browser's own validation is left alone rather than a guess being substituted for it.
  function aspectEditor(s, env) {
    const map = eff(s) || {};
    const box = document.createElement("div");
    box.className = "wf-aspect";
    const inputs = {};
    for (const name of ["square", "landscape", "portrait"]) {
      const pair = map[name] || [];
      const cell = document.createElement("div"); cell.className = "wf-aspect-cell";
      const nm = document.createElement("span"); nm.className = "wf-aspect-name"; nm.textContent = name;
      const w = document.createElement("input"); const h = document.createElement("input");
      for (const [el, v, lo, hi] of [[w, pair[0], env && env.minW, env && env.maxW],
                                      [h, pair[1], env && env.minH, env && env.maxH]]) {
        el.type = "number"; el.className = "fld-input";
        if (lo) el.min = lo;
        if (hi) el.max = hi;
        if (env && env.step) el.step = env.step;
        el.value = v == null ? "" : v;
      }
      inputs[name] = [w, h];
      const x = document.createElement("span"); x.className = "wf-aspect-x"; x.textContent = "×";
      cell.append(nm, w, x, h);
      box.appendChild(cell);
    }
    if (env) {
      const note = document.createElement("p");
      note.className = "wf-aspect-note";
      note.textContent = `This model supports ${env.minW}–${env.maxW} wide and ${env.minH}–${env.maxH} tall, `
        + `in multiples of ${env.step}.`;
      box.appendChild(note);
    }
    const save = document.createElement("button");
    save.type = "button"; save.className = "settings-btn"; save.textContent = "Save sizes";
    save.addEventListener("click", () => {
      const out = {};
      for (const name of ["square", "landscape", "portrait"]) {
        const [w, h] = inputs[name].map((el) => parseInt(el.value, 10));
        if (Number.isFinite(w) && Number.isFinite(h)) out[name] = [w, h];
      }
      saveSetting(s.key, JSON.stringify(out));
    });
    box.appendChild(save);
    return box;
  }

  async function loadSettings() {
    const box = document.getElementById("mdSettings"); if (!box) return;
    let data;
    try {
      const r = await fetch(`${GATEWAY}/catalog/config/${encodeURIComponent(id)}/settings`);
      if (!r.ok) throw new Error(`the server answered ${r.status}`);
      data = await r.json();
    } catch (e) {
      box.innerHTML = `<p class="muted">Settings couldn’t be loaded — ${escapeHtml(e.message || String(e))}.</p>`;
      return;
    }
    settingsData = data;
    if (!data.settings.length) { box.innerHTML = '<p class="muted">This workflow has nothing to configure.</p>'; return; }
    box.innerHTML = "";
    const card = document.createElement("section");
    card.className = "settings-card wf-settings";
    for (const s of data.settings) card.appendChild(settingRow(s));
    box.appendChild(card);
  }

  async function loadRecents() {
    const grid = document.getElementById("mdRecents"); if (!grid) return;
    try {
      const d = await queryHistory({ workflow: id, pageSize: 24 });
      const items = d.items || [];
      if (!items.length) { grid.innerHTML = '<p class="muted">No images from this workflow yet.</p>'; return; }
      grid.innerHTML = items.map(r => {
        const iid = encodeURIComponent(r.id), prompt = escapeHtml(r.prompt || "");
        return `<a class="imgcard" href="/image/${iid}">`
          + `<div class="img"><img data-src="${GATEWAY}/image/${iid}?w=${THUMB_W}" alt="${prompt}"></div>`
          + `<div class="meta"><div class="p">${prompt}</div>`
          + `<div class="row"><span class="seed">${escapeHtml(fmtDate(r.ts))}</span></div></div></a>`;
      }).join("");
    } catch (_) { grid.innerHTML = '<p class="muted">Couldn’t load recents.</p>'; }
  }

  load();
})();
