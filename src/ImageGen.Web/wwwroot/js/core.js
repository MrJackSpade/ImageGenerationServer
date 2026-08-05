// Shared client foundation, loaded before every page script. window.GATEWAY is injected by _Layout.

const $ = id => document.getElementById(id);
const imageId = r => (typeof r === "string") ? r : ((r && (r.id != null ? r.id : r.filename)) || "");
const viewUrl = r => `${GATEWAY}/image/${encodeURIComponent(imageId(r))}`;
// Small JPEG preview for grid/list/recents cards (full image stays at viewUrl). Tens of KB vs a multi-MB PNG.
const THUMB_W = 512;
const thumbUrl = (r, w) => `${viewUrl(r)}?w=${w || THUMB_W}`;
// Absolute ws/wss URL for a gateway path — works whether GATEWAY is an absolute http(s) origin or, now that the
// gateway is embedded same-origin, a relative path like "/forge" (relative WebSocket urls aren't universally ok).
const gwWs = path => /^https?:/i.test(GATEWAY)
  ? GATEWAY.replace(/^http/i, "ws") + path
  : (location.protocol === "https:" ? "wss:" : "ws:") + "//" + location.host + GATEWAY + path;
const escapeHtml = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
// Mirror of PromptMarkers.Key. '!' is the INERT TAG marker (a tag the tag predictor is not conditioned on) and '~'
// the GUIDE TAG marker (seen only by the predictor, never rendered); both are ordinary tags to every client surface
// that matches on a key, so they must strip here too or a "!pig" segment won't match its own chip, bookmark or ban.
const normToken = s => String(s || "").trim().replace(/^[#@!~]/, "").trim().replace(/\s+/g, "_").toLowerCase();

// Does an image belong on this artist's page? Its `marks` map (name -> "tag"|"artist") must carry this artist AND
// no other — an artist page shows what that artist's style looks like, and a blend of two styles is evidence of
// neither, so a multi-artist image belongs to no individual artist page (it stays in the gallery, history and
// search). This mirrors the server's artist filter in HistoryRepository; the two must say the same thing or the
// live-added card outlives a reload that drops it.
//
// Every artist surface asks through here — the grid's live add, the composer's inline preview, anything added
// later — so they can't disagree about what belongs. A record with no marks belongs nowhere. The key is derived
// here rather than by each caller: mark keys are normalized lowercase server-side, and two call sites each
// lowercasing for themselves is how they drift apart.
function belongsToArtistPage(marks, artist) {
  if (!carriesArtistMark(marks, artist)) return false;
  const key = String(artist || "").toLowerCase();
  for (const k in marks) if (k !== key && marks[k] === "artist") return false;
  return true;
}
// Carries this artist's mark, alone or blended with others. Only for telling "made with this artist, but with a
// second one too" apart from "nothing to do with this artist" — what SHOWS on the page is belongsToArtistPage.
function carriesArtistMark(marks, artist) {
  return !!(marks && marks[String(artist || "").toLowerCase()] === "artist");
}

// Booru category id (0=general, 1=artist, 3=copyright, 4=character, 5=meta, 6=deprecated) -> the CSS category class an
// autocomplete suggestion / chip carries. Only the notable categories get one; general/deprecated/unknown stay neutral.
function tagCategoryClass(type) {
  switch (type) {
    case 3: return "cat-copyright";
    case 4: return "cat-character";
    case 5: return "cat-meta";
    default: return "";
  }
}

// A prompt like "a [red|blue|green] hat" is a randomization template: each [opt|opt|…] group is replaced by ONE
// option chosen at random, INDEPENDENTLY per group and per call. Call it once per submitted job (never once before a
// batch fan-out) so a batch of N draws N fresh rolls instead of N copies of the same one. A group with no '|' is left
// untouched (so weighting syntax like "[a:b:0.5]" survives); nested groups collapse innermost-first.
//
// An option may be EMPTY, which is how you express "this appears only some of the time": each '|' adds one more
// alternative, so "[#cow|]" is 1-in-2, "[#cow||]" 1-in-3, and so on — an empty option is a real draw, never skipped.
// Picking one leaves a hole where the group was ("1girl, [#cow|], solo" -> "1girl, , solo"), so tidySeparators puts
// the punctuation back in order. It runs ONLY when an empty option actually won: a prompt whose groups all resolved
// to text must reach the model exactly as the user wrote it, untouched by cosmetic whitespace rewriting.
function expandRandomPrompt(text) {
  let s = String(text == null ? "" : text);
  const group = /\[([^\[\]]*)\]/g;
  let droppedEmpty = false;
  for (let guard = 0; guard < 100; guard++) {
    let changed = false;
    s = s.replace(group, (m, body) => {
      if (!body.includes("|")) return m;                       // not an alternation — leave it as-is
      changed = true;
      const opts = body.split("|").map(o => o.trim());
      const pick = opts[Math.floor(Math.random() * opts.length)];
      if (!pick) droppedEmpty = true;                          // an empty option won — the hole needs tidying
      return pick;
    });
    if (!changed) break;                                        // no groups resolved this pass → done
  }
  return droppedEmpty ? tidySeparators(s) : s;
}

// Close the gap an empty alternation option left behind: collapse the run of spaces it split in two, fold the now-empty
// comma segment(s) into one separator, and drop a comma stranded at either END of the prompt. Newlines are preserved
// (only spaces and tabs collapse) so a multi-line prompt keeps its layout. A comma left at a LINE end/start stays —
// there it is still separating the two segments it always separated, and a newline alone would silently fuse them.
function tidySeparators(s) {
  return s.replace(/[ \t]+/g, " ")
    .replace(/\s*,(?:\s*,)+/g, ",")
    .replace(/[ \t]+\n/g, "\n")
    .replace(/^\s*,\s*/, "")
    .replace(/,[ \t]*$/, "")
    .trim();
}

// A prompt like "{cow|chicken|duck} in a field" is an EXPLODE template. Unlike [a|b] (which picks ONE option at
// random), each {opt|opt|…} group fans the prompt into one variant PER option, and multiple groups MULTIPLY
// (cartesian): "{cow|chicken} {fat|skinny}" -> 4 variants. Returns the list of base prompts, each still containing any
// [a|b] random groups (those are rolled per slot afterwards, so a "10 of each" batch still varies). Only a brace group
// that contains a '|' is an explode set; a plain "{x}" is left literal (stray braces are safe), mirroring how "[x]"
// with no '|' is left untouched. An empty option is a real variant ("{cow|}" -> "cow" and ""), and the hole it leaves
// is tidied exactly like an empty random pick.
function explodePrompts(text) {
  let variants = [{ s: String(text == null ? "" : text), empty: false }];
  const group = /\{([^{}]*\|[^{}]*)\}/;
  for (let guard = 0; guard < 100; guard++) {
    const next = [];
    let changed = false;
    for (const v of variants) {
      const m = group.exec(v.s);
      if (!m) { next.push(v); continue; }               // no explode set left in this variant
      changed = true;
      for (const raw of m[1].split("|")) {
        const opt = raw.trim();
        next.push({ s: v.s.slice(0, m.index) + opt + v.s.slice(m.index + m[0].length), empty: v.empty || !opt });
      }
    }
    variants = next;
    if (!changed) break;
  }
  return variants.map(v => v.empty ? tidySeparators(v.s) : v.s);
}

// How an explode template fans out, WITHOUT building the strings: the count of {…|…} sets and the product of their
// option counts (combos = 1 when there are none). Used to warn before a multiplicative batch and to route the fan-out.
function explodeInfo(text) {
  const groups = String(text == null ? "" : text).match(/\{[^{}]*\|[^{}]*\}/g) || [];
  const combos = groups.reduce((n, g) => n * g.slice(1, -1).split("|").length, 1);
  return { groupCount: groups.length, combos };
}

// --- workflow-configuration exposed parameters --------------------------------------------------
// A configuration's UI-exposed params (steps/cfg/…) come back on each /workflows row. These render them as
// design-matched numeric fields (prefilled with the configuration's values, so untouched controls reproduce the
// configuration exactly) and read them back as an `overrides` map for generate/edit. Shared by compose + edit.
// Intersection of UI-exposed params across the selected models (matched by key + type): a param renders only if
// EVERY selected model accepts it (so e.g. vres/mode stay visible across multiple pixelizers, but a param unique
// to one model hides). The first model's spec (value/choices/range/label) drives the control. Accepts a single
// model, an array of models, or null/[].
function sharedExposedParams(models) {
  const lists = models.map(m => (m && m.exposedParams) || []);
  if (!lists.length) return [];
  return lists[0].filter(p => lists.every(l => l.some(q => q.key === p.key && q.type === p.type)));
}
function renderParamFields(box, modelOrModels) {
  if (!box) return;
  box.innerHTML = "";
  const models = Array.isArray(modelOrModels) ? modelOrModels.filter(Boolean) : (modelOrModels ? [modelOrModels] : []);
  const ps = sharedExposedParams(models).filter(p => ["int", "double", "enum", "string", "bool"].includes(p.type));
  if (!ps.length) { box.hidden = true; box.classList.add("hidden"); return; }
  box.hidden = false; box.classList.remove("hidden");
  for (const p of ps) {
    const wrap = document.createElement("label"); wrap.className = "mp-field";
    const span = document.createElement("span"); span.className = "fld-label"; span.textContent = p.label || p.key;
    // Keep labels short; the long explanation lives in a hover tooltip (native title) marked with a small ⓘ.
    if (p.help) {
      wrap.title = p.help;
      const info = document.createElement("span"); info.className = "fld-help"; info.textContent = "ⓘ"; info.setAttribute("aria-label", p.help);
      span.appendChild(document.createTextNode(" ")); span.appendChild(info);
    }
    let inp;
    if (p.type === "enum" && Array.isArray(p.choices) && p.choices.length) {
      inp = document.createElement("select");
      for (const c of p.choices) { const o = document.createElement("option"); o.value = c; o.textContent = c; inp.appendChild(o); }
      if (p.value != null) inp.value = p.value;
    } else if (p.type === "int" || p.type === "double") {
      inp = document.createElement("input"); inp.type = "number";
      if (p.min != null) inp.min = p.min;
      if (p.max != null) inp.max = p.max;
      inp.step = p.step != null ? p.step : (p.type === "double" ? "0.1" : "1");
      inp.value = (p.value != null ? p.value : "");
    } else if (p.type === "bool") {
      inp = document.createElement("input"); inp.type = "checkbox";
      inp.checked = (p.value === true || p.value === "true");
      wrap.classList.add("mp-field-bool");
    } else {
      inp = document.createElement("input"); inp.type = "text"; inp.value = (p.value != null ? p.value : "");
    }
    inp.className = "fld-input";   // the field chrome rides on the input, not on the container it happens to be in
    inp.dataset.key = p.key; inp.dataset.ptype = p.type;
    wrap.appendChild(span); wrap.appendChild(inp); box.appendChild(wrap);
  }
}
function readOverrides(box) {
  const out = {};
  if (!box || box.hidden) return out;
  for (const inp of box.querySelectorAll("[data-key]")) {
    const t = inp.dataset.ptype;
    if (t === "int" || t === "double") {
      if (inp.value === "") continue;
      const n = Number(inp.value); if (Number.isNaN(n)) continue;
      out[inp.dataset.key] = t === "int" ? Math.round(n) : n;
    } else if (t === "bool") {
      out[inp.dataset.key] = !!inp.checked;
    } else {
      if (inp.value == null || inp.value === "") continue;
      out[inp.dataset.key] = inp.value;   // enum / string
    }
  }
  return out;
}

// Exposed-param persistence, shared by the compose + edit pages. `prefs` is a FLAT by-name value map (NOT keyed per
// workflow, so a value set for one workflow prefills the same-named field on the next). applyParamPrefs writes the map
// onto freshly-rendered fields; collectParamPrefs merges the current field values back into the map (merge, not
// replace, so keys that only exist on other panels/workflows survive). Persisted in the account prefs blob — the
// draft state lives on the account, never localStorage.
function applyParamPrefs(box, prefs) {
  if (!box || box.hidden || !prefs) return;
  for (const inp of box.querySelectorAll("[data-key]")) {
    const k = inp.dataset.key; if (!(k in prefs)) continue;
    if (inp.dataset.ptype === "bool") { inp.checked = prefs[k] === true || prefs[k] === "true"; continue; }
    let v = prefs[k];
    // A flat by-name pref can carry a number from a DIFFERENT model whose grid differs — e.g. a Wan `length` of 81
    // (4n+1) restored onto MiniMax-H3's field, whose valid frames are 17n+5. HTML min/step don't snap an assigned
    // value, so 81 would be shown as-is and is invalid for H3. For a stepped numeric field (step > 1 — the frame
    // grids) snap the restored value to THIS field's own base(min)/step, clamped to its range, rounding UP to match
    // the server's FrameRule.Snap so the shown value is exactly what will render (81 -> 90, not 73).
    if (inp.dataset.ptype === "int" || inp.dataset.ptype === "double") {
      const n = Number(v);
      const step = Number(inp.step), min = inp.min !== "" ? Number(inp.min) : null, max = inp.max !== "" ? Number(inp.max) : null;
      if (!Number.isNaN(n)) {
        let s = n;
        if (step > 1 && min != null) s = min + Math.ceil((n - min) / step - 1e-9) * step;
        if (min != null) s = Math.max(min, s);
        if (max != null) s = Math.min(max, s);
        v = inp.dataset.ptype === "int" ? Math.round(s) : s;
      }
    }
    inp.value = v;
  }
}
function collectParamPrefs(box, prefs) {
  if (!box || !prefs) return;
  Object.assign(prefs, readOverrides(box));
}

// Format a duration in seconds as "45s" or "1m 5s" (minutes-seconds once it exceeds 59s). Shared by the ETA,
// the model pages, and the model dropdowns so every place that shows a render time reads the same.
function fmtDuration(seconds) {
  const s = Math.max(0, Math.round(seconds || 0));
  if (s < 60) return s + "s";
  // Hours matter once this is used for a whole QUEUE rather than one render: a backlog reading "312m" is a number
  // you have to do arithmetic on. A single render never reaches this tier, so nothing else changes shape.
  if (s >= 3600) { const h = Math.floor(s / 3600), hm = Math.round((s % 3600) / 60); return hm ? `${h}h ${hm}m` : `${h}h`; }
  const m = Math.floor(s / 60), r = s % 60;
  return r ? `${m}m ${r}s` : `${m}m`;
}

// --- ETA countdown ------------------------------------------------------------------------------
// Counts down the remaining render time beside the progress bar. `expectedSeconds` is the server's estimate (the
// machine's average of the model's last 10 renders); `startedAtIso` is when the render actually started (queue
// wait excluded). Null/absent expected => hidden (the model has no timing history yet, so no ETA is shown). The
// interval handle lives on the element so the same controller drives the compose bar and the edit bubble.
// `extraSeconds` (optional) is added on top of the live countdown — used by batch runs to show the CUMULATIVE
// remaining time (this image's countdown + the summed estimate of every image still queued behind it), not just
// the in-flight image. Omitted/0 => single-image behaviour, unchanged.
function startEta(el, expectedSeconds, startedAtIso, extraSeconds) {
  if (typeof el === "string") el = $(el);
  if (!el) return;
  stopEta(el);
  extraSeconds = Math.max(0, Number(extraSeconds) || 0);
  if ((!expectedSeconds || expectedSeconds <= 0) && extraSeconds <= 0) { el.hidden = true; el.textContent = ""; return; }
  const parsed = startedAtIso ? Date.parse(startedAtIso) : NaN;
  const base = Number.isNaN(parsed) ? Date.now() : parsed;
  const tick = () => {
    const live = expectedSeconds > 0 ? Math.max(0, expectedSeconds - (Date.now() - base) / 1000) : 0;
    const remaining = live + extraSeconds;
    el.hidden = false;
    el.textContent = remaining >= 1 ? `~${fmtDuration(Math.ceil(remaining))} left` : "finishing…";
  };
  tick();
  el._etaTimer = setInterval(tick, 1000);
}
function stopEta(el) {
  if (typeof el === "string") el = $(el);
  if (!el) return;
  if (el._etaTimer) { clearInterval(el._etaTimer); el._etaTimer = null; }
  el.hidden = true; el.textContent = "";
}

// Transient toast (the #toast element lives in _Layout).
let _toastTimer = null;
function toast(msg) {
  const t = $("toast"); if (!t) return;
  t.textContent = msg; t.classList.remove("hidden");
  clearTimeout(_toastTimer); _toastTimer = setTimeout(() => t.classList.add("hidden"), 1800);
}

// Copy to the clipboard, resolving true/false so the caller can report the real outcome. The async Clipboard API is
// only available in a secure context (and can reject if the document isn't focused), so a failure there falls through
// to the legacy execCommand path rather than being reported as success — this is a capability fallback, not a
// The failure is logged at debug; the boolean return (did the text actually land) is what callers act on.
async function copyText(t) {
  if (navigator.clipboard && navigator.clipboard.writeText) {
    try { await navigator.clipboard.writeText(t); return true; } catch (e) { console.debug("clipboard API copy failed, trying execCommand:", e); }
  }
  try {
    const ta = document.createElement("textarea"); ta.value = t; ta.style.position = "fixed"; ta.style.opacity = "0";
    document.body.appendChild(ta); ta.focus(); ta.select(); const ok = document.execCommand("copy"); ta.remove(); return ok;
  } catch (e) { console.debug("copy failed:", e); return false; }
}

// --- saving an output to disk -------------------------------------------------------------------
// Video outputs (MiniMax-H3's mp4, animated-webp clips) save the canonical mp4 clip (/image/{id}/mp4) as {id}.mp4 —
// audio intact for H3 — while stills save their own bytes under a real extension. `isVideo` is passed when the caller
// already knows (it just rendered the model's <video>); otherwise the clip kind is resolved through /media, the same
// lookup media.js uses to decide whether a library <img> is really a clip. A cross-origin gateway needs a blob save
// (a bare <a download> can't name a cross-origin file), and cache:"no-store" dodges the <img>'s ACAO-less cache
// entry a plain cors fetch would reuse and be blocked on. On any failure the raw view URL opens in a new tab.
const MIME_EXT = { "image/png": ".png", "image/jpeg": ".jpg", "image/webp": ".webp", "image/gif": ".gif", "video/mp4": ".mp4" };
async function mediaIsClip(id) {
  const r = await fetch(`${GATEWAY}/media`, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ids: [id] }) });
  if (!r.ok) throw new Error(`POST ${GATEWAY}/media -> ${r.status}`);
  const kind = (await r.json())[id];
  return kind === "webp" || kind === "mp4";
}
async function saveMedia(idOrRec, isVideo) {
  const id = String(imageId(idOrRec));
  try {
    const vid = (isVideo != null) ? isVideo : await mediaIsClip(id);
    const url = vid ? `${GATEWAY}/image/${encodeURIComponent(id)}/mp4` : viewUrl(id);
    const res = await fetch(url, { cache: "no-store" });
    const blob = await res.blob();
    // A clip always saves as {id}.mp4; a still keeps the id's own extension, else takes one from the served type.
    const name = vid ? id + ".mp4"
      : /\.\w+$/.test(id) ? id : (id || "picture") + (MIME_EXT[(blob.type || "").split(";")[0].trim().toLowerCase()] || ".png");
    const u = URL.createObjectURL(blob);
    const a = document.createElement("a"); a.href = u; a.download = name;
    document.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(u), 1000);
  } catch (e) { console.error("download failed, opening in a tab:", e); window.open(viewUrl(id), "_blank"); }
}

