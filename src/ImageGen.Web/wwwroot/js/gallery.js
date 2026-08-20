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
  // The workflow filter: null = none (All), otherwise the configuration id to filter on — which may be "" (the legacy
  // empty-ModelId "Anima" workflow). null and "" are DISTINCT states and must stay so all the way to the server:
  // sending "" as "no filter" was #188. data-workflow-set marks whether a filter is active; only then is data-workflow
  // (possibly "") the id.
  let workflow = root.dataset.workflowSet ? (root.dataset.workflow || "") : null;
  // Server-side like the other two: filtering an already-paged result would give short pages, a wrong total, and a
  // scroll that stalls whenever a page happens to be entirely viewed.
  let unviewed = !!root.dataset.unviewed;
  // Bumped on every filter change; a fetch whose token is stale belongs to a query the user has already replaced, so
  // its rows must not land in the grid (typing fast otherwise interleaves two result sets).
  let seq = 0;

  function cards(items) {
    const fragment = document.createDocumentFragment();
    (items || []).forEach(r => fragment.appendChild(buildImageCard(r, { showDate: true })));
    return fragment;
  }

  // The selected workflow's display name (without the count), for the empty-state message.
  function workflowName() {
    const opt = wfSelect && wfSelect.selectedOptions[0];
    return (opt && opt.dataset.name) || "";
  }

  // The chosen filter as the server takes it: null for the "All" option (an explicit no-filter, marked data-all),
  // otherwise the option's value — which is "" for the legacy empty-ModelId workflow and an id for the rest. Keeping
  // null and "" apart here is the client half of the #188 fix; the old `workflow || null` collapsed "" to null.
  function selectedWorkflow() {
    const opt = wfSelect && wfSelect.selectedOptions[0];
    if (!opt || opt.dataset.all !== undefined) return null;
    return opt.value;
  }

  function render() {
    status.textContent = done ? (total ? `${total} image${total === 1 ? "" : "s"}` : "") : "Loading…";
    const showEmpty = done && total === 0;
    empty.classList.toggle("hidden", !showEmpty);
    if (!showEmpty) return;
    // Say which filter came up empty — with any of them on, "no images yet" would read as though the history were
    // empty. The unviewed filter earns its own wording: emptying it is the NORMAL end state (you opened everything,
    // or you just pressed Mark all viewed), not a search that found nothing.
    // workflow === null is "no workflow filter"; "" is a real filter (the legacy empty-ModelId workflow), so test
    // against null, not truthiness.
    const hasWf = workflow !== null;
    const wf = hasWf ? `<b>${escapeHtml(workflowName() || workflow)}</b>` : "";
    if (unviewed && (search || hasWf)) empty.innerHTML = "Nothing unviewed matches those filters.";
    else if (unviewed) empty.innerHTML = "Nothing unviewed — you've opened everything.";
    else if (search && hasWf) empty.innerHTML = `Nothing from ${wf} has every word in <b>${escapeHtml(search)}</b>.`;
    else if (search) empty.innerHTML = `No image's prompt has every word in <b>${escapeHtml(search)}</b>.`;
    else if (hasWf) empty.innerHTML = `No images from ${wf} yet.`;
    else empty.innerHTML = 'No images yet. <a href="/">Make one →</a>';
  }

  async function loadNext() {
    if (loading || done) return;
    const mine = seq;
    loading = true; render();
    try {
      const d = await queryHistory({ page: page + 1, pageSize, search: search || null, workflow, unviewedOnly: unviewed });
      if (mine !== seq) return;                 // a newer filter replaced this query mid-flight
      page += 1;
      if (typeof d.total === "number") total = d.total;
      if (d.items && d.items.length) {
        grid.appendChild(cards(d.items));
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
  // triggers to re-pull, and so does the gallery — otherwise it would be the one page where a batch you were
  // watching never showed up until you reloaded.
  //
  // The events are only a SIGNAL that something changed. What to show comes from re-asking the server for
  // page 1 — accumulating ids this tab happened to witness would make a reload disagree with a tab that watched
  // the batch run (recents.js carries the same note). Thumbnails only: nothing opens,
  // nothing expands, and someone scrolled into last month does not get the view moved under them.
  let refreshing = false;
  async function refreshNewest() {
    if (refreshing) return;                       // one in flight is enough; the next event re-checks anyway
    refreshing = true;
    const mine = seq;
    try {
      const d = await queryHistory({ page: 1, pageSize, search: search || null, workflow, unviewedOnly: unviewed });
      if (mine !== seq) return;                   // a filter changed while this was in the air
      if (typeof d.total === "number") total = d.total;
      if (!d.items || !d.items.length) return;

      // Prepend only what is not already on screen, so scroll position and the loaded pages below survive.
      const seen = new Set(Array.from(grid.querySelectorAll("a.imgcard"))
        .map(a => decodeURIComponent((a.getAttribute("href") || "").replace("/image/", "")))); 
      const fresh = d.items.filter(r => !seen.has(String(r.id)));
      if (!fresh.length) return;

      grid.prepend(cards(fresh));
      loaded += fresh.length;
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
  // so a `window` listener never sees them — listening on `window` would leave /gallery dead while every other page
  // updated live. Listen on the element the event is actually dispatched on, exactly as artist.js and recents.js do.
  document.addEventListener("imagegen:generated", refreshNewest);
  document.addEventListener("imagegen:refresh", refreshNewest);

  // Start over on a new filter: empty the grid, rewind to "no pages loaded", and pull page 1 for the new query.
  function applyFilters() {
    const nextSearch = box ? box.value.trim() : search;
    const nextWorkflow = wfSelect ? selectedWorkflow() : workflow;
    const nextUnviewed = unviewedBox ? unviewedBox.checked : unviewed;
    if (nextSearch === search && nextWorkflow === workflow && nextUnviewed === unviewed) return;   // what's on screen already answers this
    search = nextSearch; workflow = nextWorkflow; unviewed = nextUnviewed;
    const url = new URL(location.href);
    if (search) url.searchParams.set("q", search); else url.searchParams.delete("q");
    // null = no filter → drop the parameter (a reload then omits it, which the server reads as "no filter"). A set
    // filter is written as-is, INCLUDING "" (?workflow=), which the server reads as the legacy empty-ModelId workflow —
    // distinct from an absent parameter. Collapsing "" to "delete" here would resurrect #188 on reload.
    if (workflow !== null) url.searchParams.set("workflow", workflow); else url.searchParams.delete("workflow");
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

  fillImageCardDates(grid); // the server-rendered first page
  render();
  syncMarkAll();
  keepFillingIfVisible();   // first page may not fill the viewport
})();
