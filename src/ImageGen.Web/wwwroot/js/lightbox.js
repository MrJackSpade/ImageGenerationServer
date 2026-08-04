// In-page image lightbox. Clicking any .imgcard opens a fitted modal viewer (image auto-fits the
// viewport, full detail/meta beside it) instead of navigating to /image/{id}, and the ‹ › / arrow
// keys step through every .imgcard on the page it was opened from — stopping at the ends, no wrap-around.
// Falls back to a normal page
// load if the card fragment can't be fetched. Loaded after core.js + detail.js.
//
// WHERE IT IS is an image ID, never a position. The grid changes underneath an open lightbox — gallery.js
// prepends live arrivals on every imagegen:generated, recents.js rebuilds its strip from scratch, a delete
// removes a card — and a remembered index then points at a different image than it did a moment ago. This
// used to hold a snapshot array plus an integer into it: after any prepend, the two paths that re-read the
// DOM (a card that 404s, and onDelete) kept the integer, so stepping jumped back by however many images had
// arrived and re-showed them. The live list IS the list; the id is the only thing that survives it changing.
(function () {
  // Build the overlay shell once, lazily reused for every open.
  const overlay = document.createElement("div");
  overlay.className = "lightbox hidden";
  overlay.innerHTML =
    '<button class="lb-close" type="button" aria-label="Close">×</button>' +
    '<button class="lb-nav lb-prev" type="button" aria-label="Previous image">‹</button>' +
    '<div class="lb-stage"><div class="lb-content"></div></div>' +
    '<button class="lb-nav lb-next" type="button" aria-label="Next image">›</button>';
  document.body.appendChild(overlay);

  const content = overlay.querySelector(".lb-content");
  const prevBtn = overlay.querySelector(".lb-prev");
  const nextBtn = overlay.querySelector(".lb-next");
  const closeBtn = overlay.querySelector(".lb-close");

  let currentId = null;// decoded id of the image currently shown — the ONLY record of where we are
  let returnUrl = null;// where to send the address bar back to on close
  let token = 0;       // guards against out-of-order fetches when cycling fast

  const isOpen = () => !overlay.classList.contains("hidden");
  // href is "/image/<escaped-id>"; keep the escaped id for fetching the fragment.
  const escId = a => { const m = (a.getAttribute("href") || "").match(/^\/image\/([^?#]+)/); return m ? m[1] : null; };
  const idOf = a => { const e = escId(a); return e ? decodeURIComponent(e) : null; };
  // Asked fresh every time. A cached copy is exactly what went stale.
  const liveCards = () => Array.from(document.querySelectorAll("a.imgcard"));
  const positionOf = (id, cards) => cards.findIndex(a => idOf(a) === id);

  async function show(a) {
    if (!a) { close(); return; }
    const eid = escId(a);
    if (!eid) { location.href = a.href; return; }   // unexpected shape — just navigate
    currentId = decodeURIComponent(eid);

    const my = ++token;
    content.innerHTML = '<div class="lb-spin">Loading…</div>';
    paintNav();

    let r;
    try {
      r = await fetch("/image/" + eid + "/card", { credentials: "same-origin" });
    } catch (e) {
      console.error("lightbox card load failed, navigating to the page:", e);
      if (my === token) location.href = a.href;      // network error — fall back to the full page
      return;
    }
    if (my !== token) return;                         // superseded by a newer navigation
    if (!r.ok) {
      // The image is gone (deleted here, on another device, or a stale card): never navigate into a 404.
      // Drop this card and slide to whatever now occupies its slot — the slot as the LIVE list numbers it,
      // taken before the removal, not a position remembered from when the lightbox was opened.
      const before = liveCards();
      const slot = positionOf(currentId, before);
      a.remove();
      slideInto(slot);
      return;
    }
    const html = await r.text();
    if (my !== token) return;                         // superseded while reading the body
    content.innerHTML = html;
    // Fetching the card IS opening the image — the server recorded the view answering this request. Drop the
    // card's unviewed outline now rather than leaving the grid disagreeing with the fact until a reload.
    a.classList.remove("unviewed");
    syncOrientation();                                // stack the meta under wide pictures
    if (window.initDetail) window.initDetail(content, { onDelete, onRegenerate: close });
    history.replaceState({ lb: 1 }, "", "/image/" + eid);
    paintNav();
    preloadNeighbours();
  }

  // Show whatever now sits at `slot` after a card was removed from the live list; the last image if the
  // removed one was last, and close if nothing is left. One rule, used by both removal paths.
  //
  // slot < 0 means the image was not in the live list to begin with — the strip rebuilt without it, or a
  // filter replaced the grid. Closing is the same answer step() gives: there is no neighbour to a thing that
  // is not there, and showing the first card instead would be a guess dressed up as navigation.
  function slideInto(slot) {
    if (slot < 0) { close(); return; }
    const cards = liveCards();
    if (!cards.length) { close(); return; }
    show(cards[Math.min(slot, cards.length - 1)]);
  }

  // The arrows describe the live list around the current image, so a prepend that happens while the modal is
  // open is reflected the next time anything repaints rather than being remembered wrong.
  function paintNav() {
    const cards = liveCards();
    const i = positionOf(currentId, cards);
    const multi = cards.length > 1 && i >= 0;
    prevBtn.classList.toggle("hidden", !multi || i === 0);                 // no ‹ on the first image
    nextBtn.classList.toggle("hidden", !multi || i === cards.length - 1);  // no › on the last image
  }

  // One step through the live list from wherever the current image now sits. No wrap-around: stepping past
  // either end does nothing, as it always has.
  function step(delta) {
    const cards = liveCards();
    const i = positionOf(currentId, cards);
    // The current image has left the list entirely (the recent strip rebuilt and it slid off the window, a
    // filter changed). The same rule as a card that 404s: there is no neighbour to reason about, so close
    // rather than guess at a position in a list this image is not in.
    if (i < 0) { close(); return; }
    const j = i + delta;
    if (j < 0 || j >= cards.length) return;
    show(cards[j]);
  }

  // A landscape picture squeezed beside the fixed-width meta column ends up tiny, so wide media gets
  // the .lb-wide layout instead: picture on top, tags/buttons underneath. The record's aspect gives the
  // answer with no flash; the media's real dimensions (once known) correct it, since a reference-edited
  // or upscaled image can differ from what was asked for.
  function syncOrientation() {
    const card = content.querySelector(".detail-card");
    if (!card) return;
    const m = content.querySelector("#detailImg") || content.querySelector("img,video");
    const w = m ? (m.naturalWidth || m.videoWidth) : 0;
    const h = m ? (m.naturalHeight || m.videoHeight) : 0;
    if (w && h) { card.classList.toggle("lb-wide", w > h); return; }
    let rec = null;
    try { rec = JSON.parse(content.querySelector("#detailRecord").textContent); } catch (e) { console.debug("lightbox: aspect record unreadable:", e); }
    card.classList.toggle("lb-wide", !!rec && rec.aspect === "landscape");
  }
  // load/loadedmetadata don't bubble — capture them. On the container, not the media, so a clip that
  // media.js swaps from <img> to <video> after the fact still re-measures.
  content.addEventListener("load", syncOrientation, true);
  content.addEventListener("loadedmetadata", syncOrientation, true);

  // Warm what stepping actually costs, so ‹ › land on something already fetched.
  //
  // This used to warm the neighbour's THUMBNAIL, which is the picture the grid behind the overlay shows —
  // already loaded, and not what the lightbox displays. Stepping fetches the card fragment and then the
  // FULL image inside it, and neither was warm, so every press paid both round trips.
  //
  // The full image is the same URL as the thumbnail without the ?w= that shrinks it (core.js: thumbUrl is
  // viewUrl + "?w="), so it can be derived from the card already in the DOM without asking the server what
  // to fetch.
  const preloaded = new Set();   // one warm per image; stepping back and forth must not refetch
  function preloadNeighbours() {
    const cards = liveCards();
    const i = positionOf(currentId, cards);
    if (i < 0 || cards.length < 2) return;
    [i - 1, i + 1].forEach(j => {
      if (j < 0 || j >= cards.length) return;          // no wrap-around neighbour at the ends
      const a = cards[j];
      const eid = escId(a);
      if (!eid || preloaded.has(eid)) return;
      preloaded.add(eid);

      // Warm ONLY the picture, never the /card fragment. Fetching /card is how the server records a VIEW
      // (ImageController marks viewed on that GET), so pre-warming the card marked neighbours viewed before you
      // ever stepped to them — corrupting the unviewed outline and the unviewed-only filter. The card is a small
      // fragment, re-fetched on the real step; the picture is the multi-megabyte part worth warming.
      //
      // A neighbour still in the imgqueue has no src yet, so fall back to data-src rather than assigning an empty
      // string — that resolves to the current page URL and fetches the document instead.
      const thumb = a.querySelector("img");
      const url = thumb && (thumb.getAttribute("src") || thumb.getAttribute("data-src"));
      if (url) { const im = new Image(); im.src = url.replace(/[?&]w=\d+/, ""); }
    });
  }

  function open(a) {
    // Must be a real card in the document: stepping reads the live list, so an anchor that is not in it has
    // no neighbours and no position. The caller falls back to a full-page navigation on false.
    if (!a || !idOf(a) || !a.isConnected) return false;
    returnUrl = location.pathname + location.search + location.hash;
    overlay.classList.remove("hidden");
    document.body.style.overflow = "hidden";
    history.pushState({ lb: 1 }, "", location.href);  // an entry to pop on close
    show(a);
    return true;
  }

  // Hide the UI without touching history (popstate already moved us).
  function hide() {
    token++;                                           // cancel any in-flight load
    overlay.classList.add("hidden");
    document.body.style.overflow = "";
    content.innerHTML = "";
    currentId = null;
  }

  // Close via UI: unwind our pushed history entry; popstate does the actual hide.
  function close() {
    if (returnUrl != null && history.state && history.state.lb) history.back();
    else hide();
  }

  // A delete from inside the modal: drop the thumbnail and advance (or close if the page is empty).
  // The card to remove is found by id in the LIVE list — the recent strip rebuilds itself on
  // imagegen:refresh (recents.js), so any node reference we held would be detached, and removing a stale
  // node leaves the just-deleted image in the grid to reload into a 404.
  function onDelete() {
    const cards = liveCards();
    const slot = positionOf(currentId, cards);
    if (slot >= 0) cards[slot].remove();
    toast("Deleted");
    slideInto(slot);   // the slot the deleted card vacated now holds the next image
  }

  prevBtn.addEventListener("click", () => step(-1));
  nextBtn.addEventListener("click", () => step(1));
  closeBtn.addEventListener("click", close);
  document.addEventListener("keydown", e => {
    if (!isOpen()) return;
    if (e.key === "Escape") close();
    else if (e.key === "ArrowLeft") step(-1);
    else if (e.key === "ArrowRight") step(1);
  });
  window.addEventListener("popstate", () => { if (isOpen()) hide(); });

  // Delegated so dynamically-added cards (e.g. the composer's recent strip) are covered too.
  document.addEventListener("click", e => {
    const a = e.target.closest("a.imgcard");
    if (!a) return;
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;  // let modified clicks open the page
    if (!/^\/image\//.test(a.getAttribute("href") || "")) return;   // non-image .imgcard (e.g. artist cards) navigate normally
    if (open(a)) e.preventDefault();
  });

  // Programmatic entry point for images that aren't .imgcard anchors themselves (the composer's result
  // card and chat bubbles): open the lightbox on the matching recent-strip card so a freshly generated
  // image uses the modal — and cycles the recent strip — just like a thumbnail click. Returns false if no
  // matching card is in the DOM yet, so the caller can fall back to a full-page navigation.
  window.openImgcard = (target) => {
    const a = (typeof target === "string")
      ? liveCards().find(el => idOf(el) === target)
      : target;
    return a ? open(a) : false;
  };
})();
