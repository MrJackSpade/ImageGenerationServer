//TODO: CHECK FOR FALLBACKS
// Reusable booru tag / artist autocomplete for a prompt <textarea>. Ported verbatim from the compose page's
// inlined logic so behavior stays identical, but parameterized over the target input + popup + the active model.
//
//   initTagBox({ input, pop, getModel, onAccept })
//     input    : the <textarea> to attach to
//     pop      : the popup <div> (positioned inside the same offsetParent as `input`; reuse .tag-pop styles)
//     getModel : () => the active model object; autocomplete is enabled only when its `.tagging` is set
//                ({ tags, artists, ... }, exactly as the /workflows row exposes via card.tagging)
//     onAccept : optional callback fired after a tag is inserted (e.g. to persist draft state)
//     allowArtist : optional () => bool, false to suppress '@' completion entirely (the artist page locks the
//                artist, so offering to complete a second one there only invites a prompt that gets excluded)
//
// Triggers on a '#' (tag), '@' (artist), '!' (quiet tag) or '~' (guide tag) token at the caret; POSTs to
// /forge/tags (count- or model-ranked). POST,
// not GET: the request carries the prompt being typed, and a URL would leave it in the browser's own history.
// Depends on core.js globals: GATEWAY, escapeHtml. Returns { close, isOpen }.
function initTagBox(opts) {
  const input = opts.input, pop = opts.pop;
  const getModel = opts.getModel || (() => null), onAccept = opts.onAccept || (() => {});
  const allowArtist = opts.allowArtist || (() => true);
  let state = null, seq = 0, timer = null;

  const model = () => { const m = getModel(); return (m && m.tagging) ? m : null; };
  function token() {
    if (input.selectionStart !== input.selectionEnd) return null;
    const pos = input.selectionStart, text = input.value; let s = pos;
    // '[' ']' '{' '}' '|' bound a token too, so '#'/'@'/'!'/'~' autocomplete fires inside a [a|b] random or {a|b} group.
    while (s > 0 && !/[\s,\[\]{}|]/.test(text[s - 1])) s--;
    const m = /^([#@!~])([^\s,\[\]{}|]*)$/.exec(text.slice(s, pos));
    return m ? { start: s, end: pos, marker: m[1], frag: m[2] } : null;
  }
  const isOpen = () => state !== null;
  function close() { state = null; pop.classList.add("hidden"); pop.innerHTML = ""; }
  function position() { pop.style.left = input.offsetLeft + "px"; pop.style.top = (input.offsetTop + input.offsetHeight + 2) + "px"; pop.style.width = input.offsetWidth + "px"; }
  function render() {
    pop.innerHTML = "";
    state.items.forEach((it, i) => {
      const cat = tagCategoryClass(it.type);
      const o = document.createElement("div"); o.className = "opt" + (i === state.sel ? " sel" : "") + (cat ? " " + cat : ""); o.setAttribute("role", "option"); o.dataset.i = i;
      const meta = (it.p != null) ? `${(it.p * 100).toFixed(it.p >= 0.01 ? 1 : 2)}%` + (it.lift != null ? ` · ×${it.lift >= 10 ? Math.round(it.lift) : it.lift.toFixed(1)}` : "") : Number(it.count || 0).toLocaleString();
      o.innerHTML = `<span class="nm"><span class="mk">${state.tok.marker}</span>${escapeHtml(it.name)}</span><span class="ct">${meta}</span>`;
      pop.appendChild(o);
    });
    position(); pop.classList.remove("hidden");
  }
  function move(d) {
    if (!state || !state.items.length) return;
    const n = state.items.length; state.sel = (state.sel + d + n) % n;
    const opts2 = pop.querySelectorAll(".opt"); opts2.forEach((o, i) => o.classList.toggle("sel", i === state.sel));
    const sel = opts2[state.sel]; if (sel) sel.scrollIntoView({ block: "nearest" });
  }
  function accept(i) {
    if (!state) return; const it = state.items[i]; if (!it) return;
    const tok = state.tok, text = input.value; let wEnd = tok.end;
    while (wEnd < text.length && !/[\s,\[\]{}|]/.test(text[wEnd])) wEnd++;
    // Inside a [a|b] random or {a|b} explode group the separator is '|', not comma — don't inject ", " there.
    const before = text.slice(0, tok.start);
    const inGroup = before.lastIndexOf("[") > before.lastIndexOf("]") || before.lastIndexOf("{") > before.lastIndexOf("}");
    const insert = tok.marker + it.name + (inGroup ? "" : ", ");
    input.value = text.slice(0, tok.start) + insert + text.slice(wEnd);
    const caret = tok.start + insert.length; input.setSelectionRange(caret, caret);
    close(); input.focus(); onAccept();
  }
  async function query(tok, kind) {
    const s = ++seq; let ctx = null;
    if (tok.marker !== "@") {
      const text = input.value; let wEnd = tok.end; while (wEnd < text.length && !/[\s,\[\]{}|]/.test(text[wEnd])) wEnd++;
      // '#' tags and '~' GUIDE tags become context; never '@' artists, '!' quiet tags, or unmarked free text.
      // Conditioning on an artist makes the suggester predict that artist's pet subjects (yomu -> pantyhose); a quiet
      // tag is by definition one the user asked not to condition on; and a guide tag is the opposite — it exists only
      // to steer suggestions, so it is exactly what should be here even though it never reaches the picture.
      // Note the asymmetry: typing '!foo' still ASKS for context-ranked suggestions (you want good completions), it
      // just never CONTRIBUTES itself — which the filter below gives for free.
      // Split on '[' ']' '{' '}' '|' too so each alternative in a [#a|#b] or {#a|#b} group is its own context tag.
      const tags = (text.slice(0, tok.start) + text.slice(wEnd)).split(/[,\[\]{}|]/)
        .map(x => x.trim()).filter(x => x.startsWith("#") || x.startsWith("~"))
        .map(x => x.slice(1).trim()).filter(Boolean);
      if (tags.length) ctx = tags.join(",");
    }
    try {
      // POST, with the prompt context in the BODY. This fires on every keystroke; as a query string it wrote the
      // prompt into the browser's history and address-bar autocomplete as it was being typed.
      const r = await fetch(`${GATEWAY}/tags`, {
        method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ q: tok.frag, kind, limit: 10, ctx }),
      });
      // A failed lookup is REPORTED, not just closed. Closing the popup is visually identical to "no tags matched",
      // which is how a dead /tags route went unnoticed on the composer's main prompt (see compose.js queryTags).
      if (!r.ok) { console.error(`tags: POST ${GATEWAY}/tags -> ${r.status}`); close(); return; }
      const items = await r.json();
      if (s !== seq) return;
      const now = token(); if (!now || now.marker !== tok.marker || now.frag !== tok.frag) return;
      if (!Array.isArray(items) || !items.length) { close(); return; }
      state = { tok: now, items, sel: 0 }; render();
    } catch (err) { console.error("tags: autocomplete lookup failed", err); close(); }
  }
  function onInput() {
    const m = model(); if (!m) { close(); return; }
    const tok = token(); if (!tok) { close(); return; }
    const kind = tok.marker === "@" ? "artist" : "tag";
    if (kind === "artist" && !allowArtist()) { close(); return; }
    if ((kind === "artist" && !m.tagging.artists) || (kind === "tag" && !m.tagging.tags)) { close(); return; }
    if (kind === "artist" && tok.frag.length < 1) { close(); return; }
    clearTimeout(timer); timer = setTimeout(() => query(tok, kind), 110);
  }

  input.addEventListener("input", onInput);
  input.addEventListener("blur", () => setTimeout(close, 120));
  pop.addEventListener("mousedown", e => { const o = e.target.closest(".opt"); if (o) { e.preventDefault(); accept(+o.dataset.i); } });
  // Navigate/accept while open; stopImmediatePropagation so a host "Enter = submit" keydown on the same input
  // doesn't also fire when we're consuming the key for the popup.
  input.addEventListener("keydown", e => {
    if (!isOpen()) return;
    if (["ArrowDown", "ArrowUp", "Enter", "Tab", "Escape"].includes(e.key)) {
      e.preventDefault(); e.stopImmediatePropagation();
      if (e.key === "ArrowDown") move(1);
      else if (e.key === "ArrowUp") move(-1);
      else if (e.key === "Escape") close();
      else accept(state.sel);   // Enter or Tab
    }
  });
  return { close, isOpen };
}
