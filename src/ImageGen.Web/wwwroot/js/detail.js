// Wires up an image card (image bookmark, token-chip bookmarks, delete, download). Uses core.js.
// Driven both by the standalone /image/{id} page (auto-init below) and by the lightbox, which
// fetches the same card fragment and calls window.initDetail(root, opts). `root` scopes all queries
// so the modal and a page never collide. opts.onDelete / opts.onBookmark let the lightbox react.
function initDetail(root, opts) {
  opts = opts || {};
  const card = root.querySelector(".detail-card");
  if (!card) return;
  // Everything below is wired from this record. Returning silently on a parse failure would leave the card rendered
  // and looking normal with bookmark, delete and download all inert, and nothing anywhere saying why. The controls
  // still cannot be wired, but the failure is made visible and logged.
  let rec;
  try {
    rec = JSON.parse(root.querySelector("#detailRecord").textContent);
  } catch (e) {
    console.error("Image record could not be parsed; this card's controls are disabled:", e);
    card.classList.add("is-broken");
    const note = document.createElement("p");
    note.className = "muted";
    note.textContent = "This image's details couldn't be read, so its controls are unavailable.";
    card.appendChild(note);
    return;
  }
  const id = rec.id;

  // The card ships its creation time as a UTC millisecond epoch in <time data-ts>; render it here so the
  // text is in the browser's zone and locale, never the server's.
  root.querySelectorAll("time[data-ts]").forEach(t => {
    t.textContent = new Date(Number(t.dataset.ts)).toLocaleString(undefined, { dateStyle: "short", timeStyle: "short" });
  });

  // Image bookmark toggle (optimistic; revert on failure).
  const star = root.querySelector("#detailStar");
  let bookmarked = star.classList.contains("on");
  const paintStar = () => { star.classList.toggle("on", bookmarked); star.textContent = bookmarked ? "★" : "☆"; star.title = bookmarked ? "Bookmarked" : "Bookmark"; };
  star.addEventListener("click", async () => {
    bookmarked = !bookmarked; paintStar();
    try {
      if (bookmarked) {
        await postImageBookmark({ ts: rec.ts, prompt: rec.prompt, marks: rec.marks || null, model: rec.model, modelId: rec.modelId, aspect: rec.aspect, id, savedAt: Date.now() });
        toast("★ Image bookmarked");
      } else {
        await deleteImageBookmark(id);
        toast("Removed image bookmark");
      }
      if (opts.onBookmark) opts.onBookmark(id, bookmarked);
    } catch (e) {
      console.error("bookmark save failed:", e);
      bookmarked = !bookmarked; paintStar();
      toast("Couldn't save bookmark");
    }
  });

  // Press-and-hold / right-click the star: file this image into categories (also bookmarks it if it wasn't).
  if (window.attachCategoryLongPress) {
    window.attachCategoryLongPress(star, () => ({
      scope: "image", id,
      record: { ts: rec.ts, id, prompt: rec.prompt, marks: rec.marks || null, model: rec.model, modelId: rec.modelId, aspect: rec.aspect, savedAt: Date.now() },
      onSaved: () => { if (!bookmarked) { bookmarked = true; paintStar(); if (opts.onBookmark) opts.onBookmark(id, true); } },
    }));
  }

  // Tap: this image's prompt in marker form — '#tag, @artist' with underscores restored, random artist/prompt
  // included, i.e. what you'd have to type to get this picture back. rec.markerPrompt is built server-side
  // (PromptMarkers) and is the SAME string Reload re-submits and the Edit page seeds its tag box with.
  //
  // Hold: the ORIGINAL, as typed — before {a|b} was rolled to one option, {{a|b}} was fanned into separate images, an
  // artist page's artist was appended, and the worker added its sampled tags. Both outcomes toast, worded so you
  // know which one you got; that distinction is the whole point of having two.
  const copyBtn = root.querySelector("#detailCopy");
  if (copyBtn) {
    const hold = attachLongPress(copyBtn, async () => {
      const original = (rec.originalPrompt || "").trim();
      // Not recorded — every image made before the column existed, and unbackfillable: the expansion happened in a
      // browser and the pre-expansion text was never sent anywhere. Deliberately NOT falling back to the resolved
      // prompt: that would put a different string on the clipboard than the one asked for, and a plain tap already
      // copies exactly that if it's what you want.
      if (!original) { toast("No original prompt was recorded for this image"); return; }
      toast(await copyText(original) ? "Original prompt copied" : "Couldn't copy the prompt");
    });
    copyBtn.addEventListener("click", async () => {
      if (hold.opened) { hold.opened = false; return; }   // the press was a hold; it has already acted
      const text = (rec.markerPrompt || "").trim();
      if (!text) { toast("No prompt to copy"); return; }
      toast(await copyText(text) ? "Prompt copied" : "Couldn't copy the prompt");
    });
  }

  // Reload/Regenerate: only when the composer is on this page (compose.js exposes attachComposerRegenerate).
  // Click regenerates this image's exact prompt; hold reveals the same count flyout the Generate button has.
  // The composer inputs are left untouched; the lightbox (if any) closes so its progress is visible.
  const reload = root.querySelector("#detailReload");
  if (reload && typeof window.attachComposerRegenerate === "function") {
    reload.hidden = false;
    window.attachComposerRegenerate(reload, rec, opts.onRegenerate);
  }

  // Token chips: tap cycles none -> bookmarked (global) -> banned (this model) -> none. Banning excludes the
  // token from this model's auto-gen; bookmark is global. Banned state is rendered server-side from the DB.
  //
  // A weight variation is NOT a distinct token: data-token is the canonical base key (PromptMarkers.Key strips the
  // weight), so `tag` and `(tag:1.2)` in the same prompt render as two chips carrying the SAME data-token, and the
  // server bookmarks/bans them as one. Their chips are therefore driven as a GROUP keyed by (token, kind): one shared
  // state, and paint() repaints every chip in the group — so favouriting one variation instantly highlights the rest,
  // instead of only the tapped chip until a reopen re-renders them from the DB (#204).
  const modelId = rec.modelId || "";
  const chipGroups = new Map();
  root.querySelectorAll(".tagchip[data-token]").forEach(chip => {
    const gk = chip.dataset.token + "\0" + chip.dataset.kind;
    let group = chipGroups.get(gk);
    if (!group) {
      group = { key: chip.dataset.token, kind: chip.dataset.kind, chips: [],
        state: chip.classList.contains("banned") ? "banned" : (chip.classList.contains("on") ? "bookmarked" : "none") };
      chipGroups.set(gk, group);
    }
    group.chips.push(chip);
  });

  chipGroups.forEach(group => {
    const key = group.key, kind = group.kind, pretty = key.replace(/_/g, " ");
    const paint = () => group.chips.forEach(c => {
      c.classList.toggle("on", group.state === "bookmarked");
      c.classList.toggle("banned", group.state === "banned");
    });
    const cycle = async () => {
      const prev = group.state;
      group.state = prev === "none" ? "bookmarked" : prev === "bookmarked" ? "banned" : "none";
      paint();
      try {
        if (prev === "none") { await postToken(key, kind); toast("★ Bookmarked " + kind + " " + pretty); }
        else if (prev === "bookmarked") { await deleteToken(key, kind); await postBan(modelId, key, kind); toast("⊘ Banned " + kind + " " + pretty + " for this workflow"); }
        else { await deleteBan(modelId, key, kind); toast("Removed ban"); }
      } catch (e) { console.error("save failed:", e); group.state = prev; paint(); toast("Couldn't save"); }
    };

    group.chips.forEach(chip => {
      chip.addEventListener("click", cycle);

      // Press-and-hold / right-click the chip: file this artist/tag into categories (also bookmarks it if it wasn't).
      //
      // Filing a BANNED tag has to lift the ban, the same way the tap cycle's bookmarked -> banned step deletes the
      // bookmark. The gesture says "I want this tag"; the ban says "never use this tag here"; one of them has to go,
      // and it is the one the user did not just perform. Without this the save would bookmark the token, paint() would
      // drop the outline, and deleteBan would never be called — so the tag would stay excluded from this model's auto-gen
      // while the chip claimed otherwise, and a reload would contradict it (banned is rendered from the DB, bookmarked is
      // this chip's own guess). Scoped to THIS model's ban, because that is the only one the chip ever represented.
      if (window.attachCategoryLongPress) {
        window.attachCategoryLongPress(chip, () => ({
          scope: "token", name: key, kind,
          onSaved: async () => {
            const prev = group.state;
            if (prev === "bookmarked") return;             // already what the save just made it
            group.state = "bookmarked"; paint();
            if (prev !== "banned") return;                 // was "none": the save bookmarked it, no ban to lift
            // Same contract as the tap handler: if the call fails, put the chip back rather than leave it
            // asserting a state the server disagrees with.
            try { await deleteBan(modelId, key, kind); }
            catch (e) { console.error("lift ban failed:", e); group.state = prev; paint(); toast("Couldn't lift the ban"); }
          },
        }));
      }
    });
  });

  // Edit is a button now (the whole action row is buttons), so navigate in JS.
  const editBtn = root.querySelector("#detailEdit");
  if (editBtn) editBtn.addEventListener("click", () => { location.href = editBtn.dataset.href; });

  // The stored request is the authoritative per-image record: it includes the resolved seed, every submitted
  // workflow override, prompt/randomization choices, LoRAs and edit/reference inputs. Fetch it lazily because most
  // images are opened without inspecting their values, and old images may predate the request columns entirely.
  const valuesBtn = root.querySelector("#detailValues");
  if (valuesBtn) valuesBtn.addEventListener("click", () => openGenerationValues(id, valuesBtn));

  root.querySelector("#detailDelete").addEventListener("click", async () => {
    if (!confirm("Delete this image from your history?")) return;
    try {
      await deleteHistory(id);
      // Tell the strips to reconcile from history immediately, so the deletion reflects now (and, with the worker as
      // the sole history writer, nothing re-adds it).
      document.dispatchEvent(new CustomEvent("imagegen:refresh"));
      if (opts.onDelete) opts.onDelete(id);
      else location.href = "/gallery";
    } catch (e) { console.error("delete failed:", e); toast("Delete failed"); }
  });

  // Save: video-aware (an H3/webp clip downloads its mp4, a still its own bytes). saveMedia (core.js) resolves the
  // clip kind through /media — the detail card has no model in hand — and force-downloads via a blob, since a bare
  // <a download> can't name a cross-origin gateway file.
  root.querySelector("#detailDownload").addEventListener("click", (e) => { e.preventDefault(); saveMedia(id); });

  // The LoRAs this image was generated with, shown as their own chips BEFORE the tag chips (distinct cyan). Display
  // only — assigning one as its cover is done through the unified portrait button, alongside artists and tags.
  const promptEl = root.querySelector("#detailPrompt");
  if (promptEl && Array.isArray(rec.loras) && rec.loras.length) {
    const frag = document.createDocumentFragment();
    rec.loras.forEach(l => {
      const chip = document.createElement("span"); chip.className = "lora-chip"; chip.title = l.name;
      chip.innerHTML = `<span class="lc-name">${escapeHtml(loraBase(l.name))}</span><span class="lc-weight">×${escapeHtml(String(l.weight))}</span>`;
      frag.appendChild(chip);
    });
    promptEl.insertBefore(frag, promptEl.firstChild);
  }

  // One portrait button for the whole image: choose whether this picture represents one of its artists, tags, or
  // LoRAs. A category with nothing in it is omitted; one with a single member assigns straight through; one with
  // several opens a second list to pick which.
  const portraitBtn = root.querySelector("#detailPortrait");
  if (portraitBtn) {
    const marks = rec.marks || {};
    const artists = Object.keys(marks).filter(k => marks[k] === "artist");
    const tags = Object.keys(marks).filter(k => marks[k] !== "artist");
    const loras = (Array.isArray(rec.loras) ? rec.loras : []).map(l => l.name);
    const groups = [
      { kind: "artist", noun: "Artist", items: artists, label: n => "@" + n.replace(/_/g, " "), assign: n => postArtistDisplay(n, id) },
      { kind: "tag", noun: "Tag", items: tags, label: n => n.replace(/_/g, " "), assign: n => postTagDisplay(n, id) },
      { kind: "lora", noun: "LoRA", items: loras, label: n => loraBase(n), assign: n => postLoraDisplay(n, id) },
    ].filter(g => g.items.length);
    if (groups.length) {
      portraitBtn.hidden = false;
      portraitBtn.addEventListener("click", () => openPortraitPicker(groups));
    }
  }
}

