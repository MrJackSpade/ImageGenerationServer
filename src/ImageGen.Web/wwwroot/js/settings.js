// Settings page: per-model banned tags/artists. Uses core.js.
// (The random-prompt temperature is on the composer's Random prompt slider, where 0 means off.)

// --- banned tags/artists (per model) ------------------------------------------------------------
(async function () {
  const sel = $("banModel"), lists = $("banLists"), form = $("banAddForm"), input = $("banInput"), kindSel = $("banKind");
  if (!sel || !lists) return;
  const models = {};   // id -> { name, tagging }
  const bans = {};     // id -> { artists:[], tags:[] }
  const ensureModel = (id, name, tagging) => { if (!models[id]) models[id] = { name: name || id, tagging: tagging || null }; };

  // Present, tagging-capable generate configurations from the gateway. /workflows already returns only runnable
  // (present + VRAM-fitting) configs, so no client-side presence check is needed.
  //
  // Both loads below record WHY they came back empty. Swallowing that would produce an empty `models`, which falls
  // through to "No tagging workflows found" with the ban form hidden — a definite statement about this machine's
  // catalog, printed because a fetch failed. Worse, it would hide the editor for bans that do exist and were simply
  // not fetched.
  let loadError = "";
  try {
    const r = await fetch(`${GATEWAY}/workflows`);
    if (!r.ok) throw new Error(`the catalog answered ${r.status}`);
    for (const w of ((await r.json()) || [])) {
      const tg = w.card && w.card.tagging;
      if (w.kind === "generate" && tg && (tg.tags || tg.artists))
        ensureModel(w.id, w.friendlyName, tg);
    }
  } catch (e) {
    loadError = e.message || String(e);
    console.error("Workflow catalog could not be loaded on the settings page:", e);
  }

  // Existing bans (also surfaces ids for models no longer installed, so they can be cleaned up).
  try {
    const groups = await fetchAllBans();
    for (const g of (groups || [])) { bans[g.modelId] = { artists: g.artists || [], tags: g.tags || [] }; ensureModel(g.modelId, g.modelId, null); }
  } catch (e) {
    loadError = e.message || String(e);
    console.error("Existing bans could not be loaded:", e);
  }

  const ids = Object.keys(models).sort((a, b) => models[a].name.localeCompare(models[b].name, undefined, { sensitivity: "base" }));
  sel.innerHTML = "";
  if (!ids.length) {
    sel.innerHTML = `<option>${loadError ? "Couldn’t load workflows" : "No tagging workflows found"}</option>`;
    sel.disabled = true; if (form) form.hidden = true;
    lists.textContent = loadError ? `Your bans couldn’t be loaded — ${loadError}.` : "";
    return;
  }
  for (const id of ids) { const o = document.createElement("option"); o.value = id; o.textContent = models[id].name; sel.appendChild(o); }

  const curId = () => sel.value;
  const curBans = () => bans[curId()] || (bans[curId()] = { artists: [], tags: [] });

  function render() {
    const b = curBans();
    lists.innerHTML = "";
    let any = false;
    for (const [kind, arr] of [["artist", b.artists], ["tag", b.tags]]) {
      if (!arr.length) continue; any = true;
      const row = document.createElement("div"); row.className = "ban-group";
      const h = document.createElement("span"); h.className = "ban-group-h"; h.textContent = kind === "artist" ? "Artists" : "Tags"; row.appendChild(h);
      for (const name of arr) {
        const chip = document.createElement("span"); chip.className = "tagchip " + kind + " banned";
        chip.innerHTML = `<span class="tc-name">${escapeHtml(name.replace(/_/g, " "))}</span>`;
        const x = document.createElement("button"); x.type = "button"; x.className = "tc-x"; x.textContent = "×"; x.title = "Remove ban";
        x.addEventListener("click", () => removeBan(kind, name));
        chip.appendChild(x); row.appendChild(chip);
      }
      lists.appendChild(row);
    }
    if (!any) { const e = document.createElement("p"); e.className = "settings-desc"; e.style.margin = "0"; e.textContent = "No bans for this workflow yet."; lists.appendChild(e); }
    // Limit the kind selector to what the model supports (when we know its tagging).
    const tg = models[curId()] && models[curId()].tagging;
    if (kindSel && tg) {
      [...kindSel.options].forEach(o => { o.hidden = (o.value === "artist" && !tg.artists) || (o.value === "tag" && !tg.tags); });
      if (kindSel.selectedOptions[0] && kindSel.selectedOptions[0].hidden) kindSel.value = tg.tags ? "tag" : "artist";
    }
  }

  async function addBan(kind, name) {
    const arr = kind === "artist" ? curBans().artists : curBans().tags;
    if (arr.includes(name)) { toast("Already banned"); return; }
    arr.push(name); render();
    try { const r = await postBan(curId(), name, kind); if (!r.ok) throw new Error(`the server answered ${r.status}`); toast("⊘ Banned " + kind + " " + name.replace(/_/g, " ")); }
    catch (e) { console.error("ban save failed:", e); const i = arr.indexOf(name); if (i >= 0) arr.splice(i, 1); render(); toast("Couldn't save"); }
  }
  async function removeBan(kind, name) {
    const arr = kind === "artist" ? curBans().artists : curBans().tags; const i = arr.indexOf(name);
    if (i >= 0) arr.splice(i, 1); render();
    try { const r = await deleteBan(curId(), name, kind); if (!r.ok && r.status !== 404) throw new Error(`the server answered ${r.status}`); toast("Removed ban"); }
    catch (e) { console.error("ban remove failed:", e); arr.push(name); render(); toast("Couldn't remove"); }
  }

  sel.addEventListener("change", render);
  if (form) form.addEventListener("submit", e => {
    e.preventDefault();
    // A comma-separated entry (e.g. "day, night, sun") is several tags, one per segment — not one long token.
    const seen = new Set();
    const names = input.value.split(",").map(normToken).filter(n => n && !seen.has(n) && seen.add(n));
    if (!names.length) return;
    input.value = "";
    const kind = kindSel ? kindSel.value : "tag";
    for (const name of names) addBan(kind, name);
  });
  render();
})();

