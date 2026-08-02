// The LoRA manager (/settings/loras): every LoRA on this box with its cover / CivitAI preview, model name, and
// trigger words. You can redefine the trigger words and choose whether they auto-attach to the prompt. Consumes JSON
// from /forge/loras/manage and saves via /api/lora/settings; the CivitAI master toggle writes the machine setting.
(function () {
  const root = document.getElementById("loraManager");
  if (!root) return;
  const THUMB = (typeof THUMB_W !== "undefined" && THUMB_W) || 220;

  const esc = s => String(s == null ? "" : s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const label = name => String(name || "").split(/[\\/]/).pop().replace(/\.(safetensors|ckpt|pt|gguf)$/i, "");

  async function load() {
    root.innerHTML = '<div class="lm-loading">Loading LoRAs… the first time, trigger words + previews are fetched from CivitAI, which can take a moment.</div>';
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
    root.innerHTML = "";

    const head = document.createElement("div"); head.className = "lm-head";
    const h = document.createElement("h2"); h.textContent = "LoRAs";
    const toggle = document.createElement("label"); toggle.className = "lm-civitai";
    const cb = document.createElement("input"); cb.type = "checkbox"; cb.checked = !!data.civitaiEnabled;
    const span = document.createElement("span"); span.textContent = " Fetch trigger words + previews from CivitAI";
    toggle.append(cb, span);
    cb.addEventListener("change", async () => {
      try {
        await Api.send("/api/machine-settings", "PUT", { key: "Civitai:Enabled", value: cb.checked ? "true" : "false" });
        toast(cb.checked ? "CivitAI lookups on" : "CivitAI lookups off");
        if (cb.checked) load();   // re-fetch now that it's allowed
      } catch (_) { cb.checked = !cb.checked; toast("Couldn't change the setting"); }
    });
    head.append(h, toggle);
    root.appendChild(head);

    const loras = data.loras || [];
    if (!loras.length) {
      const e = document.createElement("div"); e.className = "lm-empty"; e.textContent = "No LoRAs found on this machine.";
      root.appendChild(e); return;
    }

    const list = document.createElement("div"); list.className = "lm-list";
    for (const l of loras) list.appendChild(row(l));
    root.appendChild(list);
  }

  function row(l) {
    const el = document.createElement("div"); el.className = "lm-row";

    const cover = document.createElement("div"); cover.className = "lm-cover";
    if (l.cover)
      cover.innerHTML = `<img src="${GATEWAY}/image/${encodeURIComponent(l.cover)}?w=${THUMB}" alt="" loading="lazy">`;
    else if (l.previewUrl)
      cover.innerHTML = `<img src="${esc(l.previewUrl)}" alt="" loading="lazy" referrerpolicy="no-referrer">`;
    else
      cover.innerHTML = `<div class="lm-noimg">${esc(label(l.name).slice(0, 2).toUpperCase())}</div>`;

    const main = document.createElement("div"); main.className = "lm-main";
    const title = document.createElement("div"); title.className = "lm-name";
    title.textContent = label(l.name);
    title.title = l.name + (l.modelName ? ` — ${l.modelName} (CivitAI)` : "");
    if (l.folder) { const f = document.createElement("span"); f.className = "lm-folder"; f.textContent = " · " + l.folder; title.appendChild(f); }

    const trigRow = document.createElement("div"); trigRow.className = "lm-trigrow";
    const trigLbl = document.createElement("label"); trigLbl.className = "lm-triglbl"; trigLbl.textContent = "Trigger words";
    const trig = document.createElement("input"); trig.type = "text"; trig.className = "lm-trig";
    trig.value = l.triggers || "";
    trig.placeholder = l.defaultTriggers ? l.defaultTriggers + " (CivitAI)" : "none — type words to attach";
    const aa = document.createElement("label"); aa.className = "lm-aa";
    const aacb = document.createElement("input"); aacb.type = "checkbox"; aacb.checked = l.autoAttach !== false;
    aa.append(aacb, document.createTextNode(" auto-attach to prompt"));

    let saveTimer = null;
    const save = () => {
      clearTimeout(saveTimer);
      saveTimer = setTimeout(async () => {
        try { await postLoraSettings(l.name, trig.value, aacb.checked); }
        catch (_) { toast("Couldn't save"); }
      }, 400);
    };
    trig.addEventListener("input", save);
    aacb.addEventListener("change", save);

    trigRow.append(trigLbl, trig, aa);
    main.append(title, trigRow);
    el.append(cover, main);
    return el;
  }

  load();
})();
