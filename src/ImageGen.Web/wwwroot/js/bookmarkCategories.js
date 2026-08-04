// Press-and-hold (or right-click) a bookmark control — the image star or a tag/artist chip — to file it into
// categories. Attach it ONLY to controls you click to bookmark; a card or preview image that just links somewhere
// is not one. A short tap keeps its normal toggle behaviour (owned by detail.js/bookmarks.js); this
// only adds the hold path and suppresses the click that would otherwise follow it. Setting categories always ensures
// the thing is bookmarked: >=1 category files it under those on the bookmarks page, none = the Global bucket.
// Exposes window.attachCategoryLongPress(el, descFactory). descFactory() returns:
//   { scope:"token", name, kind, onSaved? }  or  { scope:"image", id, record, onSaved? }
// where record is the image's { ts,id,prompt,model,modelId,aspect,marks } (used to create the bookmark if unsaved).
// Uses core.js (fetchCategories/postTokenCategories/postImageCategories/toast).
(function () {
  const LONG_MS = 500, MOVE_TOL = 10;

  // ---- the shared dialog (a single instance, reused) -------------------------------------------
  let modal = null, listEl = null, newInput = null, saveCb = null;

  function ensureModal() {
    if (modal) return;
    modal = document.createElement("div");
    modal.className = "modal-overlay hidden";
    modal.innerHTML =
      '<div class="modal-card cat-card"><h3>Add to categories</h3>'
      + '<div class="cat-list"></div>'
      + '<div class="cat-new"><input type="text" placeholder="New category…" maxlength="120" /></div>'
      + '<div class="modal-actions"><button type="button" class="link-btn" data-cat="cancel">Cancel</button>'
      + '<button type="button" data-cat="save">Save</button></div></div>';
    document.body.appendChild(modal);
    listEl = modal.querySelector(".cat-list");
    newInput = modal.querySelector(".cat-new input");

    const doSave = async () => {
      const cats = selectedCategories();
      const cb = saveCb;
      closeModal();
      if (cb) await cb(cats);
    };
    modal.addEventListener("click", e => {
      if (e.target === modal) { closeModal(); return; }
      const b = e.target.closest("button[data-cat]"); if (!b) return;
      if (b.dataset.cat === "save") doSave(); else closeModal();
    });
    newInput.addEventListener("keydown", e => {
      if (e.key === "Enter") { e.preventDefault(); addFromInput(); }
      else if (e.key === "Escape") { e.preventDefault(); closeModal(); }
    });
  }

  function closeModal() { if (modal) modal.classList.add("hidden"); saveCb = null; }

  function addCheckbox(name, checked) {
    const row = document.createElement("label");
    row.className = "cat-row";
    row.innerHTML = '<input type="checkbox"' + (checked ? " checked" : "") + ' /> <span></span>';
    row.querySelector("span").textContent = name;
    row.querySelector("input").dataset.name = name;
    listEl.appendChild(row);
    return row;
  }

  // Commit whatever's typed in the "new category" box: check it if it already exists (case-insensitive), else add it.
  function addFromInput() {
    const name = newInput.value.trim();
    newInput.value = "";
    if (!name) return;
    const empty = listEl.querySelector(".cat-empty"); if (empty) empty.remove();
    const existing = [...listEl.querySelectorAll("input[type=checkbox]")]
      .find(c => c.dataset.name.toLowerCase() === name.toLowerCase());
    if (existing) existing.checked = true;
    else addCheckbox(name, true);
  }

  function selectedCategories() {
    addFromInput(); // fold in a typed-but-not-added value
    return [...listEl.querySelectorAll("input[type=checkbox]")].filter(c => c.checked).map(c => c.dataset.name);
  }

  async function openDialog(desc) {
    ensureModal();
    listEl.innerHTML = ""; newInput.value = "";
    let data = { all: [], selected: [] };
    try { data = await fetchCategories(queryFor(desc)); } catch (e) { console.error("categories load failed:", e); toast("Couldn't load categories"); }

    const selected = new Set((data.selected || []).map(s => s.toLowerCase()));
    const seen = new Set();
    (data.all || []).forEach(n => { seen.add(n.toLowerCase()); addCheckbox(n, selected.has(n.toLowerCase())); });
    // A selected category not in the all-list would be odd, but render it checked rather than silently drop it.
    (data.selected || []).forEach(n => { if (!seen.has(n.toLowerCase())) { seen.add(n.toLowerCase()); addCheckbox(n, true); } });
    if (!listEl.children.length) {
      const p = document.createElement("p");
      p.className = "cat-empty";
      p.textContent = "No categories yet — add one below.";
      listEl.appendChild(p);
    }

    saveCb = cats => saveCategories(desc, cats);
    modal.classList.remove("hidden");
    setTimeout(() => newInput.focus(), 30);
  }

  function queryFor(desc) {
    return desc.scope === "image"
      ? "scope=image&id=" + encodeURIComponent(desc.id)
      : "scope=token&name=" + encodeURIComponent(desc.name) + "&kind=" + encodeURIComponent(desc.kind);
  }

  async function saveCategories(desc, cats) {
    try {
      const r = desc.scope === "image"
        ? await postImageCategories(desc.record, cats)
        : await postTokenCategories(desc.name, desc.kind, cats);
      if (!r.ok) throw new Error(r.status);
      // Awaited: a caller may have its own server call to make in response (detail.js lifts a ban), and its toast
      // has to be able to land before the success one rather than after it.
      if (desc.onSaved) await desc.onSaved(cats);
      toast(cats.length ? ("Filed under " + cats.join(", ")) : "Saved to Global");
    } catch (e) { console.error("categories save failed:", e); toast("Couldn't save categories"); }
  }

  // ---- press-and-hold + right-click trigger ----------------------------------------------------
  function attach(el, descFactory) {
    let timer = null, startX = 0, startY = 0, fired = false;
    const clear = () => { if (timer) { clearTimeout(timer); timer = null; } };

    el.addEventListener("pointerdown", e => {
      if (typeof e.button === "number" && e.button !== 0) return; // primary button / touch only
      fired = false; startX = e.clientX; startY = e.clientY;
      clear();
      timer = setTimeout(() => { fired = true; clear(); openDialog(descFactory()); }, LONG_MS);
    });
    el.addEventListener("pointermove", e => {
      if (timer && (Math.abs(e.clientX - startX) > MOVE_TOL || Math.abs(e.clientY - startY) > MOVE_TOL)) clear();
    });
    el.addEventListener("pointerup", clear);
    el.addEventListener("pointercancel", clear);
    el.addEventListener("pointerleave", clear);
    // Swallow the click a completed long-press would otherwise fire (toggle bookmark / follow a card link).
    el.addEventListener("click", e => {
      if (fired) { e.preventDefault(); e.stopPropagation(); fired = false; }
    }, true);
    // Desktop mouse: right-click opens the same dialog.
    el.addEventListener("contextmenu", e => { e.preventDefault(); clear(); openDialog(descFactory()); });
  }

  window.attachCategoryLongPress = attach;
})();
