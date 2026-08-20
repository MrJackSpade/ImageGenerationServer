// The LoRA manager (/settings/loras): every LoRA on this box with its cover / CivitAI preview, model name, and trigger
// words. You can redefine the trigger words, choose whether they auto-attach, and refresh a file's CivitAI data (or all
// of them). Consumes JSON from /forge/loras/manage and saves via /api/lora/settings. Turning CivitAI lookups on/off is a
// machine-wide setting and lives on /settings/machine. Nothing blocks: rows render as stubs and fill in as they populate.
(function () {
  const root = document.getElementById("loraManager");
  if (!root) return;
  const THUMB = (typeof THUMB_W !== "undefined" && THUMB_W) || 220;

  const esc = escapeHtml;
  const label = name => String(name || "").split(/[\\/]/).pop().replace(/\.(safetensors|ckpt|pt|gguf)$/i, "");

  const rows = new Map();   // name -> { el, cover, nameEl, trig, edited, aacb }
  const busts = {};         // name -> cache-buster, bumped on refresh so the browser re-fetches the new preview
  let civitaiEnabled = false;
  let pollCancel = null;

  async function load() {
    root.innerHTML = '<div class="lm-loading">Loading LoRAs…</div>';
    let data;
    try {
      const r = await fetch(`${GATEWAY}/loras/manage`);
      if (!r.ok) throw new Error(await gwError(r));
      data = await r.json();
    } catch (e) {
      root.innerHTML = `<div class="lm-error">Couldn't load LoRAs: ${esc(friendlyError(e))}</div>`;
      return;
    }
    render(data);
  }

  function render(data) {
    if (pollCancel) { pollCancel(); pollCancel = null; }
    rows.clear();
    root.innerHTML = "";
    civitaiEnabled = !!data.civitaiEnabled;

    const head = document.createElement("div"); head.className = "lm-head";
    const h = document.createElement("h2"); h.textContent = "LoRAs";
    // The CivitAI on/off switch is a machine-wide setting and lives on the machine settings page, not here.
    // Refresh every LoRA's CivitAI data (only meaningful while lookups are on).
    const refreshAll = document.createElement("button");
    refreshAll.type = "button"; refreshAll.className = "lm-refresh-all"; refreshAll.textContent = "↻ Refresh all";
    refreshAll.title = "Re-fetch trigger words + previews from CivitAI for every LoRA";
    refreshAll.hidden = !civitaiEnabled;
    refreshAll.addEventListener("click", () => refresh(null, [...rows.keys()]));
    head.append(h, refreshAll);
    root.appendChild(head);

    const loras = data.loras || [];
    if (!loras.length) {
      const e = document.createElement("div"); e.className = "lm-empty"; e.textContent = "No LoRAs found on this machine.";
      root.appendChild(e); return;
    }

    const list = document.createElement("div"); list.className = "lm-list";
    for (const l of loras) list.appendChild(row(l));
    root.appendChild(list);

    // Fill in whatever's still populating.
    const pending = loras.filter(l => l.ready === false).map(l => l.name);
    if (pending.length && typeof pollLoraMeta === "function")
      pollCancel = pollLoraMeta(pending, applyMeta);
  }

  // The cover thumbnail: user cover wins; else the CivitAI preview cached on this box (a clip renders in <video> —
  // some previews are mp4); else a two-letter placeholder. Never hotlinks CivitAI.
  function coverHtml(l) {
    if (l.cover)
      return `<img src="${GATEWAY}/image/${encodeURIComponent(l.cover)}?w=${THUMB}" alt="" loading="lazy">`;
    if (l.hasPreview) {
      const src = loraPreviewUrl(l.name, busts[l.name]);
      return l.previewVideo
        ? `<video src="${esc(src)}" muted loop autoplay playsinline></video>`
        : `<img src="${esc(src)}" alt="" loading="lazy">`;
    }
    return `<div class="lm-noimg">${esc((l.displayName || label(l.name)).slice(0, 2).toUpperCase())}</div>`;
  }

  function row(l) {
    const el = document.createElement("div"); el.className = "lm-row" + (l.ready === false ? " loading" : "");

    const cover = document.createElement("div"); cover.className = "lm-cover";
    cover.innerHTML = coverHtml(l);

    const main = document.createElement("div"); main.className = "lm-main";
    const title = document.createElement("div"); title.className = "lm-name";
    const nameSpan = document.createElement("span"); nameSpan.textContent = l.displayName || label(l.name);
    title.appendChild(nameSpan);
    title.title = l.name + (l.modelName ? ` — ${l.modelName} (CivitAI)` : "");
    if (l.folder) { const f = document.createElement("span"); f.className = "lm-folder"; f.textContent = " · " + l.folder; title.appendChild(f); }
    if (l.ready === false) { const s = document.createElement("span"); s.className = "lm-loading-dot"; s.title = "Fetching from CivitAI…"; title.appendChild(s); }

    const trigRow = document.createElement("div"); trigRow.className = "lm-trigrow";
    const trigLbl = document.createElement("label"); trigLbl.className = "lm-triglbl"; trigLbl.textContent = "Trigger words";
    const trig = document.createElement("input"); trig.type = "text"; trig.className = "lm-trig";
    trig.value = l.triggers || "";
    trig.placeholder = l.defaultTriggers ? l.defaultTriggers + " (CivitAI)" : "none — type words to attach";
    const aa = document.createElement("label"); aa.className = "lm-aa";
    const aacb = document.createElement("input"); aacb.type = "checkbox"; aacb.checked = l.autoAttach !== false;
    aa.append(aacb, document.createTextNode(" auto-attach to prompt"));

    // Per-LoRA refresh: re-fetch this file's CivitAI data. Hidden while lookups are off.
    const refresh1 = document.createElement("button");
    refresh1.type = "button"; refresh1.className = "lm-refresh"; refresh1.textContent = "↻";
    refresh1.title = "Re-fetch this LoRA's trigger words + preview from CivitAI";
    refresh1.hidden = !civitaiEnabled;
    refresh1.addEventListener("click", () => refresh(refresh1, [l.name]));

    const entry = { el, cover, nameEl: nameSpan, trig, edited: false, aacb, userCover: l.cover };
    rows.set(l.name, entry);

    let saveTimer = null;
    const save = () => {
      clearTimeout(saveTimer);
      saveTimer = setTimeout(async () => {
        try { await postLoraSettings(l.name, trig.value, aacb.checked); }
        catch (e) { console.error("lora meta save failed:", e); toast("Couldn't save"); }
      }, 400);
    };
    trig.addEventListener("input", () => { entry.edited = true; save(); });
    aacb.addEventListener("change", save);

    trigRow.append(trigLbl, trig, aa, refresh1);
    main.append(title, trigRow);
    el.append(cover, main);
    return el;
  }

  // A poll round: patch each affected row's preview, name, and default-trigger placeholder in place. The user's own
  // typed override is never overwritten — only an untouched, unset field takes on the CivitAI default.
  function applyMeta(map) {
    for (const name of Object.keys(map)) {
      const m = map[name], entry = rows.get(name);
      if (!entry) continue;
      // Never overwrite the user's own chosen cover — only fill the CivitAI preview in when there's no cover set.
      if (!entry.userCover)
        entry.cover.innerHTML = coverHtml({ name, cover: null, hasPreview: m.hasPreview, previewVideo: m.previewVideo, displayName: m.displayName });
      entry.nameEl.textContent = m.displayName || label(name);
      entry.el.classList.toggle("loading", m.ready === false);
      if (m.ready !== false) { const dot = entry.el.querySelector(".lm-loading-dot"); if (dot) dot.remove(); }
      const def = m.triggers || "";   // meta triggers = override ?? civitai default; here the override is unknown, so
      entry.trig.placeholder = def ? def + " (CivitAI)" : "none — type words to attach";
      if (!entry.edited && !entry.trig.value) entry.trig.value = def;
    }
  }

  // Refresh: drop the cache for these files server-side and re-poll them. Marks the rows loading and bumps the
  // preview cache-buster so the new image replaces the old once it lands.
  async function refresh(btn, names) {
    names = (names || []).filter(n => rows.has(n));
    if (!names.length) return;
    if (btn) btn.disabled = true;
    try {
      const r = await postLoraRefresh(names);
      if (!r.ok) throw 0;
      names.forEach(n => { busts[n] = Date.now(); const e = rows.get(n); if (e) { e.el.classList.add("loading"); e.edited = false; } });
      if (pollCancel) pollCancel();
      pollCancel = (typeof pollLoraMeta === "function") ? pollLoraMeta(names, applyMeta) : null;
      toast(names.length > 1 ? `Refreshing ${names.length} LoRAs…` : "Refreshing…");
    } catch (e) { console.error("lora refresh failed:", e); toast("Couldn't refresh"); }
    finally { if (btn) btn.disabled = false; }
  }

  load();
})();
