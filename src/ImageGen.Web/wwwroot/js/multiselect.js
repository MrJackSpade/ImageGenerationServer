//TODO: CHECK FOR FALLBACKS
// Image-grid multi-select + batch delete (history/recents pages only — NOT bookmarks, which it isn't loaded on).
// Android-style: long-press an image card to enter selection mode, then tap cards to toggle. A fixed top-right
// bar shows the count + a Delete button that batch-deletes the selected images from history. Operates on the same
// `a.imgcard[href^="/image/"]` cards the lightbox uses; in selection mode it intercepts clicks (capture phase, so
// it runs before lightbox.js) and toggles instead of opening the viewer. Loaded after core.js (deleteHistory/toast).
(function () {
  const isImg = a => !!a && a.matches && a.matches("a.imgcard") && /^\/image\//.test(a.getAttribute("href") || "");
  const idOf = a => { const m = (a.getAttribute("href") || "").match(/^\/image\/([^?#]+)/); return m ? decodeURIComponent(m[1]) : null; };

  let mode = false;
  const sel = new Set();          // selected image ids
  let bar = null;

  function ensureBar() {
    if (bar) return;
    bar = document.createElement("div");
    bar.className = "msbar hidden";
    bar.innerHTML = '<span class="ms-count">0 selected</span>'
      + '<button type="button" class="ms-del">🗑 Delete</button>'
      + '<button type="button" class="ms-cancel" aria-label="Cancel selection">✕</button>';
    document.body.appendChild(bar);
    bar.querySelector(".ms-cancel").addEventListener("click", exit);
    bar.querySelector(".ms-del").addEventListener("click", doDelete);
  }
  function updateBar() {
    ensureBar();
    bar.classList.toggle("hidden", !mode);
    bar.querySelector(".ms-count").textContent = `${sel.size} selected`;
    bar.querySelector(".ms-del").disabled = sel.size === 0;
  }
  function setMode(on) {
    mode = on;
    document.body.classList.toggle("ms-mode", on);
    if (!on) { sel.clear(); document.querySelectorAll("a.imgcard.ms-sel").forEach(c => c.classList.remove("ms-sel")); }
    updateBar();
  }
  function toggle(a) {
    const id = idOf(a); if (!id) return;
    if (sel.has(id)) { sel.delete(id); a.classList.remove("ms-sel"); }
    else { sel.add(id); a.classList.add("ms-sel"); }
    if (sel.size === 0) setMode(false); else updateBar();   // emptying the selection drops back out of select mode
  }
  function exit() { setMode(false); }

  function removeCards(id) { document.querySelectorAll("a.imgcard").forEach(a => { if (idOf(a) === id) a.remove(); }); }

  async function doDelete() {
    if (!sel.size) return;
    const ids = [...sel];
    if (!confirm(`Delete ${ids.length} image${ids.length > 1 ? "s" : ""} from your history?`)) return;
    bar.querySelector(".ms-del").disabled = true;
    let ok = 0, fail = 0;
    for (const id of ids) {
      try { const r = await deleteHistory(id); if (r && (r.ok || r.status === 204)) { ok++; removeCards(id); } else fail++; }
      catch (_) { fail++; }
    }
    setMode(false);
    toast(fail ? `Deleted ${ok}, ${fail} failed` : `Deleted ${ok}`);
    document.dispatchEvent(new CustomEvent("imagegen:refresh"));   // strips/grids reconcile from /api/history
  }

  // Long-press (~450ms, same dwell as the model picker) to enter selection / toggle the held card.
  let timer = null, fired = false, px = 0, py = 0;
  document.addEventListener("pointerdown", e => {
    const a = e.target.closest && e.target.closest("a.imgcard"); if (!isImg(a)) return;
    fired = false; px = e.clientX; py = e.clientY;
    clearTimeout(timer);
    timer = setTimeout(() => { fired = true; if (!mode) setMode(true); toggle(a); }, 450);
  });
  document.addEventListener("pointermove", e => {
    if (timer && (Math.abs(e.clientX - px) > 10 || Math.abs(e.clientY - py) > 10)) { clearTimeout(timer); timer = null; }
  });
  ["pointerup", "pointerleave", "pointercancel"].forEach(ev =>
    document.addEventListener(ev, () => { clearTimeout(timer); timer = null; }));

  // Capture phase: run before lightbox's click handler. Swallow the click after a long-press, and in selection
  // mode toggle the card instead of opening the viewer. Outside selection mode, do nothing (lightbox opens normally).
  document.addEventListener("click", e => {
    const a = e.target.closest && e.target.closest("a.imgcard"); if (!isImg(a)) return;
    if (fired) { fired = false; e.preventDefault(); e.stopPropagation(); return; }
    if (mode) { e.preventDefault(); e.stopPropagation(); toggle(a); }
  }, true);

  // Suppress the long-press callout/context menu on cards so mobile long-press selects cleanly.
  document.addEventListener("contextmenu", e => { if (e.target.closest && e.target.closest("a.imgcard") && isImg(e.target.closest("a.imgcard"))) e.preventDefault(); });
  document.addEventListener("keydown", e => { if (mode && e.key === "Escape") { e.preventDefault(); exit(); } });
})();
