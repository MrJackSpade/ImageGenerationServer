// Compose page "Recent" strip. SOURCE OF TRUTH = the server, always. /api/recents returns the images this strip should
// show, already sized: the newest MIN, stretched to cover the current-or-last batch whenever that batch produced more
// than MIN. The window is worked out server-side from the job table, so a tab that just loaded shows exactly what a tab
// that watched the batch run shows.
//
// A generation announces "a new image exists" via `imagegen:generated` (and the live cross-device tracker fires
// `imagegen:refresh` when a job finalizes); this strip treats those purely as TRIGGERS to re-pull. Nothing about what
// to show is accumulated here — accumulating it would crop the last batch to its newest 48 on a reload, since the
// batch size would only ever exist in the page that watched it happen. The outline is the same story: it means "you
// haven't opened this", which is a per-user fact the server keeps, not a set of ids this tab happened to witness.
// Uses core.js.
(function () {
  const wrap = document.getElementById("recent-wrap");
  const grid = document.getElementById("recent");
  if (!wrap || !grid) return;
  const MIN = 48;                 // fewest images the strip is willing to show; the server may return more
  let items = [];
  let timer = null;
  let seq = 0;                    // refresh sequence: only the newest pull may apply, so a slow stale one can't win

  function render() {
    if (!items.length) { wrap.classList.add("hidden"); grid.innerHTML = ""; return; }
    wrap.classList.remove("hidden");
    grid.innerHTML = "";
    for (const r of items) grid.appendChild(buildImageCard(r));
  }
  async function refresh() {
    const mine = ++seq;
    try {
      const d = await Api.json(`/api/recents?min=${MIN}`, { cache: "no-store" });
      if (mine !== seq) return;     // a newer refresh started while this was in flight — drop this stale response
      items = d.items || [];        // the server's answer IS the strip — replace, don't merge; deletes show up at once
      render();
    } catch (e) {
      // The strip keeps whatever it last showed rather than blanking on a blip; the next trigger refreshes it. That
      // is deliberate — logged so it stays distinguishable from "nothing new has been generated".
      console.error("Recents strip could not be refreshed; showing the last known set:", e);
    }
  }
  function schedule() { clearTimeout(timer); timer = setTimeout(refresh, 250); }   // debounce bursts of triggers

  // "A new image exists" — re-pull. This is also what catches a batch's last slots, which land and finalize between
  // two liveSync polls. A new image has not been opened, so it comes back unviewed and outlined on its own.
  document.addEventListener("imagegen:generated", () => schedule());
  // "A job finalized" (cross-device tracker) — re-pull.
  document.addEventListener("imagegen:refresh", schedule);

  refresh();
})();
