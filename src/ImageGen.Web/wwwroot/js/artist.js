//TODO: CHECK FOR FALLBACKS
// Artist page: the user's generations for this artist — server-rendered first page, infinite scroll for the
// rest (/api/history?artist=), and live additions from the composer's `imagegen:generated` event (every gen
// here is locked to this artist). Plus choosing/clearing the artist's display image. Uses core.js.
(function () {
  const root = document.getElementById("artist");
  if (!root) return;
  const artist = root.dataset.artist;
  const gateway = root.dataset.gateway || (window.GATEWAY || "");
  const status = document.getElementById("artistStatus");
  const sentinel = document.getElementById("artistSentinel");
  const hero = document.getElementById("artistHero");
  const note = document.getElementById("artistDisplayNote");
  const clearBtn = document.getElementById("artistClearDisplay");
  const pretty = "@" + artist.replace(/_/g, " ");
  const cssEsc = s => (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/"/g, '\\"');
  const getGrid = () => document.getElementById("artistGrid");

  const seen = new Set();
  (function seedSeen() { const g = getGrid(); if (g) g.querySelectorAll(".gen[data-id]").forEach(el => seen.add(el.dataset.id)); })();

  function cardHtml(r) {
    const id = encodeURIComponent(r.id);
    const prompt = escapeHtml(r.prompt || "");
    return `<div class="gen" data-id="${escapeHtml(r.id)}">`
      + `<a class="imgcard${r.viewed ? "" : " unviewed"}" href="/image/${id}"><div class="img"><img data-src="${gateway}/image/${id}?w=${THUMB_W}" alt="${prompt}"></div>`
      + `<div class="meta"><div class="p">${prompt}</div><div class="row"><span class="tag">${escapeHtml(r.model || "")}</span></div></div></a>`
      + `<button type="button" class="set-display" title="Use as ${escapeHtml(pretty)}'s display image">★</button></div>`;
  }

  // --- display image (hero) ---------------------------------------------------------------------
  function setHero(id) {
    if (!hero) return;
    hero.innerHTML = id ? `<img src="${gateway}/image/${encodeURIComponent(id)}?w=768" alt="${escapeHtml(pretty)}">` : '<div class="artcard-empty">no image yet</div>';
  }
  function markCurrent(id) {
    const grid = getGrid(); if (!grid) return;
    grid.querySelectorAll(".gen.is-display").forEach(g => g.classList.remove("is-display"));
    if (id) { const g = grid.querySelector(`.gen[data-id="${cssEsc(id)}"]`); if (g) g.classList.add("is-display"); }
  }
  // Delegated on root so it also covers cards the composer adds live.
  root.addEventListener("click", async (e) => {
    const b = e.target.closest(".set-display"); if (!b) return;
    e.preventDefault(); e.stopPropagation();
    const gen = b.closest(".gen"); const id = gen && gen.dataset.id; if (!id) return;
    try {
      const r = await postArtistDisplay(artist, id); if (!r.ok) throw 0;
      setHero(id); markCurrent(id);
      if (note) note.textContent = "Showing the image you chose.";
      if (clearBtn) clearBtn.classList.remove("hidden");
      toast("Display image set");
    } catch (_) { toast("Couldn't set display image"); }
  });
  if (clearBtn) clearBtn.addEventListener("click", async () => {
    try {
      const r = await deleteArtistDisplay(artist); if (!r.ok && r.status !== 204) throw 0;
      const first = getGrid() && getGrid().querySelector(".gen");   // latest gen = first card
      const id = first ? first.dataset.id : null;
      setHero(id); markCurrent(null);
      if (note) note.textContent = id ? "Showing your latest generation." : "No images yet.";
      clearBtn.classList.add("hidden");
      toast("Using your latest generation");
    } catch (_) { toast("Couldn't clear"); }
  });

  // --- live: a gen from the composer (locked to this artist) lands at the top of the grid --------
  function ensureGrid() {
    let g = getGrid(); if (g) return g;
    g = document.createElement("div"); g.className = "cardgrid"; g.id = "artistGrid";
    const empty = root.querySelector(".bm-empty");
    if (empty) empty.replaceWith(g);
    else if (sentinel) sentinel.parentNode.insertBefore(g, sentinel);
    else root.appendChild(g);
    return g;
  }
  function addCard(r) {
    if (!r || !r.id || seen.has(r.id)) return;
    seen.add(r.id);
    const t = document.createElement("template"); t.innerHTML = cardHtml(r);
    ensureGrid().insertBefore(t.content, ensureGrid().firstChild);
  }
  // Only THIS artist's images belong here. The page's own composer is locked to this artist, but the
  // cross-device/page liveSync announces every gen the user makes anywhere — so match on the image's marks
  // (name -> tag|artist) before adding, or an unrelated composition would land on the artist grid.
  document.addEventListener("imagegen:generated", e => {
    const rec = e.detail; if (!rec || !rec.id) return;
    if (belongsToArtistPage(rec.marks, artist)) { addCard({ id: rec.id, prompt: rec.prompt, model: rec.model }); return; }
    // Made WITH this artist but blended with another: it belongs to no individual artist page. This page's
    // composer can still produce one (it suppresses '@' autocomplete but doesn't stop a second artist being
    // typed), and a gen started here that then never appears reads as a failure — so say where it went.
    if (carriesArtistMark(rec.marks, artist)) toast("Made with more than one artist — find it in the gallery");
  });

  // --- infinite scroll (mirrors gallery.js) -----------------------------------------------------
  const grid = getGrid();
  if (!grid || !sentinel) return;
  const pageSize = parseInt(root.dataset.pageSize, 10) || 40;
  let total = parseInt(root.dataset.total, 10) || 0;
  let page = parseInt(root.dataset.loadedPage, 10) || 1;
  let loaded = grid.querySelectorAll(".gen").length;
  let loading = false, done = loaded >= total;
  function render() { if (status) status.textContent = done ? (total ? `${total} generation${total === 1 ? "" : "s"}` : "") : "Loading…"; }
  async function loadNext() {
    if (loading || done) return;
    loading = true; render();
    try {
      const d = await queryHistory({ artist, page: page + 1, pageSize });
      page += 1;
      if (typeof d.total === "number") total = d.total;
      const fresh = (d.items || []).filter(it => it && it.id && !seen.has(it.id));
      if (fresh.length) {
        fresh.forEach(it => seen.add(it.id));
        const t = document.createElement("template"); t.innerHTML = fresh.map(cardHtml).join("");
        grid.appendChild(t.content);
      }
      loaded += (d.items || []).length;
      if (!d.items || !d.items.length || loaded >= total) done = true;
    } catch (_) {
    } finally { loading = false; render(); keepFilling(); }
  }
  function keepFilling() { if (done || loading) return; if (sentinel.getBoundingClientRect().top < window.innerHeight + 400) loadNext(); }
  if ("IntersectionObserver" in window) {
    new IntersectionObserver(es => { if (es.some(e => e.isIntersecting)) loadNext(); }, { rootMargin: "400px 0px" }).observe(sentinel);
  } else {
    window.addEventListener("scroll", keepFilling, { passive: true });
  }
  render(); keepFilling();
})();