// Gateway error helpers.
function friendlyError(e) {
  if (e instanceof TypeError) return "Can't reach the image server — is it running?";
  return (e && e.message) ? e.message : "Something went wrong. Please try again.";
}
async function gwError(r) {
  try { const j = await r.clone().json(); if (j && j.error) return j.error; } catch (_) {}
  return "The image server returned an error (" + r.status + ").";
}

// --- generation tracking (shared by compose + edit) ---------------------------------------------
// Upload a blob/File to the gateway's input store; returns its image id (used as an edit source / mask / reference).
async function uploadToInput(blobOrFile, filename) {
  const fd = new FormData(); fd.append("image", blobOrFile, filename || "edit_src.png");
  const r = await fetch(`${GATEWAY}/upload`, { method: "POST", body: fd });
  if (!r.ok) throw new Error("Couldn't upload the image (" + r.status + ").");
  return (await r.json()).id;
}
// Progress fraction (0..1) from a ComfyUI /ws frame, or null if the frame carries no progress.
function wsFraction(m) {
  const d = m && m.data;
  if (!d) return null;
  if (m.type === "progress_state" && d.nodes) {
    let running = null;
    for (const k in d.nodes) { const nd = d.nodes[k]; if (nd && nd.state === "running") running = nd; }
    return running ? Math.min(1, (running.value || 0) / (running.max || 1)) : null;
  }
  if (m.type === "progress") return Math.min(1, (d.value || 0) / (d.max || 1));
  return null;
}
// Progress through a whole multi-image run, assembled from the two sources that each know half of it: a poll that
// counts FINISHED images, and ws frames that report the fraction through the CURRENT one. Naively pairing them
// draws the bar backwards at every image boundary — the ws reports the next image the moment it starts rendering,
// seconds before the poll observes the previous one finishing, so the new image's ~0 gets added to the old count.
// Hence `fraction`: one image's fraction only ever climbs, so a DROP is unambiguously the next image starting, and
// the finished count is credited right then instead of waiting for the poll to catch up.
//
// Both bar writers must go through one of these — a bar with two writers computing two different quantities
// bounces. Make one per run; its state is that run's.
function newBatchProgress() {
  let frac = 0, done = 0;
  return {
    fraction(f) { if (f < frac) done++; frac = f; },        // a ws frame's 0..1 through the image rendering now
    finished(n) { if (n > done) done = n; },                // the poll's authoritative finished-image count
    // 0..1 across the run. Floored so a just-started run shows something, capped below 1 so only completion fills it.
    value(total) { return Math.min(0.99, Math.max(0.02, (done + frac) / Math.max(1, total))); },
  };
}
// Poll /result for a single prompt until done/error; the ws is best-effort live progress. Page-agnostic: the
// caller supplies hooks — onFraction(0..1) to drive its bar, onStart(res) when the render begins (to start the
// ETA), and setActiveGen(obj|null) so the page's Cancel button can reach the in-flight job. Resolves the final
// "done" result row; rejects on server error, or with an AbortError DOMException on cancellation — whether this
// caller asked for it or the slot came back "cancelled" (stopped from another device).
function trackPrompt(promptId, hooks) {
  hooks = hooks || {};
  const onFraction = hooks.onFraction || (() => {});
  const onStart = hooks.onStart || (() => {});
  const setActiveGen = hooks.setActiveGen || (() => {});
  return new Promise((resolve, reject) => {
    let settled = false, pollTimer = null, ws = null, etaStarted = false;
    const done = (fn) => {
      if (settled) return; settled = true;
      try { ws && ws.close(); } catch (e) { console.debug("ws close on finish failed:", e); }
      if (pollTimer) clearInterval(pollTimer);
      document.removeEventListener("visibilitychange", onVisible);
      setActiveGen(null); fn();
    };
    async function checkResult() {
      if (settled) return;
      let res;
      try { const r = await fetch(`${GATEWAY}/result/${encodeURIComponent(promptId)}`); if (!r.ok) return; res = await r.json(); } catch (e) { console.debug("result poll failed:", e); return; }
      if (res && res.startedAt && !etaStarted) { etaStarted = true; onStart(res); }
      // Still waiting for the GPU ("queued") or on it ("running") — either way, keep polling.
      if (!res || res.status === "running" || res.status === "queued") return;
      if (res.status === "error") { done(() => reject(new Error(res.error || "The image server reported an error generating the image."))); return; }
      // Stopped on request — terminal, but not a failure. Rejecting as AbortError is what every caller already
      // recognises as "the user cancelled" (they test e.name), so it reports "Cancelled." rather than an error.
      if (res.status === "cancelled") { done(() => reject(new DOMException("cancelled", "AbortError"))); return; }
      if (res.status === "done") { done(() => resolve(res)); return; }
    }
    function openWs() {
      if (settled || ws) return;
      try {
        ws = new WebSocket(gwWs("/ws"));
        ws.onmessage = (ev) => {
          if (typeof ev.data !== "string") return;
          let m; try { m = JSON.parse(ev.data); } catch (e) { console.debug("gen ws non-JSON message:", e); return; }
          const id = m.data && m.data.prompt_id;
          if (id && id !== promptId) return;
          const f = wsFraction(m);
          if (f != null) onFraction(f);
          if (m.type === "executed" || m.type === "execution_error" || m.type === "execution_success") checkResult();
        };
        ws.onclose = () => { ws = null; };
        ws.onerror = (ev) => { console.debug("gen ws error:", ev); try { ws && ws.close(); } catch (e) { console.debug("ws close failed:", e); } ws = null; };
      } catch (e) { console.debug("gen ws open failed:", e); ws = null; }
    }
    const onVisible = () => { if (document.visibilityState === "visible" && !settled) { checkResult(); openWs(); } };
    document.addEventListener("visibilitychange", onVisible);
    setActiveGen({ cancel: async () => { try { await fetch(`${GATEWAY}/interrupt`, { method: "POST" }); } catch (e) { console.debug("interrupt request failed:", e); } done(() => reject(new DOMException("Generation cancelled", "AbortError"))); } });
    pollTimer = setInterval(checkResult, 1500);
    checkResult(); openWs();
  });
}