// NOTE: the generation mask (which tag kinds Random prompt may emit) lives on the composer, under the Random prompt
// slider that it qualifies, and is built by compose.js (buildTagTypes) from the same /api/settings response. It is
// the same account setting on the same PUT route — surfaced there, not here.

// --- pin bookmarks in autocomplete (account toggle, on /settings/configurations) ----------------
// Governs every tag box across the composer/edit/inpaint boxes at once (tagbox.js reads it from /api/settings). Loaded
// disabled so a click before the stored value is known can't persist the wrong state.
(function () {
  const box = $("pinBookmarks");
  if (!box) return;
  fetchSettings()
    .then(s => { box.checked = !!s.pinBookmarks; box.disabled = false; })
    // A failed read leaves the box disabled rather than showing a default the user might then "confirm" by clicking —
    // an unknown value must not masquerade as "off".
    .catch(e => { console.error("Your settings couldn’t be loaded:", e); toast("Couldn’t load your settings — reload"); });
  box.addEventListener("change", async () => {
    try {
      const r = await savePinBookmarks(box.checked);
      if (!r.ok) throw new Error(`the server answered ${r.status}`);
    } catch (e) {
      console.error("pin-bookmarks save failed:", e);
      box.checked = !box.checked;   // revert the optimistic flip so the control reflects what is actually stored
      toast("Couldn’t save");
    }
  });
})();

// --- free VRAM ----------------------------------------------------------------------------------
// Asks the renderer to unload its models. It applies the request between prompts, so clicking this mid-render is
// safe — nothing running is cancelled.
(function () {
  const btn = $("freeVramBtn");
  if (!btn) return;
  btn.addEventListener("click", async () => {
    btn.disabled = true;
    try {
      const r = await fetch(`${GATEWAY}/free-vram`, { method: "POST" });
      toast(r.ok ? "Models unloaded" : "Couldn't free VRAM");
    } catch (e) { console.error("free-vram failed:", e); toast("Couldn't free VRAM"); }
    btn.disabled = false;
  });
})();

// --- restart ComfyUI ----------------------------------------------------------------------------
// Rendered by the view only where this deployment supervises the renderer (the Docker image; CanRestart). Signals
// the entrypoint's supervisor to bounce ComfyUI, which re-reads patches and clears a stuck allocator.
(function () {
  const btn = $("restartComfyBtn");
  if (!btn) return;
  btn.addEventListener("click", async () => {
    btn.disabled = true;
    try {
      const r = await fetch("/api/comfy-patches/restart", { method: "POST" });
      toast(r.ok ? "ComfyUI restarting…" : "Couldn't restart ComfyUI");
    } catch (e) { console.error("comfy restart failed:", e); toast("Couldn't restart ComfyUI"); }
    btn.disabled = false;
  });
})();
