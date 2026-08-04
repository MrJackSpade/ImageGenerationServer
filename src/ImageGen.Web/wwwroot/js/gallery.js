// History page: filters + infinite scroll. The first page is server-rendered; as the sentinel nears the viewport we
// fetch the next /api/history page and append cards built to match the server markup, so the lightbox
// (delegated on a.imgcard) and the grid styles apply unchanged. Uses core.js (GATEWAY, escapeHtml, Api).
//
// Both filters (the prompt search and the workflow select) are applied SERVER-side, not over what's already loaded:
// they go to /api/history as ?search= and ?workflow=, so a match on page 40 is found without scrolling to it, and
// the count in the footer is the count of matches. Changing either therefore restarts paging from page 1 with a
// fresh grid.
(function () {
  const root = document.getElementById("gallery");
  if (!root) return;
  const grid = root.querySelector(".cardgrid");
  const sentinel = document.getElementById("gallerySentinel");
  const status = document.getElementById("galleryStatus");
  const empty = document.getElementById("galleryEmpty");
  const box = document.getElementById("gallerySearch");
  const wfSelect = document.getElementById("galleryWorkflow");
  const unviewedBox = document.getElementById("galleryUnviewed");
  const form = (box && box.form) || (wfSelect && wfSelect.form) || (unviewedBox && unviewedBox.form);

  const pageSize = parseInt(root.dataset.pageSize, 10) || 40;
  let total = parseInt(root.dataset.total, 10) || 0;
  let page = parseInt(root.dataset.loadedPage, 10) || 1;   // highest page already in the DOM
  let loaded = grid.querySelectorAll("a.imgcard").length;
  let loading = false, done = loaded >= total;
  let search = root.dataset.search || "";
  let workflow = root.dataset.workflow || "";
  // Server-side like the other two: filtering an already-paged result would give short pages, a wrong total, and a
  // scroll that stalls whenever a page happens to be entirely viewed.
  let unviewed = !!root.dataset.unviewed;
  // Bumped on every filter change; a fetch whose token is stale belongs to a query the user has already replaced, so
  // its rows must not land in the grid (typing fast otherwise interleaves two result sets).
  let seq = 0;

  const fmtDate = ts => { try { return new Date(ts).toLocaleDateString(undefined, { month: "short", day: "numeric" }); } catch (e) { console.debug("date format failed:", e); return ""; } };
  // Cards carry the UTC millisecond epoch in <time data-ts>; the text is always written here, in the
  // browser's zone — the server never bakes in its own local time.
  const fillDates = () => grid.querySelectorAll("time[data-ts]").forEach(t => { if (!t.textContent) t.textContent = fmtDate(Number(t.dataset.ts)); });

  function cardHtml(r) {
    const id = encodeURIComponent(r.id);
    const prompt = escapeHtml(r.prompt || "");
    // Outlined until opened, exactly as on the Recent strip — one meaning, both grids.
    return `<a class="imgcard${r.viewed ? "" : " unviewed"}" href="/image/${id}">`
      + `<div class="img"><img data-src="${GATEWAY}/image/${id}?w=${THUMB_W}" alt="${prompt}"></div>`
      + `<div class="meta"><div class="p">${prompt}</div>`
      + `<div class="row"><span class="tag">${escapeHtml(r.model || "")}</span><span class="seed"><time data-ts="${Number(r.ts) || 0}"></time></span></div></div>`
      + `</a>`;
  }

  // The selected workflow's display name (without the count), for the empty-state message.
  function workflowName() {
    const opt = wfSelect && wfSelect.selectedOptions[0];
    return (opt && opt.dataset.name) || "";
  }

  function render() {
    status.textContent = done ? (total ? `${total} image${total === 1 ? "" : "s"}` : "") : "Loading…";
    const showEmpty = done && total === 0;
    empty.classList.toggle("hidden", !showEmpty);
    if (!showEmpty) return;
    // Say which filter came up empty — with any of them on, "no images yet" would read as though the history were
    // empty. The unviewed filter earns its own wording: emptying it is the NORMAL end state (you opened everything,
    // or you just pressed Mark all viewed), not a search that found nothing.
    const wf = workflow ? `<b>${escapeHtml(workflowName() || workflow)}</b>` : "";
    if (unviewed && (search || workflow)) empty.innerHTML = "Nothing unviewed matches those filters.";
    else if (unviewed) empty.innerHTML = "Nothing unviewed — you've opened everything.";
    else if (search && workflow) empty.innerHTML = `Nothing from ${wf} has every word in <b>${escapeHtml(search)}</b>.`;
    else if (search) empty.innerHTML = `No image's prompt has every word in <b>${escapeHtml(search)}</b>.`;
    else if (workflow) empty.innerHTML = `No images from ${wf} yet.`;
    else empty.innerHTML = 'No images yet. <a href="/">Make one →</a>';
  }

  async function loadNext() {
    if (loading || done) return;
    const mine = seq;
    loading = true; render();
    try {
      const d = await queryHistory({ page: page + 1, pageSize, search: search || null, workflow: workflow || null, unviewedOnly: unviewed });
      if (mine !== seq) return;                 // a newer filter replaced this query mid-flight
      page += 1;
      if (typeof d.total === "number") total = d.total;
      if (d.items && d.items.length) {
        const tpl = document.createElement("template");
        tpl.innerHTML = d.items.map(cardHtml).join("");
        grid.appendChild(tpl.content);
        fillDates();
        syncMarkAll();   // a newly-loaded page can be the first thing on screen carrying an outline
        loaded += d.items.length;
      }
      if (!d.items || !d.items.length || loaded >= total) done = true;
    } catch (e) {
      console.debug("gallery: page load failed (will retry):", e);
      // Transient (network/500): stop the spinner but leave `done` false so scrolling can retry later.
    } finally {
      if (mine === seq) {
        loading = false; render();
        keepFillingIfVisible();
      }
    }
  }

  // New images appear as they finish, like every other page.
  //
  // A generation announces itself with `imagegen:generated`, and the live cross-device tracker fires
  // `imagegen:refresh` when a job finalizes. artist.js, compose.js, edit.js and recents.js all treat those as
  // triggers to re-pull; the gallery listened to neither, so this was the one page where a batch you were
  // watching never showed up and you had to reload to see your own images.
  //
  // The events are only a SIGNAL that something changed. What to show comes from re-asking the server for
  // page 1 — accumulating ids this tab happened to witness is what previously made a reload disagree with a
  // tab that watched the batch run (recents.js carries the same note). Thumbnails only: nothing opens,
  // nothing expands, and someone scrolled into last month does not get the view moved under them.
  let refreshing = false;
  async function refreshNewest() {
    if (refreshing) return;                       // one in flight is enough; the next event re-checks anyway
    refreshing = true;
    const mine = seq;
    try {
      const d = await queryHistory({ page: 1, pageSize, search: search || null, workflow: workflow || null, unviewedOnly: unviewed });
      if (mine !== seq) return;                   // a filter changed while this was in the air
      if (typeof d.total === "number") total = d.total;
      if (!d.items || !d.items.length) return;

      // Prepend only what is not already on screen, so scroll position and the loaded pages below survive.
      const seen = new Set(Array.from(grid.querySelectorAll("a.imgcard"))
        .map(a => decodeURIComponent((a.getAttribute("href") || "").replace("/image/", "")))); 
      const fresh = d.items.filter(r => !seen.has(String(r.id)));
      if (!fresh.length) return;

      const tpl = document.createElement("template");
      tpl.innerHTML = fresh.map(cardHtml).join("");
      grid.prepend(tpl.content);
      loaded += fresh.length;
      fillDates();
      syncMarkAll();
      // imgqueue.js watches the document with a MutationObserver, so the prepended cards are claimed for
      // lazy loading without being told.
      render();
    } catch (e) {
      console.debug("gallery: refresh failed (will retry):", e);
      // Transient: the next generation event tries again.
    } finally {
      refreshing = false;
    }
  }
  // The emitters dispatch these on `document` with bubbles:false (compose.js, edit.js, detail.js, multiselect.js),
  // so a `window` listener never sees them — /gallery sat dead while every other page updated live. Listen on the
  // element the event is actually dispatched on, exactly as artist.js and recents.js do.
  document.addEventListener("imagegen:generated", refreshNewest);
  document.addEventListener("imagegen:refresh", refreshNewest);

  // Start over on a new filter: empty the grid, rewind to "no pages loaded", and pull page 1 for the new query.
  function applyFilters() {
    const nextSearch = box ? box.value.trim() : search;
    const nextWorkflow = wfSelect ? wfSelect.value : workflow;
    const nextUnviewed = unviewedBox ? unviewedBox.checked : unviewed;
    if (nextSearch === search && nextWorkflow === workflow && nextUnviewed === unviewed) return;   // what's on screen already answers this
    search = nextSearch; workflow = nextWorkflow; unviewed = nextUnviewed;
    const url = new URL(location.href);
    if (search) url.searchParams.set("q", search); else url.searchParams.delete("q");
    if (workflow) url.searchParams.set("workflow", workflow); else url.searchParams.delete("workflow");
    if (unviewed) url.searchParams.set("unviewed", "true"); else url.searchParams.delete("unviewed");
    history.replaceState(null, "", url);        // reload/bookmark keeps the filters; no extra back-button steps
    restart();
  }

  // Throw away what's on screen and pull page 1 for the current query. Bumping seq is what makes any fetch already
  // in the air belong to a query the user has replaced, so its rows cannot land in the new grid.
  function restart() {
    seq += 1;
    grid.innerHTML = "";
    loaded = 0; page = 0; total = 0; done = false; loading = false;
    loadNext();
  }

  // The observer only fires on intersection *changes*; if the sentinel stays in view after appending (tall
  // viewport, short page), nudge another load so we don't stall waiting for a scroll that never comes.
  function keepFillingIfVisible() {
    if (done || loading) return;
    if (sentinel.getBoundingClientRect().top < window.innerHeight + 400) loadNext();
  }

  if ("IntersectionObserver" in window) {
    new IntersectionObserver(es => { if (es.some(e => e.isIntersecting)) loadNext(); }, { rootMargin: "400px 0px" }).observe(sentinel);
  } else {
    window.addEventListener("scroll", keepFillingIfVisible, { passive: true });
  }

  let typingTimer = null;
  const filterNow = () => { clearTimeout(typingTimer); applyFilters(); };
  if (box) {
    box.addEventListener("input", () => {
      clearTimeout(typingTimer);
      typingTimer = setTimeout(applyFilters, 250);   // settle between keystrokes, then one round trip
    });
    box.addEventListener("search", filterNow);       // the clear "×"
  }
  if (wfSelect) wfSelect.addEventListener("change", filterNow);
  if (unviewedBox) unviewedBox.addEventListener("change", filterNow);
  // Enter shouldn't reload the page when JS is driving — filter in place instead.
  if (form) form.addEventListener("submit", e => { e.preventDefault(); filterNow(); });

  // "Mark all viewed" — clears the unread backlog in one call. Shown only while something is actually outlined, so
  // it isn't a button that does nothing; re-evaluated whenever cards are added or the outlines are cleared.
  const markAll = document.getElementById("markAllViewed");
  const anyUnviewed = () => !!document.querySelector("a.imgcard.unviewed");
  function syncMarkAll() { if (markAll) markAll.hidden = !anyUnviewed(); }
  if (markAll) markAll.addEventListener("click", async () => {
    markAll.disabled = true;
    try {
      const r = await Api.send("/api/history/viewed", "POST");
      if (!r.ok) throw new Error(r.status);
      const n = ((await r.json().catch(() => null)) || {}).marked || 0;
      document.querySelectorAll("a.imgcard.unviewed").forEach(el => el.classList.remove("unviewed"));
      toast(n ? `Marked ${n} image${n === 1 ? "" : "s"} viewed` : "Nothing left to mark");
      // With the unviewed filter on, every card on screen no longer belongs to the query that fetched it. Re-run it
      // rather than leaving a grid that contradicts its own filter until the next reload.
      if (unviewed && n) restart();
    } catch (e) { console.error("mark viewed failed:", e); toast("Couldn't mark them viewed"); }
    finally { markAll.disabled = false; syncMarkAll(); }
  });

  fillDates();              // the server-rendered first page
  render();
  syncMarkAll();
  keepFillingIfVisible();   // first page may not fill the viewport
})();