// --- hold-to-reveal count picker (shared: compose Generate, detail Reload, inpaint Generate) -----
// ONE implementation of "click = make 1, hold = pick how many". The flyout and the custom-amount modal are built
// here in JS and reused across every attached button (only one can be open at a time), so a page gets the picker by
// attaching it to a button — no per-page markup. The button keeps its own submit/click handler; this only reports
// the chosen count.
//   attachCountPicker(btn, { onPick, onHold })
//     onPick(n) — the user picked n from the flyout (or typed it into the custom modal).
//     onHold()  — optional; return true to CLAIM the long-press and suppress the flyout (compose stacks another
//                 render onto the queue instead of offering a count when it's already busy).
//   Returns a handle whose `.opened` is true once a press has become a long-press, so the button's click/submit
//   handler can bail out — otherwise releasing the hold would ALSO fire the plain "make 1" action.
const COUNT_CHOICES = [2, 4, 6, 10];
let cpPop = null, cpAnchor = null, cpPick = null;
function hideCountPop() { if (cpPop) cpPop.hidden = true; }
function ensureCountPop() {
  if (cpPop) return cpPop;
  cpPop = document.createElement("div");
  cpPop.className = "count-pop"; cpPop.hidden = true;
  cpPop.setAttribute("role", "menu"); cpPop.setAttribute("aria-label", "How many to make");
  // Fixed-positioned and anchored to whichever button opened it, so it doesn't depend on the button's parent
  // being a positioned .gen-wrap (the inpaint Generate button isn't in one).
  cpPop.style.position = "fixed"; cpPop.style.zIndex = "9999"; cpPop.style.right = "auto"; cpPop.style.bottom = "auto";
  for (const n of COUNT_CHOICES) {
    const b = document.createElement("button");
    b.type = "button"; b.dataset.n = String(n); b.textContent = String(n);
    cpPop.appendChild(b);
  }
  const cb = document.createElement("button");
  cb.type = "button"; cb.dataset.custom = "1"; cb.title = "Custom amount"; cb.setAttribute("aria-label", "Custom amount"); cb.textContent = "✎";
  cpPop.appendChild(cb);
  document.body.appendChild(cpPop);
  cpPop.addEventListener("click", e => {
    const pick = cpPick;
    if (e.target.closest("button[data-custom]")) { hideCountPop(); openCustomCount(n => pick && pick(n)); return; }
    const b = e.target.closest("button[data-n]"); if (!b) return;
    hideCountPop(); if (pick) pick(parseInt(b.dataset.n, 10) || 1);
  });
  document.addEventListener("pointerdown", e => {
    if (cpPop && !cpPop.hidden && e.target !== cpAnchor && !cpPop.contains(e.target)) hideCountPop();
  }, true);
  return cpPop;
}
function showCountPop(btn, pick) {
  const pop = ensureCountPop();
  cpAnchor = btn; cpPick = pick; pop.hidden = false;
  const r = btn.getBoundingClientRect();
  pop.style.left = Math.max(8, Math.min(r.left, window.innerWidth - pop.offsetWidth - 8)) + "px";
  pop.style.top = Math.max(8, r.top - pop.offsetHeight - 8) + "px";
}
// The app's ONE long-press dwell. `onHold` fires once the press has been held; the returned handle's `opened` flag
// says a press became a hold, which the button's own click handler must check so the short-press action doesn't
// fire on top of it. (A hold still produces a click on release — there is no browser event that says "not a tap".)
const LONG_PRESS_MS = 450;
function attachLongPress(btn, onHold) {
  const handle = { opened: false };
  if (!btn) return handle;
  let timer = null;
  btn.addEventListener("pointerdown", () => {
    handle.opened = false;
    clearTimeout(timer);
    timer = setTimeout(() => { handle.opened = true; onHold(); }, LONG_PRESS_MS);
  });
  ["pointerup", "pointerleave", "pointercancel"].forEach(ev => btn.addEventListener(ev, () => clearTimeout(timer)));
  return handle;
}
function attachCountPicker(btn, opts) {
  const onPick = (opts && opts.onPick) || (() => {});
  const onHold = (opts && opts.onHold) || (() => false);
  const handle = attachLongPress(btn, () => {
    if (!onHold()) showCountPop(btn, n => { handle.opened = false; onPick(n); });
  });
  return handle;
}

