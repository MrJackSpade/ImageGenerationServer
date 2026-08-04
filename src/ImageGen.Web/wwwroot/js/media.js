// Video-clip upgrade. Some library images are short video clips, stored as animated webp. A browser only ANIMATES
// an animated webp inside an <img> — it can't loop it cleanly or treat it as a video. So every <img> that points at
// a gateway image is checked (one batched /forge/media lookup) and, if it's a clip, swapped in place for a muted,
// auto-playing, looping <video> whose source is the on-demand mp4 (/forge/image/{id}/mp4, ?w preserved for thumbs).
//
// A MutationObserver catches dynamically-added cards too (gallery infinite-scroll, the recents strip, the lightbox
// fragment), so every render site — gallery, bookmarks, artist, the detail page and the lightbox — is covered with
// no per-page wiring. The composer renders its own <video> for clip results (it knows the model is a video model),
// so those never appear here as an <img>. Loaded by _Layout after the page scripts (window.GATEWAY is set inline).
(function () {
  if (!window.GATEWAY) return;
  const GW = window.GATEWAY;
  const imgRe = new RegExp("^" + GW.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "/image/([^/?#]+)");

  const verdict = new Map();   // decoded id -> "image"|"webp"|"mp4" (cached; a clip never stops being one)
  const pending = new Map();   // decoded id -> [img, ...] awaiting a verdict
  let flushTimer = null;
  const isClip = k => k === "webp" || k === "mp4";   // /forge/media kinds that upgrade an <img> to a <video>

  // Grid thumbnails carry their URL in data-src until imgqueue.js releases them, so both attributes count here — the
  // clip lookup is independent of the load queue and shouldn't wait for it (nor be blinded by it).
  const srcOf = img => img.getAttribute("src") || img.getAttribute("data-src") || "";

  // The image id an <img> points at (decoded), or null if it isn't a gateway image / is already a clip's poster.
  function idOf(img) {
    const m = srcOf(img).match(imgRe);
    if (!m) return null;
    try { return decodeURIComponent(m[1]); } catch (e) { console.debug("media: id decode failed, using raw:", e); return m[1]; }
  }

  function widthParam(img) {
    const m = srcOf(img).match(/[?&]w=(\d+)/);
    return m ? m[1] : null;
  }

  function upgrade(img, id, kind) {
    if (!img.isConnected) return;
    const enc = encodeURIComponent(id);
    const w = widthParam(img);
    const mp4 = `${GW}/image/${enc}/mp4` + (w ? `?w=${w}` : "");
    const v = document.createElement("video");
    if (img.className) v.className = img.className;
    if (img.id) v.id = img.id;                 // keep #detailImg etc.
    v.loop = true; v.muted = true; v.playsInline = true;
    v.setAttribute("muted", ""); v.setAttribute("playsinline", "");

    if (img.id === "detailImg") {
      // Opened in the big viewer: play immediately, looping, with a scrubber.
      v.controls = true; v.autoplay = true; v.preload = "metadata"; v.src = mp4;
    } else if (kind === "mp4") {
      // An mp4 clip (e.g. MiniMax-H3) has NO server-side still poster — ImageSharp can't decode an mp4, so
      // /image/{id}?still=true 404s and the card would stay blank until hovered. Paint the first frame from the clip
      // itself: load only metadata, then seek to the very start, which decodes+shows frame 0 without playing. Hover
      // plays; leaving pauses back to that first frame.
      v.preload = "metadata";
      v.src = mp4;
      v.addEventListener("loadedmetadata", () => { try { v.currentTime = 0.001; } catch (e) { console.debug("media: seek to first frame failed:", e); } }, { once: true });
      v.addEventListener("mouseenter", () => { const p = v.play(); if (p && p.catch) p.catch(e => console.debug("media: hover play interrupted:", e)); });
      v.addEventListener("mouseleave", () => { v.pause(); try { v.currentTime = 0.001; } catch (e) { console.debug("media: seek to first frame failed:", e); } });
    } else {
      // An animated-webp clip: the cheap server still-frame poster works (ImageSharp reads webp), so keep
      // preload=none + the imgqueue-throttled poster; the clip only loads + plays while hovered.
      v.preload = "none";
      // data-poster, not poster: a grid of clips would otherwise burst every still-frame request at once, which is
      // exactly what imgqueue.js exists to prevent. It assigns the real poster when a slot frees.
      v.setAttribute("data-poster", `${GW}/image/${enc}?` + (w ? `w=${w}&` : "") + "still=true");
      v.src = mp4;
      v.addEventListener("mouseenter", () => { const p = v.play(); if (p && p.catch) p.catch(e => console.debug("media: hover play interrupted:", e)); });
      // The HTML poster only shows until playback starts, so a plain pause would leave the last frame (or nothing).
      // load() resets the element back to its poster without re-downloading the clip (preload=none).
      v.addEventListener("mouseleave", () => { v.pause(); v.load(); });
    }
    img.replaceWith(v);
  }

  function flush() {
    flushTimer = null;
    const ids = Array.from(pending.keys()).filter(id => !verdict.has(id));
    if (!ids.length) { resolvePending(); return; }
    // POST the ids in the body, not the query string. This is one lookup for EVERY image on the page — hundreds of
    // ids — and a GET would stuff them all into the URL, sailing past Kestrel's ~8 KB request line and getting the
    // connection aborted before the server saw it (ERR_CONNECTION_ABORTED), the more so the more thumbnails loaded.
    fetch(`${GW}/media`, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids }),
    })
      .then(r => { if (!r.ok) throw new Error(`POST ${GW}/media -> ${r.status}`); return r.json(); })
      .then(map => {
        // A MISSING key is not a verdict. The endpoint answers for every id it is asked about, so an absent id
        // means the answer was incomplete — recording it as false would assert "not a video" on no evidence.
        for (const id of ids) {
          if (Object.prototype.hasOwnProperty.call(map, id)) verdict.set(id, map[id]);
          else console.error(`media: /media returned no answer for id ${id} — left unresolved, not assumed still`);
        }
      })
      .catch(err => {
        // Deliberately caches NOTHING. `verdict` is permanent for the page, so writing false here would turn one
        // transient failure into "this is not a video" forever — every clip in that batch stuck as a static <img>,
        // no loop, no scrubber, nothing logged. The public edge truncates proxied responses under concurrency, so
        // this path is real, not theoretical. Unresolved ids stay in `pending` and the next batch retries them.
        console.error("media: clip lookup failed — ids left unresolved for retry", err);
      })
      .finally(resolvePending);
  }

  function resolvePending() {
    for (const [id, imgs] of Array.from(pending.entries())) {
      if (!verdict.has(id)) continue;
      const kind = verdict.get(id);
      pending.delete(id);
      if (isClip(kind)) for (const img of imgs) upgrade(img, id, kind);
    }
    // Anything still pending (verdict not yet known) waits for the in-flight batch's resolvePending.
  }

  function consider(img) {
    if (img.tagName !== "IMG" || img.dataset.mediaChecked) return;
    const id = idOf(img);
    if (!id) return;
    img.dataset.mediaChecked = "1";
    if (verdict.has(id)) { const kind = verdict.get(id); if (isClip(kind)) upgrade(img, id, kind); return; }
    const list = pending.get(id);
    if (list) list.push(img); else pending.set(id, [img]);
    if (!flushTimer) flushTimer = setTimeout(flush, 60);   // debounce a burst of cards into one lookup
  }

  function scan(root) {
    if (!root) return;
    if (root.tagName === "IMG") { consider(root); return; }
    if (root.querySelectorAll) root.querySelectorAll("img").forEach(consider);
  }

  new MutationObserver(muts => {
    for (const m of muts) for (const n of m.addedNodes) if (n.nodeType === 1) scan(n);
  }).observe(document.documentElement, { childList: true, subtree: true });

  if (document.readyState === "loading")
    document.addEventListener("DOMContentLoaded", () => scan(document.body));
  else
    scan(document.body);
})();
