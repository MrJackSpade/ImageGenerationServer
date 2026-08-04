// Queue page: a paginated, cross-user feed of every generation on this box (all users). Unfinished work comes FIRST,
// in the order the queue will serve it (the row on the GPU is the top row), with a progress bar + time-remaining
// countdown; finished jobs follow, newest first. 25 per page, polled every 2s whichever page is shown. The prompt is
// shown only for the current user's own jobs (others' prompts are private). Uses core.js (GATEWAY, escapeHtml,
// fmtDuration).
(function () {
  const $list = document.getElementById("queueList");
  const $pager = document.getElementById("queuePager");
  const $outstanding = document.getElementById("queueOutstanding");
  const $cancelAll = document.getElementById("queueCancelAll");
  const $cancelMine = document.getElementById("queueCancelMine");
  if (!$list) return;

  const PAGE_SIZE = 25;
  const catalog = {};   // configId -> { name, avgSeconds }
  const nameOf = id => (catalog[id] && catalog[id].name) || id;

  let page = 1, total = 0, pollTimer = null;

  // Names only — a failure here degrades the queue to raw config ids, which is visible and harmless. It is logged
  // rather than swallowed so "why is the queue showing ids instead of names" has an answer.
  async function loadCatalog() {
    // DIAGNOSTIC: /workflows is fetched serially BEFORE the first /queue, so the queue list cannot appear until this
    // returns. It re-probes ComfyUI (object_info per loader) on every call — the suspected source of a long blank list.
    const t = performance.now();
    try {
      const r = await fetch(`${GATEWAY}/workflows`);
      console.log(`[queue] /workflows responded ${r.status} in ${Math.round(performance.now() - t)}ms`);
      if (!r.ok) throw new Error(`the catalog answered ${r.status}`);
      const rows = (await r.json()) || [];
      for (const row of rows) catalog[row.id] = { name: row.friendlyName || row.id, avgSeconds: row.avgSeconds };
      console.log(`[queue] /workflows parsed ${rows.length} rows, total ${Math.round(performance.now() - t)}ms`);
    } catch (e) {
      console.error(`[queue] /workflows FAILED after ${Math.round(performance.now() - t)}ms; queue will show raw ids:`, e);
    }
  }

  function totalPages() { return Math.max(1, Math.ceil(total / PAGE_SIZE)); }

  // Fetch a page. `live` true keeps the per-second countdown alive (signature-deduped render); false forces a fresh
  // rebuild (used when navigating, so the list always reflects the page just asked for).
  async function fetchPage(p, live) {
    // DIAGNOSTIC: separate the network+server time (the DB page query lives behind this) from the DOM render time,
    // and log it on every poll so a one-off slow first load is distinguishable from persistent latency.
    const t = performance.now();
    let data;
    try {
      const r = await fetch(`${GATEWAY}/queue?page=${p}&pageSize=${PAGE_SIZE}`);
      console.log(`[queue] /queue?page=${p} responded ${r.status} in ${Math.round(performance.now() - t)}ms`);
      data = r.ok ? await r.json() : null;
    }
    catch (e) { console.error(`[queue] /queue?page=${p} THREW after ${Math.round(performance.now() - t)}ms:`, e); return; }
    if (!data) { console.warn(`[queue] /queue?page=${p} returned no data (response not ok)`); return; }
    page = data.page || p; total = data.total || 0;
    if (!live) lastSig = null;
    const tr = performance.now();
    render(data.jobs || []);
    renderPager();
    renderOutstanding(data.outstanding);
    console.log(`[queue] page ${page}: ${(data.jobs || []).length} rows, fetch+parse ${Math.round(tr - t)}ms, render ${Math.round(performance.now() - tr)}ms`);
  }

  // What's left across the WHOLE box, from the server — this page shows 25 rows and its `total` counts finished
  // history too, so there is nothing here to add up. Blank when the queue is idle rather than reading "0 jobs".
  const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;
  function renderOutstanding(o) {
    // A bulk cancel with nothing to cancel is a dead button, so each one is present only while its scope has work.
    if ($cancelAll) $cancelAll.hidden = !(o && o.jobs > 0);
    if ($cancelMine) $cancelMine.hidden = !(o && o.mineJobs > 0);
    if (!$outstanding) return;
    if (!o || !o.images) { $outstanding.textContent = ""; $outstanding.title = ""; return; }
    const parts = [plural(o.jobs, "job"), plural(o.images, "image")];
    // A workflow with no timing history here has no average, so it adds nothing to the sum — the total is then a
    // lower bound, and says so with ≥ rather than quietly reading as an estimate of the whole thing.
    if (o.etaSeconds) parts.push((o.unpricedImages ? "≥" : "~") + fmtDuration(o.etaSeconds) + " left");
    else if (o.unpricedImages) parts.push("no estimate yet");
    $outstanding.textContent = parts.join(" · ");
    $outstanding.title = "Still to render on this box, every user. Wall-clock only if nothing else is submitted."
      + (o.unpricedImages
        ? ` ${plural(o.unpricedImages, "image")} on a workflow that hasn't rendered here yet, so it has no average and the total is a lower bound.`
        : "");
  }

  function go(p) {
    p = Math.min(Math.max(1, p), totalPages());
    fetchPage(p, false).then(schedulePolling);
  }

  // Poll whatever page is on screen. This used to poll page 1 only, on the assumption that in-flight jobs sit there —
  // but the queue serves the OLDEST job first, so during any backlog the live rows were on the LAST page, which never
  // refreshed. A row left mid-render there kept its progress bar ticking (tickAll below runs regardless of page) and
  // sat on "finishing…" indefinitely while the queue drained behind it. A page showing a live row must refresh it;
  // whether that page is the first one is not something this can assume.
  function schedulePolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    pollTimer = setInterval(() => fetchPage(page, true), 2000);
  }

  function timeAgo(iso) {
    if (!iso) return "";
    const t = Date.parse(iso); if (Number.isNaN(t)) return "";
    const s = Math.max(0, (Date.now() - t) / 1000);
    if (s < 60) return "just now";
    if (s < 3600) return `${Math.floor(s / 60)}m ago`;
    if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
    return `${Math.floor(s / 86400)}d ago`;
  }

  function statusText(j) {
    if (j.status === "running") return j.total > 1 ? `running ${Math.min(j.progress + 1, j.total)}/${j.total}` : "running";
    if (j.status === "queued") return j.jobsAhead ? `queued · ${j.jobsAhead} ahead` : "queued";
    if (j.status === "done") return j.total > 1 ? `done · ${j.produced}/${j.total}` : "done";
    // You stopped it — say so, and keep the count: a batch of 10 cancelled after 3 landed made 3 of the 10 asked for.
    if (j.status === "cancelled") return j.total > 1 ? `cancelled · ${j.produced}/${j.total}` : "cancelled";
    if (j.status === "error") return j.produced > 0 ? `partial · ${j.produced}/${j.total}` : "failed";
    return j.status;
  }
  // Cancelled is deliberately NOT the error colour. Nothing went wrong; the user asked for it.
  const statusClass = j => j.status === "running" ? "on" : j.status === "done" ? "ok"
    : j.status === "error" ? "err" : j.status === "cancelled" ? "off" : "";

  // Fraction 0..1 of the CURRENT image's expected time that has elapsed. Time-based (no cross-user step counts
  // here), capped just under full so a longer-than-average render doesn't read as "done"; null without an estimate.
  function elapsedFraction(expected, startedAtIso) {
    if (!expected || expected <= 0 || !startedAtIso) return null;
    const started = Date.parse(startedAtIso);
    if (Number.isNaN(started)) return null;
    return Math.min(0.99, Math.max(0, (Date.now() - started) / 1000 / expected));
  }

  // The bar tracks the JOB, not the picture on the GPU. `expectedSeconds`/`startedAt` describe the running slot only
  // (ForgeApi.QueueRowOf), so feeding them straight to the bar made a batch of ten fill and snap back to zero ten
  // times — while the label beside it already read "running 4/10". `progress` is the count of finished slots, so
  // whole images done plus the fraction of the one in flight is the honest number.
  //
  // Without an estimate the in-flight fraction is unknown, but progress/total still isn't: the bar then steps once
  // per finished image instead of going nowhere for the whole job. Null only while nothing at all is known.
  function batchFraction(expected, startedAtIso, progress, total) {
    const n = total > 0 ? total : 1;
    const current = elapsedFraction(expected, startedAtIso);
    if (current == null && progress <= 0) return null;
    return Math.min(0.99, (progress + (current || 0)) / n);
  }

  // Time left on the whole job: what's left of the image being rendered, plus a full estimate for each image after
  // it. Every slot in a job is the same workflow at the same size, so the running slot's estimate is the per-image
  // one. Degenerates to the old single-image countdown when total is 1.
  function remainingText(expected, startedAtIso, progress, total) {
    if (!expected || expected <= 0 || !startedAtIso) return "";
    const started = Date.parse(startedAtIso);
    if (Number.isNaN(started)) return "";
    const currentLeft = Math.max(0, expected - (Date.now() - started) / 1000);
    const notStarted = Math.max(0, (total > 0 ? total : 1) - progress - 1);
    const remaining = currentLeft + notStarted * expected;
    return remaining >= 1 ? `~${fmtDuration(Math.ceil(remaining))} left` : "finishing…";
  }

  // Update a running row's bar + countdown in place, once a second, so the countdown stays live between polls. Reads
  // only the dataset, so every value the fraction needs has to be on it (see row()).
  function tickRow(el) {
    const expected = Number(el.dataset.expected) || 0, started = el.dataset.started || "";
    const progress = Number(el.dataset.progress) || 0, total = Number(el.dataset.total) || 1;
    const frac = batchFraction(expected, started, progress, total);
    const fill = el.querySelector(".queue-bar-fill");
    if (fill && frac != null) fill.style.width = Math.round(frac * 100) + "%";
    const eta = el.querySelector(".queue-eta");
    if (eta) eta.textContent = remainingText(expected, started, progress, total);
  }
  function tickAll() { for (const el of $list.querySelectorAll(".queue-row.running")) tickRow(el); }

  function row(j) {
    const running = j.status === "running" && j.active;
    const el = document.createElement("div");
    el.className = "listrow queue-row" + (running ? " running" : "") + (j.active ? "" : " done");
    const exp = j.expectedSeconds || (catalog[j.model] && catalog[j.model].avgSeconds);
    if (running) {
      el.dataset.expected = exp || 0; el.dataset.started = j.startedAt || "";
      el.dataset.progress = j.progress || 0; el.dataset.total = j.total || 1;
    }

    const main = document.createElement("div"); main.className = "queue-main";
    const nr = document.createElement("div"); nr.className = "queue-namerow";
    nr.innerHTML = `<span class="listrow-name">${escapeHtml(nameOf(j.model))}</span>`
      + `<span class="listrow-badge ${j.kind === "edit" ? "is-edit" : "is-gen"}">${j.kind === "edit" ? "edit" : "gen"}</span>`
      + `<span class="queue-status ${statusClass(j)}">${escapeHtml(statusText(j))}</span>`
      // How long the finished generation took (queue wait excluded) — only on completed rows that recorded a render.
      + (!j.active && j.durationSeconds ? `<span class="queue-took" title="Generation time">took ${escapeHtml(fmtDuration(j.durationSeconds))}</span>` : "");
    main.appendChild(nr);

    if (j.mine && j.prompt) { const p = document.createElement("div"); p.className = "queue-prompt"; p.textContent = j.prompt; main.appendChild(p); }
    else if (!j.mine) { const p = document.createElement("div"); p.className = "queue-prompt muted"; p.textContent = "(another user)"; main.appendChild(p); }

    if (running) {
      const prog = document.createElement("div"); prog.className = "queue-prog";
      const bar = document.createElement("div"); bar.className = "queue-bar";
      const fill = document.createElement("div"); fill.className = "queue-bar-fill"; bar.appendChild(fill);
      const eta = document.createElement("span"); eta.className = "queue-eta";
      prog.appendChild(bar); prog.appendChild(eta); main.appendChild(prog);
    }
    el.appendChild(main);

    // Cancel only makes sense for a live (queued/running) job.
    if (j.active) {
      const cancel = document.createElement("button"); cancel.className = "queue-cancel"; cancel.textContent = "Cancel";
      cancel.addEventListener("click", async () => {
        cancel.disabled = true; cancel.textContent = "Cancelling…";
        try { await fetch(`${GATEWAY}/cancel/${encodeURIComponent(j.jobId)}`, { method: "POST" }); } catch (e) { console.debug("best-effort cancel failed:", e); }
        setTimeout(() => fetchPage(page, false), 400);
      });
      el.appendChild(cancel);
    } else if (j.mine && j.requeueable > 0) {
      // Re-run the images this job never made. Only on your OWN finished rows: another user's row can't be requeued
      // and doesn't even carry a prompt. The count comes from the server, so the button never appears with nothing
      // behind it, and the server refuses anything whose inputs are gone rather than making a doomed job.
      const again = document.createElement("button");
      again.className = "queue-cancel queue-requeue";
      again.textContent = "Requeue";
      again.title = j.status === "cancelled"
        ? `Queue the ${j.requeueable} image${j.requeueable === 1 ? "" : "s"} you cancelled again`
        : `Try the ${j.requeueable} image${j.requeueable === 1 ? "" : "s"} that didn't get made again`;
      again.addEventListener("click", async () => {
        again.disabled = true; again.textContent = "Queueing…";
        try {
          const r = await fetch(`${GATEWAY}/requeue/${encodeURIComponent(j.jobId)}`, { method: "POST" });
          const body = await r.json().catch(() => null);
          if (!r.ok) { toast((body && body.error) || "Couldn't requeue"); }
          else { const n = (body && body.total) || 0; toast(`Queued ${n} image${n === 1 ? "" : "s"}`); }
        } catch (e) { console.error("requeue failed:", e); toast("Couldn't requeue"); }
        finally { again.disabled = false; again.textContent = "Requeue"; fetchPage(1, false); }
      });
      el.appendChild(again);
    }

    // Right-side stat: expected time while live, relative finish time once done. Appended LAST, after any button, so
    // it always sits at the row's right edge — behind the button it was pushed left on the rows that have one, and
    // the column zig-zagged down a page that mixes both.
    const time = document.createElement("span"); time.className = "listrow-stat";
    if (j.active) { time.title = "Expected time"; time.textContent = exp ? fmtDuration(exp) : ""; }
    else { time.title = "Finished"; time.textContent = timeAgo(j.finishedAt || j.createdAt); }
    el.appendChild(time);
    return el;
  }

  // Re-render only when the structural signature changes, so the per-second countdown isn't reset by every poll.
  function sig(jobs) {
    return jobs.map(j => `${j.jobId}:${j.status}:${j.progress}/${j.total}:${j.produced || 0}:${j.startedAt || ""}`).join("|");
  }
  let lastSig = null;

  function render(jobs) {
    const s = page + "#" + sig(jobs);
    if (s === lastSig) { tickAll(); return; }
    lastSig = s;
    if (!jobs.length) { $list.innerHTML = '<p class="muted">No generations yet.</p>'; return; }
    $list.innerHTML = "";
    for (const j of jobs) $list.appendChild(row(j));
    tickAll();
  }

  function renderPager() {
    if (!$pager) return;
    if (total <= PAGE_SIZE) { $pager.innerHTML = ""; return; }
    const pages = totalPages();
    const prev = page <= 1, next = page >= pages;
    $pager.innerHTML =
      `<button class="pager-btn${prev ? " disabled" : ""}" data-act="prev"${prev ? " disabled" : ""}>← Prev</button>`
      + `<span class="pager-info">Page ${page} of ${pages} · ${total} total</span>`
      + `<button class="pager-btn${next ? " disabled" : ""}" data-act="next"${next ? " disabled" : ""}>Next →</button>`;
  }

  // Bulk cancel. One server call per scope, not a loop over the rendered rows: this page shows 25 of a list it
  // re-polls every 2s, so a client-side loop would clear only the visible page and race the poll rebuilding it.
  // Both confirm — cancelling is irreversible, and "all" can discard work that isn't yours.
  async function bulkCancel(btn, path, ask) {
    if (!confirm(ask)) return;
    const label = btn.textContent;
    btn.disabled = true; btn.textContent = "Cancelling…";
    try {
      const r = await fetch(`${GATEWAY}${path}`, { method: "POST" });
      if (!r.ok) throw new Error(r.status);
      const n = (await r.json()).cancelled || 0;
      toast(n ? `Cancelled ${n} job${n === 1 ? "" : "s"}` : "Nothing left to cancel");
    } catch (e) { console.error("cancel failed:", e); toast("Couldn't cancel"); }
    finally { btn.disabled = false; btn.textContent = label; fetchPage(page, false); }
  }
  if ($cancelMine) $cancelMine.addEventListener("click", () =>
    bulkCancel($cancelMine, "/cancel-mine", "Cancel all of your queued and running generations?"));
  if ($cancelAll) $cancelAll.addEventListener("click", () =>
    bulkCancel($cancelAll, "/cancel-all", "Cancel EVERY queued and running generation on this box, including other users'?"));

  if ($pager) $pager.addEventListener("click", e => {
    const b = e.target.closest("button[data-act]"); if (!b || b.disabled) return;
    go(b.dataset.act === "prev" ? page - 1 : page + 1);
  });

  // DIAGNOSTIC timeline. `performance.now()` is milliseconds since navigation START, so logging it here reveals how
  // long AFTER the page began loading the queue script even runs — a large value means the HTML document/assets were
  // slow (before any of these fetches), a small one means the delay is entirely in the two awaited fetches below.
  (async () => {
    const t0 = performance.now();
    console.log(`[queue] bootstrap start at +${Math.round(t0)}ms since navigation`);
    await loadCatalog();
    console.log(`[queue] catalog done at +${Math.round(performance.now() - t0)}ms into bootstrap; fetching first page`);
    await fetchPage(1, false);
    console.log(`[queue] FIRST PAGE SHOWN at +${Math.round(performance.now() - t0)}ms into bootstrap`);
    schedulePolling(); setInterval(tickAll, 1000);
  })();
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") fetchPage(page, true); });
})();