// The custom-amount modal behind the flyout's ✎ — also built here, also shared. openCustomCount(cb) reports the
// typed amount to cb; nothing is submitted if the user cancels.
let ccModal = null, ccInput = null, ccCb = null;
function ccClamp() { let n = parseInt(ccInput.value, 10); if (isNaN(n)) n = 1; n = Math.max(1, n); ccInput.value = n; return n; }
function closeCustomCount() { if (ccModal) ccModal.classList.add("hidden"); ccCb = null; }
function ensureCustomCount() {
  if (ccModal) return;
  ccModal = document.createElement("div");
  ccModal.className = "modal-overlay hidden";
  ccModal.innerHTML = '<div class="modal-card"><h3>How many to make?</h3>'
    + '<div class="num-row"><button type="button" data-cc="-" aria-label="Fewer">−</button>'
    + '<input class="fld-input" type="number" min="1" step="1" value="12" inputmode="numeric" />'
    + '<button type="button" data-cc="+" aria-label="More">+</button></div>'
    + '<div class="modal-actions"><button type="button" class="link-btn" data-cc="cancel">Cancel</button>'
    + '<button type="button" class="primary-btn" data-cc="go">Generate</button></div></div>';
  document.body.appendChild(ccModal);
  ccInput = ccModal.querySelector("input");
  const go = () => { const n = ccClamp(); const cb = ccCb; closeCustomCount(); if (cb) cb(n); };
  ccModal.addEventListener("click", e => {
    if (e.target === ccModal) { closeCustomCount(); return; }
    const b = e.target.closest("button[data-cc]"); if (!b) return;
    const a = b.dataset.cc;
    if (a === "-") ccInput.value = Math.max(1, (parseInt(ccInput.value, 10) || 1) - 1);
    else if (a === "+") ccInput.value = (parseInt(ccInput.value, 10) || 0) + 1;
    else if (a === "go") go();
    else closeCustomCount();
  });
  ccInput.addEventListener("keydown", e => {
    if (e.key === "Enter") { e.preventDefault(); go(); }
    else if (e.key === "Escape") { e.preventDefault(); closeCustomCount(); }
  });
}
function openCustomCount(cb) {
  ensureCustomCount();
  ccCb = cb;
  ccModal.classList.remove("hidden");
  setTimeout(() => { ccInput.focus(); ccInput.select(); }, 30);
}

