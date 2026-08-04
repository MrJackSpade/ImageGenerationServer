//TODO: CHECK FOR FALLBACKS
// Reusable multi-select model dropdown — the Style/Model picker shared by the compose, artist, and edit pages.
// Behavior (the gen page's pattern): a short tap picks exactly one model and closes the menu; a ~450ms long-press
// enters multi-select mode (per-row checkboxes appear, taps toggle, the menu stays open). Multi mode persists
// until the selection empties. The per-row checkbox is the SINGLE source of truth for selection; CSS hides it
// until .multi is set on the menu, and marks the chosen single-select row with a ✓. Loaded after core.js.
//
// createModelPicker(opts) -> { rebuild, setSelectedIds, open, getSelectedIds, getSelected, getPrimary, isMulti }
// opts:
//   select, toggle, menu   — the .model-select wrapper, its toggle button, and the popover (required)
//   nameOf(item)           — display name (used in rows and the toggle label)
//   favOf?(item)->bool, timeOf?(item)->seconds, tagsOf?(item)->string[]   — optional row adornments
//   groups?: [{label, match(item)->bool}]   — fixed group headers (compose: image/video); omit for a flat list
//   groupBy?(item)->str|null  — dynamic grouping: items with the same returned label share a header; null/"" = no
//                               header (rendered flat). Used by the edit page's Effects bucket (groups by effect type).
//   hint?: string          — footer hint text
//   labelFor?(items)->str  — toggle label; default "Pick a model…" / single name / "N models selected"
//   onChange?(ids)         — after ANY selection change (refresh dependent UI: placeholder/params/refs/…)
//   onCommit?(ids)         — after a USER-driven change only (persist prefs)
const PICKER_HOLD_MS = 450;   // long-press dwell to enter multi-select (matches the count picker)
function createModelPicker(opts) {
  const { select, toggle, menu } = opts;
  const nameOf = opts.nameOf;
  const favOf = opts.favOf || (() => false);
  const timeOf = opts.timeOf || (() => 0);
  const tagsOf = opts.tagsOf || (() => []);
  const onChange = opts.onChange || (() => {});
  const onCommit = opts.onCommit || (() => {});
  const labelFor = opts.labelFor || (items => !items.length ? "Pick a workflow…" : items.length === 1 ? nameOf(items[0]) : `${items.length} workflows selected`);
  let byId = {};        // id -> item, from the last rebuild (resolves selection back to items)
  let multi = false;

  const cbs = () => Array.from(menu.querySelectorAll('input[type="checkbox"]'));
  const selectedIds = () => cbs().filter(c => c.checked).map(c => c.value);
  const selected = () => selectedIds().map(id => byId[id]).filter(Boolean);
  // ★ favorites first, then by name — the order both pages used before they shared this. sensitivity:'base' so the
  // name sort ignores case ('Zephyr' does not jump above 'anima'); ties among case-equal names keep input order.
  const sortItems = arr => arr.slice().sort((a, b) => { const af = favOf(a) ? 0 : 1, bf = favOf(b) ? 0 : 1; return af !== bf ? af - bf : nameOf(a).localeCompare(nameOf(b), undefined, { sensitivity: "base" }); });

  function row(m) {
    const opt = document.createElement("div"); opt.className = "model-opt"; opt.dataset.id = m.id; opt.setAttribute("role", "option");
    const cb = document.createElement("input"); cb.type = "checkbox"; cb.value = m.id; cb.tabIndex = -1; cb.setAttribute("aria-hidden", "true");
    const text = document.createElement("div"); text.className = "model-opt-text";
    const nameRow = document.createElement("div"); nameRow.className = "model-opt-namerow";
    const nm = document.createElement("span"); nm.className = "model-opt-nm"; nm.textContent = (favOf(m) ? "★ " : "") + nameOf(m); nameRow.appendChild(nm);
    const t = timeOf(m); if (t) { const tm = document.createElement("span"); tm.className = "model-opt-time"; tm.textContent = fmtDuration(t); nameRow.appendChild(tm); }
    text.appendChild(nameRow);
    const tg = tagsOf(m) || []; if (tg.length) { const sub = document.createElement("div"); sub.className = "model-opt-tags"; for (const x of tg) { const chip = document.createElement("span"); chip.className = "model-opt-tag"; chip.textContent = x; sub.appendChild(chip); } text.appendChild(sub); }
    opt.appendChild(cb); opt.appendChild(text); return opt;
  }
  function rebuild(models) {
    byId = {}; for (const m of models) byId[m.id] = m;
    menu.innerHTML = "";
    if (opts.groups) {
      for (const g of opts.groups) {
        const ms = sortItems(models.filter(g.match)); if (!ms.length) continue;
        const head = document.createElement("div"); head.className = "model-group"; head.textContent = g.label; menu.appendChild(head);
        for (const m of ms) menu.appendChild(row(m));
      }
    } else if (opts.groupBy) {
      // Dynamic grouping: bucket items by groupBy(item); group order = first appearance after the fav/name sort.
      // Items with NO group (null/"") always render FIRST, headerless — a headerless row placed after a header would
      // read as belonging to that header, so the ungrouped bucket can only be unambiguous at the top. Everything
      // after the first header therefore sits under one.
      const order = [], byGroup = new Map();
      for (const m of sortItems(models)) {
        const g = opts.groupBy(m) || "";
        if (!byGroup.has(g)) { byGroup.set(g, []); order.push(g); }
        byGroup.get(g).push(m);
      }
      order.sort((a, b) => (a === "" ? -1 : b === "" ? 1 : 0));   // stable: only lifts the ungrouped bucket to the front
      for (const g of order) {
        if (g) { const head = document.createElement("div"); head.className = "model-group"; head.textContent = g; menu.appendChild(head); }
        for (const m of byGroup.get(g)) menu.appendChild(row(m));
      }
    } else {
      for (const m of sortItems(models)) menu.appendChild(row(m));
    }
    if (opts.hint) { const h = document.createElement("div"); h.className = "model-hint"; h.textContent = opts.hint; menu.appendChild(h); }
  }
  function syncStates() { cbs().forEach(c => c.closest(".model-opt").classList.toggle("selected", c.checked)); }
  function syncLabel() { toggle.innerHTML = ""; const s = document.createElement("span"); s.textContent = labelFor(selected()); toggle.appendChild(s); }
  function setMulti(on) { multi = on; menu.classList.toggle("multi", on); }
  function refresh() { syncStates(); syncLabel(); onChange(selectedIds()); }
  function commit(close) { refresh(); onCommit(selectedIds()); if (close) open(false); }
  function open(o) { menu.hidden = !o; toggle.setAttribute("aria-expanded", String(o)); }

  // Programmatic selection (boot default / prefs restore / bucket switch): set checkboxes, infer the mode
  // (multi when 2+), refresh dependent UI — but do NOT persist (callers restore, they don't re-save).
  function setSelectedIds(ids) {
    const set = new Set(ids || []);
    cbs().forEach(c => c.checked = set.has(c.value));
    setMulti(selectedIds().length > 1);
    refresh();
  }
  function tapOption(opt) {
    const cb = opt.querySelector('input[type="checkbox"]');
    if (multi) {
      cb.checked = !cb.checked;
      if (selectedIds().length === 0) setMulti(false);   // emptied -> back to the single-select list
      commit(false);                                     // multi-select keeps the menu open
    } else {
      cbs().forEach(c => c.checked = false); cb.checked = true;   // single-select: exactly this one
      commit(true);                                      // ...and close, like a plain dropdown
    }
  }

  let timer = null, fired = false, px = 0, py = 0;
  toggle.addEventListener("click", () => open(menu.hidden));
  menu.addEventListener("pointerdown", e => {
    const opt = e.target.closest(".model-opt"); if (!opt) return;
    fired = false; px = e.clientX; py = e.clientY; clearTimeout(timer);
    timer = setTimeout(() => { fired = true; setMulti(true); opt.querySelector('input[type="checkbox"]').checked = true; commit(false); }, PICKER_HOLD_MS);
  });
  menu.addEventListener("pointermove", e => { if (timer && (Math.abs(e.clientX - px) > 10 || Math.abs(e.clientY - py) > 10)) { clearTimeout(timer); timer = null; } });
  ["pointerup", "pointerleave", "pointercancel"].forEach(ev => menu.addEventListener(ev, () => { clearTimeout(timer); timer = null; }));
  menu.addEventListener("click", e => { const opt = e.target.closest(".model-opt"); if (!opt) return; if (fired) { fired = false; return; } tapOption(opt); });
  document.addEventListener("pointerdown", e => { if (!menu.hidden && !select.contains(e.target)) open(false); }, true);

  return {
    rebuild, setSelectedIds, open,
    getSelectedIds: selectedIds, getSelected: selected,
    getPrimary: () => { const ms = selected(); return ms.length === 1 ? ms[0] : null; },
    isMulti: () => multi,
  };
}
