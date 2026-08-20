// LoRA picker: a modal grid over GET /forge/loras. Folder navigation, global search across the whole tree, batch
// checkbox-style selection, cover thumbnails, and compatibility dimming. Pure JSON in — the DOM is built here — and
// the picked LoRAs are handed back to the composer via opts.onAdd. Consumes JSON, never HTML.
(function () {
  const THUMB = (typeof THUMB_W !== "undefined" && THUMB_W) || 220;
  let overlay = null;
  let pollCancel = null;

  const esc = escapeHtml;
  function label(name) { return String(name || "").split(/[\\/]/).pop().replace(/\.(safetensors|ckpt|pt|gguf)$/i, ""); }
  function parentFolder(f) { const i = f.lastIndexOf("/"); return i < 0 ? "" : f.slice(0, i); }
  function relFolder(itemFolder, folder) {
    itemFolder = itemFolder || "";
    if (!folder) return itemFolder;
    if (itemFolder === folder) return "";
    return itemFolder.startsWith(folder + "/") ? itemFolder.slice(folder.length + 1) : "";
  }
  function inFolder(itemFolder, folder) {
    itemFolder = itemFolder || "";
    return folder ? (itemFolder === folder || itemFolder.startsWith(folder + "/")) : true;
  }

  function close() {
    if (pollCancel) { pollCancel(); pollCancel = null; }
    if (!overlay) return;
    overlay.remove(); overlay = null;
    document.removeEventListener("keydown", onKey);
  }
  function onKey(e) { if (e.key === "Escape") close(); }

  window.openLoraPicker = async function (opts) {
    opts = opts || {};
    close();
    const picked = new Set();
    const already = new Set(opts.current || []);
    let all = [];
    let folder = "";   // current folder ("" = root)
    let search = "";
    let showIncompatible = false;   // incompatible LoRAs are HIDDEN by default; a toggle reveals them

    overlay = document.createElement("div");
    overlay.className = "lora-picker-overlay";
    overlay.addEventListener("click", e => { if (e.target === overlay) close(); });

    const modal = document.createElement("div"); modal.className = "lora-picker";
    overlay.appendChild(modal);

    const head = document.createElement("div"); head.className = "lp-head";
    const title = document.createElement("div"); title.className = "lp-title"; title.textContent = "Add LoRAs";
    const searchInput = document.createElement("input");
    searchInput.type = "search"; searchInput.className = "lp-search"; searchInput.placeholder = "Search all LoRAs…";
    const closeBtn = document.createElement("button");
    closeBtn.type = "button"; closeBtn.className = "lp-close"; closeBtn.textContent = "×"; closeBtn.title = "Close";
    closeBtn.addEventListener("click", close);
    head.append(title, searchInput, closeBtn);

    const crumb = document.createElement("div"); crumb.className = "lp-crumb";
    const grid = document.createElement("div"); grid.className = "lp-grid";
    const foot = document.createElement("div"); foot.className = "lp-foot";
    // Incompatible LoRAs are hidden by default; this reveals them (dimmed). Shown only when some exist.
    const incompatToggle = document.createElement("label"); incompatToggle.className = "lp-incompat"; incompatToggle.hidden = true;
    const incompatCb = document.createElement("input"); incompatCb.type = "checkbox";
    const incompatLbl = document.createElement("span");
    incompatToggle.append(incompatCb, incompatLbl);
    incompatCb.addEventListener("change", () => { showIncompatible = incompatCb.checked; render(); });
    const addBtn = document.createElement("button");
    addBtn.type = "button"; addBtn.className = "lp-add primary-btn"; addBtn.textContent = "Add"; addBtn.disabled = true;
    addBtn.addEventListener("click", () => {
      const chosen = all.filter(l => picked.has(l.name));
      if (opts.onAdd) opts.onAdd(chosen);
      close();
    });
    foot.append(incompatToggle, addBtn);

    modal.append(head, crumb, grid, foot);
    document.body.appendChild(overlay);
    document.addEventListener("keydown", onKey);
    searchInput.focus();

    function updateAdd() {
      addBtn.disabled = picked.size === 0;
      addBtn.textContent = picked.size ? `Add ${picked.size}` : "Add";
    }
    function empty(msg) { const d = document.createElement("div"); d.className = "lp-empty"; d.textContent = msg; return d; }

    function folderTile(sub) {
      const t = document.createElement("button"); t.type = "button"; t.className = "lp-tile lp-folder";
      t.innerHTML = `<div class="lp-thumb lp-folder-ic">📁</div><div class="lp-name">${esc(sub)}</div>`;
      t.addEventListener("click", () => { folder = folder ? folder + "/" + sub : sub; render(); });
      return t;
    }

    // Name shown on the tile: CivitAI's model name once populated, else the filename.
    function displayLabel(l) { return l.displayName || label(l.name); }

    // Tile thumbnail: the user's own cover wins; else the CivitAI preview cached on this box (a clip renders in a
    // <video>, since some previews are mp4); else a two-letter placeholder. Never hotlinks CivitAI.
    function thumbHtml(l) {
      if (l.cover)
        return `<img class="lp-thumb" src="${GATEWAY}/image/${encodeURIComponent(l.cover)}?w=${THUMB}" alt="" loading="lazy">`;
      if (l.hasPreview) {
        const src = loraPreviewUrl(l.name);
        return l.previewVideo
          ? `<video class="lp-thumb" src="${esc(src)}" muted loop autoplay playsinline></video>`
          : `<img class="lp-thumb" src="${esc(src)}" alt="" loading="lazy">`;
      }
      return `<div class="lp-thumb lp-noimg">${esc(displayLabel(l).slice(0, 2).toUpperCase())}</div>`;
    }

    function tileInner(l) {
      const badges = (l.compatible === false ? '<span class="lp-badge warn" title="May not fit the selected model">!</span>' : '')
        + (l.clipCapable === false ? '<span class="lp-badge" title="Model-only (no CLIP effect)">M</span>' : '');
      const loading = l.ready === false ? '<span class="lp-loading" title="Fetching details from CivitAI…"></span>' : '';
      return `${thumbHtml(l)}<div class="lp-name">${esc(displayLabel(l))}</div><div class="lp-badges">${badges}</div>${loading}`;
    }

    function loraTile(l) {
      const t = document.createElement("div");
      t.className = "lp-tile lp-lora"
        + (l.compatible === false ? " incompatible" : "")
        + (l.ready === false ? " loading" : "")
        + (picked.has(l.name) ? " picked" : "")
        + (already.has(l.name) ? " already" : "");
      t.dataset.lora = l.name;
      t.innerHTML = tileInner(l);
      t.title = l.name
        + (l.compatible === false ? " — may not fit the selected model" : "")
        + (already.has(l.name) ? " (already added)" : "");
      t.addEventListener("click", () => {
        if (already.has(l.name)) return;   // can't re-add what's already in the stack
        if (picked.has(l.name)) picked.delete(l.name); else picked.add(l.name);
        t.classList.toggle("picked", picked.has(l.name));
        updateAdd();
      });
      return t;
    }

    // Fold a poll update into the `all` list and patch any tile currently rendered for that name — the metadata
    // arriving (name, preview) shouldn't disturb the folder/search view or the user's picks.
    function applyMeta(map) {
      let changed = false;
      for (const l of all) {
        const m = map[l.name];
        if (!m) continue;
        l.displayName = m.displayName; l.triggers = m.triggers; l.autoAttach = m.autoAttach;
        l.ready = m.ready; l.hasPreview = m.hasPreview; l.previewVideo = m.previewVideo;
        changed = true;
        const tile = grid.querySelector(`.lp-tile[data-lora="${cssEsc(l.name)}"]`);
        if (tile) { tile.classList.toggle("loading", l.ready === false); tile.innerHTML = tileInner(l); }
      }
      // A file whose physical name did not match may become a match when its CivitAI name lands. Patching only
      // already-rendered tiles cannot reveal it, so refresh the flat search results without disturbing picked state.
      if (changed && search.trim()) render();
      return changed;
    }
    function cssEsc(s) { return (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/["\\]/g, "\\$&"); }

    // Incompatible LoRAs are hidden unless the toggle is on. (Unknown compatibility — compatible !== false — always shows.)
    const showable = l => showIncompatible || l.compatible !== false;
    // Search both identities: the renderer's physical filename and CivitAI's human name. Metadata arrives
    // asynchronously, so render() is also re-run from applyMeta while a query is active.
    const matchesSearch = (l, q) => l.name.toLowerCase().includes(q)
      || String(l.displayName || "").toLowerCase().includes(q);

    function render() {
      grid.innerHTML = ""; crumb.innerHTML = "";
      const q = search.trim().toLowerCase();

      // Global search: a flat list across the WHOLE tree, regardless of the current folder.
      if (q) {
        crumb.textContent = `Search: “${search}”`;
        const matches = all.filter(l => matchesSearch(l, q) && showable(l));
        matches.forEach(l => grid.appendChild(loraTile(l)));
        if (!matches.length) grid.appendChild(empty("No LoRAs match."));
        return;
      }

      if (folder) {
        const back = document.createElement("button");
        back.type = "button"; back.className = "lp-back"; back.textContent = "↑ " + folder;
        back.addEventListener("click", () => { folder = parentFolder(folder); render(); });
        crumb.appendChild(back);
      } else {
        crumb.textContent = "All LoRAs";
      }

      // Subfolders directly under the current folder.
      const subs = new Set();
      for (const l of all) {
        if (!inFolder(l.folder, folder)) continue;
        const rest = relFolder(l.folder, folder);
        if (rest) subs.add(rest.split("/")[0]);
      }
      [...subs].sort((a, b) => a.localeCompare(b)).forEach(sub => grid.appendChild(folderTile(sub)));

      // Files directly in the current folder.
      const files = all.filter(l => (l.folder || "") === folder && showable(l));
      files.forEach(l => grid.appendChild(loraTile(l)));

      if (!subs.size && !files.length) grid.appendChild(empty(showIncompatible ? "No LoRAs here." : "No compatible LoRAs here."));
    }

    searchInput.addEventListener("input", () => { search = searchInput.value; render(); });

    grid.appendChild(empty("Loading…"));
    try {
      const url = `${GATEWAY}/loras` + (opts.workflow ? `?workflow=${encodeURIComponent(opts.workflow)}` : "");
      const r = await fetch(url);
      if (!r.ok) throw new Error("load failed");
      all = (await r.json()) || [];
    } catch (e) {
      console.error("loraPicker: load failed", e);
      grid.innerHTML = ""; grid.appendChild(empty("Couldn't load LoRAs."));
      return;
    }

    // Incompatible LoRAs are hidden by default; surface the toggle only when there are some to reveal.
    const incompatCount = all.filter(l => l.compatible === false).length;
    if (incompatCount > 0) {
      incompatToggle.hidden = false;
      incompatLbl.textContent = ` Show ${incompatCount} incompatible`;
    }

    // Smart routing: open the top-level folder matching the workflow, when there is one.
    if (opts.defaultFolder) {
      const df = String(opts.defaultFolder).toLowerCase();
      const hit = all.find(l => { const f = (l.folder || "").toLowerCase(); return f === df || f.split("/")[0] === df; });
      if (hit) folder = hit.folder.split("/")[0];
    }
    render();

    // Any file still populating gets polled; each round patches the affected tiles in place (name + preview) without
    // disturbing the current view. Stops itself when the server reports nothing pending.
    const pending = all.filter(l => l.ready === false).map(l => l.name);
    if (pending.length && typeof pollLoraMeta === "function")
      pollCancel = pollLoraMeta(pending, applyMeta);
  };
})();
