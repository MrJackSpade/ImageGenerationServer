//TODO: CHECK FOR FALLBACKS
// Settings page: per-model banned tags/artists. Uses core.js.
// (The random-prompt temperature moved to the composer's Random prompt slider, where 0 means off.)

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
  // Both loads below record WHY they came back empty. Swallowing that produced an empty `models`, which fell through
  // to "No tagging workflows found" with the ban form hidden — a definite statement about this machine's catalog,
  // printed because a fetch failed. Worse, it hid the editor for bans that do exist and were simply not fetched.
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
    try { const r = await postBan(curId(), name, kind); if (!r.ok) throw 0; toast("⊘ Banned " + kind + " " + name.replace(/_/g, " ")); }
    catch (_) { const i = arr.indexOf(name); if (i >= 0) arr.splice(i, 1); render(); toast("Couldn't save"); }
  }
  async function removeBan(kind, name) {
    const arr = kind === "artist" ? curBans().artists : curBans().tags; const i = arr.indexOf(name);
    if (i >= 0) arr.splice(i, 1); render();
    try { const r = await deleteBan(curId(), name, kind); if (!r.ok && r.status !== 404) throw 0; toast("Removed ban"); }
    catch (_) { arr.push(name); render(); toast("Couldn't remove"); }
  }

  sel.addEventListener("change", render);
  if (form) form.addEventListener("submit", e => {
    e.preventDefault();
    const name = normToken(input.value); if (!name) return;
    input.value = ""; addBan(kindSel ? kindSel.value : "tag", name);
  });
  render();
})();

// NOTE: the generation mask (which tag kinds Random prompt may emit) used to be built here. It now lives on the
// composer, under the Random prompt slider that it qualifies, and is built by compose.js (buildTagTypes) from the
// same /api/settings response. It is still the same account setting on the same PUT route — only the UI moved.

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
    } catch (_) { toast("Couldn't free VRAM"); }
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
    } catch (_) { toast("Couldn't restart ComfyUI"); }
    btn.disabled = false;
  });
})();
