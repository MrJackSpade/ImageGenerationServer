// Wires up an image card (image bookmark, token-chip bookmarks, delete, download). Uses core.js.
// Driven both by the standalone /image/{id} page (auto-init below) and by the lightbox, which
// fetches the same card fragment and calls window.initDetail(root, opts). `root` scopes all queries
// so the modal and a page never collide. opts.onDelete / opts.onBookmark let the lightbox react.
function initDetail(root, opts) {
  opts = opts || {};
  const card = root.querySelector(".detail-card");
  if (!card) return;
  // Everything below is wired from this record. Returning silently on a parse failure — which is what this did —
  // left the card rendered and looking normal with bookmark, delete and download all inert, and nothing anywhere
  // said why. The controls still cannot be wired, but the failure is now visible and logged.
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
    } catch (_) {
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
  // Hold: the ORIGINAL, as typed — before [a|b] was rolled to one option, {a|b} was fanned into separate images, an
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
  const modelId = rec.modelId || "";
  root.querySelectorAll(".tagchip[data-token]").forEach(chip => {
    const key = chip.dataset.token, kind = chip.dataset.kind, pretty = key.replace(/_/g, " ");
    let state = chip.classList.contains("banned") ? "banned" : (chip.classList.contains("on") ? "bookmarked" : "none");
    const paint = () => { chip.classList.toggle("on", state === "bookmarked"); chip.classList.toggle("banned", state === "banned"); };
    chip.addEventListener("click", async () => {
      const prev = state;
      const next = state === "none" ? "bookmarked" : state === "bookmarked" ? "banned" : "none";
      state = next; paint();
      try {
        if (prev === "none") { await postToken(key, kind); toast("★ Bookmarked " + kind + " " + pretty); }
        else if (prev === "bookmarked") { await deleteToken(key, kind); await postBan(modelId, key, kind); toast("⊘ Banned " + kind + " " + pretty + " for this workflow"); }
        else { await deleteBan(modelId, key, kind); toast("Removed ban"); }
      } catch (_) { state = prev; paint(); toast("Couldn't save"); }
    });

    // Press-and-hold / right-click the chip: file this artist/tag into categories (also bookmarks it if it wasn't).
    //
    // Filing a BANNED tag has to lift the ban, the same way the tap cycle's bookmarked -> banned step deletes the
    // bookmark. The gesture says "I want this tag"; the ban says "never use this tag here"; one of them has to go,
    // and it is the one the user did not just perform. Without this the save bookmarked the token, paint() dropped
    // the outline, and deleteBan was never called — so the tag stayed excluded from this model's auto-gen while the
    // chip claimed otherwise, and a reload contradicted it (banned is rendered from the DB, bookmarked was this
    // chip's own guess). Scoped to THIS model's ban, because that is the only one the chip ever represented.
    if (window.attachCategoryLongPress) {
      window.attachCategoryLongPress(chip, () => ({
        scope: "token", name: key, kind,
        onSaved: async () => {
          const prev = state;
          if (prev === "bookmarked") return;             // already what the save just made it
          state = "bookmarked"; paint();
          if (prev !== "banned") return;                 // was "none": the save bookmarked it, no ban to lift
          // Same contract as the tap handler: if the call fails, put the chip back rather than leave it
          // asserting a state the server disagrees with.
          try { await deleteBan(modelId, key, kind); }
          catch (_) { state = prev; paint(); toast("Couldn't lift the ban"); }
        },
      }));
    }
  });

  // Edit is a button now (the whole action row is buttons), so navigate in JS.
  const editBtn = root.querySelector("#detailEdit");
  if (editBtn) editBtn.addEventListener("click", () => { location.href = editBtn.dataset.href; });

  root.querySelector("#detailDelete").addEventListener("click", async () => {
    if (!confirm("Delete this image from your history?")) return;
    try {
      await deleteHistory(id);
      // Tell the strips to reconcile from history immediately, so the deletion reflects now (and, with the worker as
      // the sole history writer, nothing re-adds it).
      document.dispatchEvent(new CustomEvent("imagegen:refresh"));
      if (opts.onDelete) opts.onDelete(id);
      else location.href = "/gallery";
    } catch (_) { toast("Delete failed"); }
  });

  // Force a real download even though the image is cross-origin (the gateway): fetch -> blob -> anchor.
  // cache:"no-store" is essential: the <img> preview already loaded this URL as a no-CORS request (no
  // Origin -> no Access-Control-Allow-Origin on the cached response, and the gateway sends no Vary:Origin),
  // so a plain cors fetch would reuse that ACAO-less cache entry and be blocked. no-store forces a fresh
  // request that carries Origin and gets ACAO:* back.
  root.querySelector("#detailDownload").addEventListener("click", async (e) => {
    e.preventDefault();
    try {
      const res = await fetch(viewUrl(id), { cache: "no-store" }); const blob = await res.blob();
      const u = URL.createObjectURL(blob); const a = document.createElement("a");
      a.href = u; a.download = /\.\w+$/.test(id) ? id : (id || "picture") + ".png";
      document.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(u), 1000);
    } catch (_) { window.open(viewUrl(id), "_blank"); }
  });

  // "Set as display image" for each artist this picture used — so it can represent the artist on the
  // bookmarks/artist pages without leaving the viewer.
  const actions = root.querySelector(".detail-actions");
  const artistMarks = Object.keys(rec.marks || {}).filter(k => rec.marks[k] === "artist");
  if (actions && artistMarks.length) {
    artistMarks.forEach(name => {
      const b = document.createElement("button");
      b.type = "button"; b.className = "icon-btn"; b.textContent = "🖼";
      b.title = "Set as @" + name.replace(/_/g, " ") + "'s display image";
      b.setAttribute("aria-label", b.title);
      b.addEventListener("click", async () => {
        try { const r = await postArtistDisplay(name, id); if (!r.ok) throw 0; toast("Display image set for @" + name.replace(/_/g, " ")); }
        catch (_) { toast("Couldn't set display image"); }
      });
      actions.appendChild(b);
    });
  }

  // The LoRAs this image was generated with: name + strength, each with a "set as display image" button that makes
  // the current picture that LoRA's cover in the composer's picker. One image can be a cover for several LoRAs.
  const meta = root.querySelector(".detail-meta");
  if (meta && Array.isArray(rec.loras) && rec.loras.length && typeof window.postLoraDisplay === "function") {
    const box = document.createElement("div"); box.className = "detail-loras";
    const head = document.createElement("div"); head.className = "detail-loras-head"; head.textContent = "LoRAs";
    box.appendChild(head);
    rec.loras.forEach(l => {
      const row = document.createElement("div"); row.className = "detail-lora";
      const nm = document.createElement("span"); nm.className = "detail-lora-name";
      nm.textContent = String(l.name || "").split(/[\\/]/).pop().replace(/\.(safetensors|ckpt|pt|gguf)$/i, "");
      nm.title = l.name;
      const wt = document.createElement("span"); wt.className = "detail-lora-weight"; wt.textContent = "×" + l.weight;
      const set = document.createElement("button");
      set.type = "button"; set.className = "icon-btn detail-lora-set"; set.textContent = "🖼";
      set.title = "Set as this LoRA's display image";
      set.setAttribute("aria-label", set.title);
      set.addEventListener("click", async () => {
        try { const r = await postLoraDisplay(l.name, id); if (!r.ok) throw 0; toast("Display image set for " + nm.textContent); }
        catch (_) { toast("Couldn't set display image"); }
      });
      row.append(nm, wt, set);
      box.appendChild(row);
    });
    meta.appendChild(box);
  }
}

window.initDetail = initDetail;
// Standalone detail page: bind immediately.
if (document.querySelector(".detail-stage")) initDetail(document, {});