// --- same-origin /api data client (history + bookmarks) -----------------------------------------
const Api = {
  async json(path, opts) {
    const r = await fetch(path, { credentials: "same-origin", ...opts });
    if (!r.ok) throw new Error("API " + path + " -> " + r.status);
    return r.json();
  },
  send(path, method, body) {
    return fetch(path, {
      method, credentials: "same-origin",
      headers: body !== undefined ? { "Content-Type": "application/json" } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  },
};
// NOTE: there is intentionally no client history writer. History is written exactly once, server-side, by the
// JobQueue worker the moment an image is produced. The browser only READS history (queryHistory/recents) and DELETEs.
// Register a submitted gateway job (legacy/vestigial; the worker persists regardless of the browser now).
const postPending = rec => Api.send("/api/pending", "POST", rec);
const deleteHistory = id => Api.send("/api/history?id=" + encodeURIComponent(id), "DELETE");
// A history page. POST with the query in the BODY — `search` is prompt content by another name and `tag` is a tag
// token, and a URL carrying either is written into the browser's own history and address-bar autocomplete on the
// user's machine, where nothing server-side can clean it up. Every history read goes through here so no caller can
// put one in a URL by accident.
async function queryHistory(query) {
  const r = await Api.send("/api/history/query", "POST", query);
  if (!r.ok) throw new Error("API /api/history/query -> " + r.status);
  return r.json();
}
const fetchBookmarks = () => Api.json("/api/bookmarks");
const postToken = (name, kind) => Api.send("/api/bookmarks/tokens", "POST", { name, kind });
const deleteToken = (name, kind) => Api.send(`/api/bookmarks/tokens?name=${encodeURIComponent(name)}&kind=${encodeURIComponent(kind)}`, "DELETE");
const postTokenPin = (name, kind, pinned) => Api.send("/api/bookmarks/tokens/pin", "POST", { name, kind, pinned });
const postImageBookmark = rec => Api.send("/api/bookmarks/images", "POST", rec);
const deleteImageBookmark = id => Api.send("/api/bookmarks/images?id=" + encodeURIComponent(id), "DELETE");
// Bookmark categories (the long-press dialog). fetchCategories takes a query string ("scope=token&name=&kind=" or
// "scope=image&id="); the POSTs set a bookmark's whole category set, creating the bookmark if it isn't saved yet.
const fetchCategories = q => Api.json("/api/bookmarks/categories?" + q);
const postTokenCategories = (name, kind, categories) => Api.send("/api/bookmarks/tokens/categories", "POST", { name, kind, categories });
const postImageCategories = (image, categories) => Api.send("/api/bookmarks/images/categories", "POST", { image, categories });
// Per-artist display image (what represents the artist on the bookmarks/artist pages).
const postArtistDisplay = (artist, id) => Api.send("/api/artist/display", "POST", { artist, id });
const deleteArtistDisplay = artist => Api.send("/api/artist/display?artist=" + encodeURIComponent(artist), "DELETE");
// Per-LoRA cover image (what represents a LoRA in the composer's picker). Exposed on window so detail.js (loaded on
// pages that may not share module scope) can reach it.
const postLoraDisplay = (lora, id) => Api.send("/api/lora/display", "POST", { lora, id });
window.postLoraDisplay = postLoraDisplay;
// Per-tag portrait image (what represents a tag on the bookmarks page). Exposed on window for detail.js.
const postTagDisplay = (tag, id) => Api.send("/api/tag/display", "POST", { tag, id });
window.postTagDisplay = postTagDisplay;
const deleteTagDisplay = tag => Api.send("/api/tag/display?tag=" + encodeURIComponent(tag), "DELETE");
// Per-LoRA trigger-word override + auto-attach (the LoRA manager page).
const postLoraSettings = (lora, triggers, autoAttach) => Api.send("/api/lora/settings", "POST", { lora, triggers, autoAttach });

// LoRA CivitAI metadata (name/triggers/preview) is populated in the background: a surface renders stubs at once, then
// polls until each file is `ready`. The preview media is served from THIS box (never hotlinked); it may be a clip, so
// callers check previewVideo. All JSON in/out — the media is only ever an <img>/<video> src, never fetched as HTML.
const loraPreviewUrl = (name, bust) =>
  `${GATEWAY}/lora-preview?name=${encodeURIComponent(name)}` + (bust ? `&v=${encodeURIComponent(bust)}` : "");
const postLoraMeta = names => Api.send("/forge/loras/meta", "POST", { names });          // -> { items:[…], pending }
const postLoraRefresh = names => Api.send("/forge/loras/refresh", "POST", { names: names || [] });
window.loraPreviewUrl = loraPreviewUrl;
window.postLoraRefresh = postLoraRefresh;

// Poll /forge/loras/meta until nothing in `names` is still populating. onUpdate(map) fires each round with a
// name -> { displayName, triggers, autoAttach, ready, hasPreview, previewVideo } map. Stops when the server reports
// nothing pending (or a round fails — the next page visit resumes it). The interval is the polling cadence the feature
// calls for, not a deadline on work; returns a canceller. Exposed for the picker/manager/composer.
function pollLoraMeta(names, onUpdate, intervalMs) {
  names = (names || []).filter(Boolean);
  if (!names.length) return () => {};
  let stopped = false;
  const gap = intervalMs || 1500;
  async function tick() {
    if (stopped) return;
    let data;
    try { const r = await postLoraMeta(names); if (!r.ok) throw new Error(`the server answered ${r.status}`); data = await r.json(); }
    catch (e) { console.debug("lora meta poll failed:", e); return; }
    if (stopped) return;
    const map = {};
    (data.items || []).forEach(it => { map[it.name] = it; });
    try { onUpdate(map); } catch (e) { console.error("lora meta onUpdate callback threw:", e); }
    if (data.pending) setTimeout(tick, gap);
  }
  setTimeout(tick, gap);
  return () => { stopped = true; };
}
window.pollLoraMeta = pollLoraMeta;
// Per-model banned tags/artists (excluded from auto-gen for that model). The generate path does NOT read these — the
// worker resolves the user's bans server-side — so this is purely the settings manager's view of them.
const fetchAllBans = () => Api.json("/api/bans/all");
const postBan = (modelId, name, kind) => Api.send("/api/bans", "POST", { modelId, name, kind });
const deleteBan = (modelId, name, kind) => Api.send(`/api/bans?modelId=${encodeURIComponent(modelId)}&name=${encodeURIComponent(name)}&kind=${encodeURIComponent(kind)}`, "DELETE");

// Account-level settings (per user, follows the account across devices). Read-only: every writable preference has
// its own PUT route below, so one autosave can't clobber another's.
const fetchSettings = () => Api.json("/api/settings");
// Composer state (draft prompt, model, aspect, random-artist toggle, random-prompt temperature) as an opaque JSON
// string, on its own route.
const saveComposerPrefs = json => Api.send("/api/settings/composer", "PUT", { composerPrefs: json });
// The editor's state (active mode, selected workflow(s), inpaint workflow, flat param overrides, brush size) as an
// opaque JSON string, on its own route — the edit-page analogue of saveComposerPrefs (per user, across devices).
const saveEditPrefs = json => Api.send("/api/settings/edit-prefs", "PUT", { editPrefs: json });
// The bookmarks page's folded sections as an opaque JSON string, on its own route. Client state belongs on the
// account, never in localStorage: a fold set that lives in one browser is invisible to every other device.
const saveBookmarkPrefs = json => Api.send("/api/settings/bookmarks", "PUT", { bookmarkPrefs: json });
// Whether autocomplete pins the user's matching bookmarks to the top — its own account boolean, on its own route.
const savePinBookmarks = on => Api.send("/api/settings/pin-bookmarks", "PUT", { pinBookmarks: on });
// Favorited workflow ids (JSON array string) + custom per-workflow tags (JSON map string, encrypted server-side).
// Favourites, hidden workflows and per-workflow labels are RELATIONS server-side (rows, not blobs), so these send
// and receive real arrays/maps.
const saveFavoriteWorkflows = ids => Api.send("/api/settings/favorites", "PUT", { favoriteWorkflowIds: ids });
const saveWorkflowTags = map => Api.send("/api/settings/workflow-tags", "PUT", { customWorkflowTags: map });
const saveHiddenWorkflows = ids => Api.send("/api/settings/hidden", "PUT", { hiddenWorkflowIds: ids });
// Workflows hidden from the API workflow list — a separate per-user set from the UI-picker one above.
const saveHiddenApiWorkflows = ids => Api.send("/api/settings/hidden-api", "PUT", { hiddenApiWorkflowIds: ids });
// NOTE: no client writes the ACCOUNT-level generation mask any more. The mask is a per-generation choice now — the
// chip row under the composer's Random prompt slider — so it rides in the generate/enqueue body as `tagTypes` and in
// the composer prefs draft. The stored account value survives as the server-side fallback for a caller that sends no
// mask (the MCP), and PUT /api/settings/generation-tag-types is still the way to set THAT.
// The workflow relations arrive as real JSON now — an array of ids, and a map of id -> labels — because that is what
// they are in the database. There is nothing left to parse; the shape is still CHECKED, because a surface that
// silently accepted the wrong shape would read as "you have favourited nothing" and then save that back.
function parseFavs(s) {
  const a = s.favoriteWorkflowIds ?? [];
  if (!Array.isArray(a)) throw new Error("favoriteWorkflowIds is not an array");
  return a;
}
function parseHidden(s) {
  const a = s.hiddenWorkflowIds ?? [];
  if (!Array.isArray(a)) throw new Error("hiddenWorkflowIds is not an array");
  return a;
}
function parseHiddenApi(s) {
  const a = s.hiddenApiWorkflowIds ?? [];
  if (!Array.isArray(a)) throw new Error("hiddenApiWorkflowIds is not an array");
  return a;
}
function parseWorkflowTags(s) {
  const m = s.customWorkflowTags ?? {};
  if (!m || typeof m !== "object" || Array.isArray(m)) throw new Error("customWorkflowTags is not an object");
  return m;
}

// The per-user workflow preferences (favorites / hidden / custom tags), loaded as ONE unit with an explicit ok flag.
//
// `ok:false` means UNKNOWN, not empty, and that distinction is the whole point. A caller that wrote
// `fetchSettings().catch(() => ({}))` and fed the empty object to parsers that also answer empty on failure would let
// a single failed GET produce a perfectly plausible "this user has no favorites". That value is not inert: the pages
// hold it in memory and PUT the entire set back on the next star or hide, which would quietly replace the user's real
// favorites with nothing. Pages that write MUST refuse to when ok is false; pages that only read may show the
// un-personalized list.
// `settings` is the whole settings response, or null when the GET itself failed — callers that also need a different
// blob out of it (the edit page's editPrefs) read it from here rather than issuing a second GET, and can tell a
// failed fetch from blobs that arrived but would not parse.
async function loadWorkflowPrefs() {
  const empty = { favs: new Set(), hidden: new Set(), hiddenApi: new Set(), tags: {} };
  let s;
  try {
    s = await fetchSettings();
  } catch (e) {
    console.error("Workflow preferences could not be loaded:", e);
    return { ok: false, settings: null, ...empty };
  }
  try {
    return { ok: true, settings: s, favs: new Set(parseFavs(s)), hidden: new Set(parseHidden(s)), hiddenApi: new Set(parseHiddenApi(s)), tags: parseWorkflowTags(s) };
  } catch (e) {
    // The response arrived; these three blobs are what is unreadable. Other blobs in it are still fine, so it is
    // handed back — but ok stays false, so nothing writes over the ones that failed.
    console.error("Stored workflow preferences are not readable:", e);
    return { ok: false, settings: s, ...empty };
  }
}