function loraBase(name) {
  return String(name || "").split(/[\\/]/).pop().replace(/\.(safetensors|ckpt|pt|gguf)$/i, "");
}

// ---- generation-values modal (one shared modal, reused by every card) ----------------------------
// Values are rendered with textContent all the way down: prompts, filenames and arbitrary override keys are data,
// never markup. Objects and arrays are expanded recursively so no nested request value disappears behind "[object]".
let gvModal = null, gvBody = null, gvCopy = null, gvRequest = null, gvRequestJson = null, gvReturnFocus = null, gvToken = 0;

const generationValueLabels = {
  kind: "Kind", workflow: "Workflow", prompt: "Prompt", negativePrompt: "Negative Prompt", aspect: "Aspect",
  randomArtist: "Random Artist", randomPrompt: "Random Prompt", temperature: "Temperature", tagTypes: "Tag Types",
  overrides: "Workflow Parameters", loras: "LoRAs", sourceImageId: "Source Image ID", maskImageId: "Mask Image ID",
  lastFrameImageId: "Last Frame Image ID", referenceIds: "Reference Image IDs",
};

function generationValueLabel(key) {
  if (generationValueLabels[key]) return generationValueLabels[key];
  const words = String(key)
    .replace(/[_-]+/g, " ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Za-z])(\d)/g, "$1 $2")
    .replace(/(\d)([A-Za-z])/g, "$1 $2")
    .trim();
  if (!words) return "Value";
  const acronyms = { cfg: "CFG", fps: "FPS", id: "ID", ids: "IDs", lora: "LoRA", loras: "LoRAs", vae: "VAE" };
  return words.split(/\s+/).map(word => acronyms[word.toLowerCase()] || word[0].toUpperCase() + word.slice(1)).join(" ");
}

function generationScalar(value) {
  if (value === null || value === undefined) return { text: "null", empty: true };
  if (value === "") return { text: '\"\"', empty: false };
  return { text: String(value), empty: false };
}

function appendGenerationValue(parent, label, value) {
  if (Array.isArray(value)) {
    if (!value.length) { appendGenerationValue(parent, label, "[]"); return; }
    const group = document.createElement("section"); group.className = "generation-values-group";
    const heading = document.createElement("h4"); heading.textContent = label; group.appendChild(heading);
    const contents = document.createElement("div"); contents.className = "generation-values-nested";
    value.forEach((item, i) => appendGenerationValue(contents, String(i + 1), item));
    group.appendChild(contents); parent.appendChild(group); return;
  }

  if (value && typeof value === "object") {
    const entries = Object.entries(value);
    if (!entries.length) { appendGenerationValue(parent, label, "{}"); return; }
    const group = document.createElement("section"); group.className = "generation-values-group";
    const heading = document.createElement("h4"); heading.textContent = label; group.appendChild(heading);
    const contents = document.createElement("div"); contents.className = "generation-values-nested";
    entries.forEach(([key, nested]) => appendGenerationValue(contents, generationValueLabel(key), nested));
    group.appendChild(contents); parent.appendChild(group); return;
  }

  const row = document.createElement("div"); row.className = "generation-value-row";
  const name = document.createElement("div"); name.className = "generation-value-name"; name.textContent = label;
  const shown = generationScalar(value);
  const val = document.createElement("div"); val.className = "generation-value-value" + (shown.empty ? " is-empty" : "");
  val.textContent = shown.text;
  row.append(name, val); parent.appendChild(row);
}

function ensureGenerationValuesModal() {
  if (gvModal) return;
  gvModal = document.createElement("div");
  gvModal.className = "modal-overlay generation-values-modal hidden";
  gvModal.setAttribute("role", "dialog");
  gvModal.setAttribute("aria-modal", "true");
  gvModal.setAttribute("aria-labelledby", "generationValuesTitle");
  gvModal.innerHTML =
    '<div class="modal-card generation-values-card"><div class="generation-values-head">'
    + '<h3 id="generationValuesTitle">Generation values</h3>'
    + '<button type="button" class="generation-values-close" data-gv="close" aria-label="Close">×</button></div>'
    + '<div class="generation-values-body"></div>'
    + '<div class="modal-actions"><button type="button" class="link-btn" data-gv="copy" disabled>Copy JSON</button>'
    + '<button type="button" class="settings-btn" data-gv="done">Done</button></div></div>';
  document.body.appendChild(gvModal);
  gvBody = gvModal.querySelector(".generation-values-body");
  gvCopy = gvModal.querySelector('[data-gv="copy"]');

  gvModal.addEventListener("click", async e => {
    if (e.target === gvModal) { closeGenerationValues(); return; }
    const button = e.target.closest("button[data-gv]");
    if (!button) return;
    if (button.dataset.gv === "close" || button.dataset.gv === "done") { closeGenerationValues(); return; }
    if (button.dataset.gv === "copy" && gvRequestJson) {
      toast(await copyText(gvRequestJson) ? "Generation values copied" : "Couldn't copy generation values");
    }
  });

  // Capture beats the lightbox's document-level Escape handler: Escape closes this top modal, not both layers.
  document.addEventListener("keydown", e => {
    if (e.key !== "Escape" || !gvModal || gvModal.classList.contains("hidden")) return;
    e.preventDefault(); e.stopImmediatePropagation(); closeGenerationValues();
  }, true);
}

function generationValuesMessage(text, error) {
  gvBody.innerHTML = "";
  const p = document.createElement("p");
  p.className = "generation-values-message" + (error ? " is-error" : "");
  p.textContent = text; gvBody.appendChild(p);
}

function closeGenerationValues() {
  if (!gvModal || gvModal.classList.contains("hidden")) return;
  gvToken++;
  gvModal.classList.add("hidden");
  const target = gvReturnFocus;
  gvReturnFocus = null;
  if (target && target.isConnected) target.focus();
}

async function openGenerationValues(id, returnFocus) {
  ensureGenerationValuesModal();
  const token = ++gvToken;
  gvRequest = null; gvRequestJson = null; gvReturnFocus = returnFocus; gvCopy.disabled = true;
  generationValuesMessage("Loading generation values…", false);
  gvModal.classList.remove("hidden");
  gvModal.querySelector('[data-gv="close"]').focus();

  try {
    const response = await fetch(`/forge/image/${encodeURIComponent(id)}/params`, { credentials: "same-origin" });
    if (token !== gvToken) return;
    if (response.status === 404) {
      generationValuesMessage("Generation values were not recorded for this image.", false);
      return;
    }
    if (!response.ok) throw new Error(`generation values request failed (${response.status})`);
    const raw = await response.text();
    const request = JSON.parse(generationJsonForDisplay(raw));
    if (token !== gvToken) return;
    gvRequest = request; gvRequestJson = raw; gvBody.innerHTML = "";
    Object.entries(request).forEach(([key, value]) => appendGenerationValue(gvBody, generationValueLabel(key), value));
    gvCopy.disabled = false;
  } catch (e) {
    if (token !== gvToken) return;
    console.error("generation values load failed:", e);
    generationValuesMessage("Generation values couldn't be loaded.", true);
  }
}

// JSON.parse rounds an API-supplied 64-bit seed before the UI ever sees it. Quote only unsafe integer TOKENS in a
// display copy of the document (never digits inside strings), while keeping the untouched response for Copy JSON.
function generationJsonForDisplay(raw) {
  let output = "", inString = false, escaped = false;
  for (let i = 0; i < raw.length;) {
    const ch = raw[i];
    if (inString) {
      output += ch; i++;
      if (escaped) escaped = false;
      else if (ch === "\\") escaped = true;
      else if (ch === '\"') inString = false;
      continue;
    }
    if (ch === '\"') { inString = true; output += ch; i++; continue; }
    if (ch === "-" || (ch >= "0" && ch <= "9")) {
      const match = raw.slice(i).match(/^-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/);
      if (match) {
        const token = match[0];
        let unsafe = false;
        if (/^-?\d+$/.test(token)) {
          try { unsafe = BigInt(token) > BigInt(Number.MAX_SAFE_INTEGER) || BigInt(token) < BigInt(Number.MIN_SAFE_INTEGER); }
          catch (_) { unsafe = false; }
        }
        output += unsafe ? JSON.stringify(token) : token;
        i += token.length; continue;
      }
    }
    output += ch; i++;
  }
  return output;
}

window.closeGenerationValuesModal = closeGenerationValues;

// ---- unified portrait picker (one shared modal, reused by every card) -----------------------------
// Level 1 lists the categories that have members — a lone member shows its own name; several show "Artist…/Tag…/
// LoRA…". Clicking a "…" row swaps in level 2: that category's members in a scrollbox. Assigning posts the matching
// display-image endpoint (artist/tag/lora) and closes.
let ppModal = null, ppTitle = null, ppList = null, ppBack = null, ppGroups = null;

function ensurePortraitModal() {
  if (ppModal) return;
  ppModal = document.createElement("div");
  ppModal.className = "modal-overlay hidden";
  ppModal.innerHTML =
    '<div class="modal-card portrait-card"><h3>Assign as portrait:</h3>'
    + '<div class="portrait-list"></div>'
    + '<div class="modal-actions"><button type="button" class="link-btn" data-pp="back" hidden>‹ Back</button>'
    + '<button type="button" class="link-btn" data-pp="cancel">Cancel</button></div></div>';
  document.body.appendChild(ppModal);
  ppTitle = ppModal.querySelector("h3");
  ppList = ppModal.querySelector(".portrait-list");
  ppBack = ppModal.querySelector('[data-pp="back"]');
  ppModal.addEventListener("click", e => {
    if (e.target === ppModal) { closePortraitModal(); return; }
    const b = e.target.closest("button[data-pp]"); if (!b) return;
    if (b.dataset.pp === "cancel") closePortraitModal();
    else if (b.dataset.pp === "back") renderPortraitCategories();
  });
  document.addEventListener("keydown", e => {
    if (e.key === "Escape" && ppModal && !ppModal.classList.contains("hidden")) closePortraitModal();
  });
}

function closePortraitModal() { if (ppModal) ppModal.classList.add("hidden"); }

function portraitRow(text, cls, onClick) {
  const b = document.createElement("button");
  b.type = "button"; b.className = "portrait-row" + (cls ? " " + cls : "");
  b.textContent = text;
  b.addEventListener("click", onClick);
  return b;
}

function renderPortraitCategories() {
  ppTitle.textContent = "Assign as portrait:";
  ppBack.hidden = true;
  ppList.innerHTML = "";
  ppGroups.forEach(g => {
    if (g.items.length === 1) {
      ppList.appendChild(portraitRow(g.label(g.items[0]), "pr-" + g.kind, () => assignPortrait(g, g.items[0])));
    } else {
      ppList.appendChild(portraitRow(g.noun + "…", "pr-" + g.kind + " pr-more", () => renderPortraitMembers(g)));
    }
  });
}

function renderPortraitMembers(g) {
  ppTitle.textContent = "Assign as " + g.noun.toLowerCase() + " portrait:";
  ppBack.hidden = false;
  ppList.innerHTML = "";
  g.items.forEach(n => ppList.appendChild(portraitRow(g.label(n), "pr-" + g.kind, () => assignPortrait(g, n))));
}

async function assignPortrait(g, name) {
  try {
    const r = await g.assign(name);
    if (!r || !r.ok) throw 0;
    toast("Portrait set — " + g.label(name));
    closePortraitModal();
  } catch (e) { console.error("set portrait failed:", e); toast("Couldn't set portrait"); }
}

function openPortraitPicker(groups) {
  ensurePortraitModal();
  ppGroups = groups;
  renderPortraitCategories();
  ppModal.classList.remove("hidden");
}

window.initDetail = initDetail;
// Standalone detail page: bind immediately.
if (document.querySelector(".detail-stage")) initDetail(document, {});
