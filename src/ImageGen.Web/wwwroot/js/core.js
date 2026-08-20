// Shared client foundation, loaded before every page script. window.GATEWAY is injected by _Layout.

const $ = id => document.getElementById(id);
const imageId = r => (typeof r === "string") ? r : ((r && (r.id != null ? r.id : r.filename)) || "");
const viewUrl = r => `${GATEWAY}/image/${encodeURIComponent(imageId(r))}`;
// Small JPEG preview for grid/list/recents cards (full image stays at viewUrl). Tens of KB vs a multi-MB PNG.
const THUMB_W = 512;
const thumbUrl = (r, w) => `${viewUrl(r)}?w=${w || THUMB_W}`;

// One image-grid card contract for gallery, artist, workflow-detail and recents. Contexts choose whether their
// metadata row needs the model, date, or both; structure, escaping, thumbnail URL and unviewed semantics stay shared.
function buildImageCard(record, options) {
  const r = record || {}, opts = options || {};
  const id = imageId(r);
  const a = document.createElement("a");
  a.className = "imgcard";
  if (r.viewed !== true) a.classList.add("unviewed");
  a.href = "/image/" + encodeURIComponent(id);

  const imageWrap = document.createElement("div"); imageWrap.className = "img";
  const image = document.createElement("img"); image.setAttribute("data-src", thumbUrl(r)); image.alt = r.prompt || "";
  imageWrap.appendChild(image);

  const meta = document.createElement("div"); meta.className = "meta";
  const prompt = document.createElement("div"); prompt.className = "p"; prompt.textContent = r.prompt || "";
  const row = document.createElement("div"); row.className = "row";
  if (opts.showModel !== false) {
    const model = document.createElement("span"); model.className = "tag"; model.textContent = r.model || "";
    row.appendChild(model);
  }
  if (opts.showDate) {
    const date = document.createElement("span"); date.className = "seed";
    const time = document.createElement("time"); time.dataset.ts = String(Number(r.ts) || 0);
    date.appendChild(time); row.appendChild(date);
  }
  meta.append(prompt, row); a.append(imageWrap, meta);
  fillImageCardDates(a);
  return a;
}

function formatImageCardDate(ts) {
  try { return new Date(ts).toLocaleDateString(undefined, { month: "short", day: "numeric" }); }
  catch (e) { console.debug("date format failed:", e); return ""; }
}

// Also formats the server-rendered first page, whose <time> nodes use the same card contract.
function fillImageCardDates(root) {
  (root || document).querySelectorAll("time[data-ts]").forEach(t => {
    if (!t.textContent) t.textContent = formatImageCardDate(Number(t.dataset.ts));
  });
}
// Absolute ws/wss URL for a gateway path — works whether GATEWAY is an absolute http(s) origin or, now that the
// gateway is embedded same-origin, a relative path like "/forge" (relative WebSocket urls aren't universally ok).
const gwWs = path => /^https?:/i.test(GATEWAY)
  ? GATEWAY.replace(/^http/i, "ws") + path
  : (location.protocol === "https:" ? "wss:" : "ws:") + "//" + location.host + GATEWAY + path;
const escapeHtml = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
// The <option> list for a model-slot picker, shared by every place one is drawn (the workflow library dialog, the
// models page, and a workflow's detail page): a "— not set —" clear option, then the recognised candidates A–Z, then
// every other file of the slot's kind A–Z. A slot may be bound to ANY file of its kind — the patterns pre-fill, they
// do not restrict. The caller wraps this in its own <select> so each surface keeps its own chrome.
function slotOptionsHtml(s) {
  const byName = (a, b) => a.localeCompare(b, undefined, { sensitivity: "base" });
  const candidates = (s.candidates || []).slice().sort(byName);
  const rest = (s.available || []).filter(f => !(s.candidates || []).includes(f)).sort(byName);
  const opt = (f, tag) => {
    const label = `${f}${tag || ""}`;
    return `<option value="${escapeHtml(f)}" label="${escapeHtml(label)}"${f === s.boundFile ? " selected" : ""}>${escapeHtml(label)}</option>`;
  };
  return `<option value="" label="— not set —">— not set —</option>`
    + candidates.map(f => opt(f, " (recognised)")).join("")
    + rest.map(f => opt(f, "")).join("");
}
// Mirror of PromptMarkers.Key. '!' is the INERT TAG marker (a tag the tag predictor is not conditioned on) and '~'
// the GUIDE TAG marker (seen only by the predictor, never rendered); both are ordinary tags to every client surface
// that matches on a key, so they must strip here too or a "!pig" segment won't match its own chip, bookmark or ban.
// Weight/emphasis is identity-invisible (issue #133): 'tag', '(tag)', '(tag:1.2)' and '[tag]' are the same tag, so the
// wrapper is peeled — before AND after the marker, so it may sit either side ('#(tag:1.2)', '(#tag:1.2)') — down to the
// base tag before the marker/whitespace/case steps. This must stay in lockstep with PromptMarkers.StripWeight (C#).
function stripWeight(s) {
  s = String(s == null ? "" : s).trim();
  // Peel one unescaped wrapper that spans the WHOLE segment per turn: '(...)' carries an optional trailing ':weight',
  // '[...]' is bare de-emphasis. A booru tag that natively holds parens ('hatsune_miku_(vocaloid)', '(a)_(b)') is not
  // wrapped and survives; an escaped '\(' is a literal char, unescaped at the end. Bounded loop handles nesting.
  for (let guard = 0; guard < 32; guard++) {
    if (s.length < 2) break;
    const open = s[0], close = open === "(" ? ")" : open === "[" ? "]" : "";
    if (!close) break;
    let depth = 0, end = -1;
    for (let i = 0; i < s.length; i++) {
      const c = s[i];
      if (c === "\\") { i++; continue; }          // escaped char — literal, not a bracket
      if (c === open) depth++;
      else if (c === close && --depth === 0) { end = i; break; }
    }
    if (end !== s.length - 1) break;              // no wrapper, or it closes before the segment's end
    let inner = s.slice(1, end).trim();
    if (open === "(") inner = inner.replace(/:\s*-?\d+(?:\.\d+)?\s*$/, "").trim();   // drop a trailing ':weight'
    s = inner;
  }
  return s.includes("\\") ? s.replace(/\\([()\[\]])/g, "$1") : s;   // unescape literal brackets
}
const normToken = s => {
  let t = stripWeight(s);
  if (/^[#@!~]/.test(t)) t = stripWeight(t.slice(1));   // drop exactly one marker, then peel a wrapper it sat outside of
  return t.trim().replace(/\s+/g, "_").toLowerCase();
};


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
// autocomplete suggestion carries. Only the notable categories get one; general/deprecated/unknown stay neutral.
// Artist (1) is intentionally neutral: the '#' tag popup lists tags only — artists complete on the separate '@'
// marker — so an artist suggestion never appears here to be colored.
function tagCategoryClass(type) {
  switch (type) {
    case 3: return "cat-copyright";
    case 4: return "cat-character";
    case 5: return "cat-meta";
    default: return "";
  }
}

// How an exhaustive template fans out, WITHOUT building the strings: the count of {{…|…}} sets and the product of their
// option counts (combos = 1 when there are none). Used to warn before a multiplicative batch and to route the fan-out.
// Actual resolution is server-side for generation and edits; this is only the composer's pre-submit warning.
function explodeInfo(text) {
  const groups = String(text == null ? "" : text).match(/\{\{[^{}]*\|[^{}]*\}\}/g) || [];
  const combos = groups.reduce((n, g) => n * g.slice(2, -2).split("|").length, 1);
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
//
// Per-user visibility (#191) applies BEFORE the intersection: each model's effective set is its shipped exposed
// params minus the ones this user hid, plus the shipped hidden-but-revealable ones this user revealed — so a
// revealed param obeys the same every-model rule an exposed one does.
let PARAM_VIS = {};   // configId -> paramKey -> bool (true = show, false = hide; absent = shipped default)
// The seconds control is the length param renamed by the server's frames→seconds projection; the visibility pref is
// keyed by the config param it rides on.
const paramVisKey = p => p.key === "duration_seconds" ? "length" : p.key;
function effectiveParams(m) {
  const vis = PARAM_VIS[m.id] || {};
  return [
    ...(m.exposedParams || []).filter(p => vis[paramVisKey(p)] !== false),
    ...(m.hiddenParams || []).filter(p => vis[paramVisKey(p)] === true),
  ];
}
function sharedExposedParams(models) {
  const lists = models.map(m => (m && effectiveParams(m)) || []);
  if (!lists.length) return [];
  const rangeKey = p => JSON.stringify(p.rangeOverride || null);
  return lists[0]
    .filter(p => lists.every(l => l.some(q => q.key === p.key && q.type === p.type)))
    .map(p => {
      const peers = lists.map(l => l.find(q => q.key === p.key && q.type === p.type));
      // A multi-model field may offer the alternate range only when every selected model declares the same one.
      // Sending the first model's wider range to a stricter peer would turn an explicit opt-in into an invalid batch.
      return peers.every(q => rangeKey(q) === rangeKey(p)) ? p : { ...p, rangeOverride: null };
    });
}
function renderParamFields(box, modelOrModels, extraParams) {
  if (!box) return;
  box.innerHTML = "";
  const models = Array.isArray(modelOrModels) ? modelOrModels.filter(Boolean) : (modelOrModels ? [modelOrModels] : []);
  const ps = sharedExposedParams(models).filter(p => ["int", "double", "enum", "string", "multiline", "bool"].includes(p.type));
  // Caller-forced params (the compose page's custom-size width/height): appended when the visibility overlay didn't
  // already produce them, so they render through the same control path as any exposed param.
  for (const p of (extraParams || [])) if (!ps.some(q => q.key === p.key)) ps.push(p);
  if (!ps.length) { box.hidden = true; box.classList.add("hidden"); return; }
  box.hidden = false; box.classList.remove("hidden");
  for (const p of ps) {
    const wrap = document.createElement("div"); wrap.className = "mp-field";
    const span = document.createElement("label"); span.className = "fld-label"; span.textContent = p.label || p.key;
    const fieldId = `${box.id || "params"}-${String(p.key).replace(/[^a-zA-Z0-9_-]/g, "-")}`;
    span.htmlFor = fieldId;
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
      // Configurations carry seed=0 as their typed fallback, but an untouched/revealed seed control must be blank:
      // omission is what asks the server to choose and persist a fresh seed. Explicitly typing 0 still submits 0.
      inp.value = (p.key === "seed" && Number(p.value) === 0) ? "" : (p.value != null ? p.value : "");
    } else if (p.type === "bool") {
      inp = document.createElement("input"); inp.type = "checkbox";
      inp.checked = (p.value === true || p.value === "true");
      wrap.classList.add("mp-field-bool");
    } else if (p.type === "multiline") {
      inp = document.createElement("textarea"); inp.rows = 8; inp.value = (p.value != null ? p.value : "");
      wrap.classList.add("mp-field-multiline");
    } else {
      inp = document.createElement("input"); inp.type = "text"; inp.value = (p.value != null ? p.value : "");
    }
    inp.className = "fld-input";   // the field chrome rides on the input, not on the container it happens to be in
    inp.id = fieldId;
    inp.dataset.key = p.key; inp.dataset.ptype = p.type;
    wrap.appendChild(span); wrap.appendChild(inp);

    if ((p.type === "int" || p.type === "double") && p.rangeOverride) {
      wrap.classList.add("mp-field-range");
      const alternate = p.rangeOverride;
      const normalMin = p.min != null ? Number(p.min) : null;
      const normalMax = p.max != null ? Number(p.max) : null;
      const alternateMin = alternate.min != null ? Number(alternate.min) : normalMin;
      const alternateMax = alternate.max != null ? Number(alternate.max) : normalMax;
      const option = document.createElement("label"); option.className = "mp-range-override";
      const toggle = document.createElement("input"); toggle.type = "checkbox"; toggle.className = "mp-range-toggle";
      const optionText = document.createElement("span"); optionText.textContent = alternate.label;
      option.append(toggle, optionText);
      const warning = document.createElement("p"); warning.className = "mp-range-warning";
      warning.textContent = alternate.warning; warning.hidden = true;

      const setBound = (name, value) => value == null ? inp.removeAttribute(name) : inp.setAttribute(name, value);
      const outsideNormalRange = () => {
        const n = Number(inp.value);
        return inp.value !== "" && !Number.isNaN(n)
          && ((normalMin != null && n < normalMin) || (normalMax != null && n > normalMax));
      };
      const applyAlternateRange = active => {
        toggle.checked = active;
        setBound("min", active ? alternateMin : normalMin);
        setBound("max", active ? alternateMax : normalMax);
        warning.hidden = !active || !outsideNormalRange();
      };
      toggle.addEventListener("change", () => {
        applyAlternateRange(toggle.checked);
        if (!toggle.checked && inp.value !== "") {
          let n = Number(inp.value);
          if (normalMin != null) n = Math.max(normalMin, n);
          if (normalMax != null) n = Math.min(normalMax, n);
          inp.value = n;
        }
      });
      inp.addEventListener("input", () => { warning.hidden = !toggle.checked || !outsideNormalRange(); });
      const insideAlternateRange = n => (alternateMin == null || n >= alternateMin)
        && (alternateMax == null || n <= alternateMax);
      inp.rangeOverrideControl = {
        normalMin, normalMax, alternateMin, alternateMax,
        apply: applyAlternateRange,
        active: () => toggle.checked,
      };
      wrap.append(option, warning);
      const initial = Number(inp.value);
      if (outsideNormalRange() && !Number.isNaN(initial) && insideAlternateRange(initial)) applyAlternateRange(true);
    }

    box.appendChild(wrap);
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
      const range = inp.rangeOverrideControl;
      if (range && !Number.isNaN(n)) {
        const outsideNormal = (range.normalMin != null && n < range.normalMin)
          || (range.normalMax != null && n > range.normalMax);
        const insideAlternate = (range.alternateMin == null || n >= range.alternateMin)
          && (range.alternateMax == null || n <= range.alternateMax);
        if (outsideNormal && insideAlternate) range.apply(true);
      }
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
    if (inp.rangeOverrideControl?.active()) inp.rangeOverrideControl.apply(true);
  }
}
function collectParamPrefs(box, prefs) {
  if (!box || !prefs) return;
  // Clearing a pinned seed must stay cleared across a panel re-render. readOverrides correctly omits blanks, but its
  // merge semantics would otherwise leave the previous seed behind in the flat preference map.
  for (const inp of box.querySelectorAll('[data-key="seed"]')) if (inp.value === "") delete prefs.seed;
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

// --- drag-and-drop upload -----------------------------------------------------------------------
// Turn `zone` into a file drop target: highlight it (`.drop-hover`) while a file is dragged over, and hand the
// dropped File list to `onFiles`. Only real file drags act — dragging text or a browser image carries no
// "Files" in dataTransfer.types, so those pass through untouched. The same named upload handler the surface's
// hidden <input> calls is passed as `onFiles`, so a drop reuses the exact click-upload path.
function dragCarriesFiles(e) {
  return !!(e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files"));
}
function attachDropUpload(zone, onFiles) {
  if (!zone) return;
  const over = e => { if (!dragCarriesFiles(e)) return; e.preventDefault(); zone.classList.add("drop-hover"); };
  zone.addEventListener("dragenter", over);
  zone.addEventListener("dragover", over);
  // Ignore leaves onto a child (dragleave fires crossing into descendants too) — only drop the highlight when the
  // cursor actually leaves the zone.
  zone.addEventListener("dragleave", e => { if (!zone.contains(e.relatedTarget)) zone.classList.remove("drop-hover"); });
  zone.addEventListener("drop", e => {
    zone.classList.remove("drop-hover");
    if (!dragCarriesFiles(e)) return;
    e.preventDefault();
    const files = Array.from(e.dataTransfer.files || []);
    if (files.length) onFiles(files);
  });
}
// A file dropped OUTSIDE any zone makes the browser navigate to/open it, blowing the page away. Swallow the
// document-level default for file drags so a stray drop does nothing. Zone drops preventDefault themselves; this is
// the safety net for everywhere else. Install once per page.
function preventStrayFileDrops() {
  const swallow = e => { if (dragCarriesFiles(e)) e.preventDefault(); };
  document.addEventListener("dragover", swallow);
  document.addEventListener("drop", swallow);
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

// ComfyUI's legacy preview-image WebSocket payload: two big-endian uint32 values (binary event type, then image
// format), followed by the encoded image. The app deliberately does not negotiate preview metadata yet, so event 1 is
// the only preview shape the pinned renderer sends; unknown binary events fail closed here.
const COMFY_PREVIEW_HEADER_BYTES = 8;
const COMFY_PREVIEW_IMAGE_EVENT = 1;
const COMFY_PREVIEW_JPEG = 1;
const COMFY_PREVIEW_PNG = 2;

// Owns the temporary object URL and DOM for one trackJobBatch invocation. A durable onSlot result always clears this
// card before the page renders its real image/actions; cancellation, failure and no-change completion clear it too.
function livePreviewPainter(target) {
  let revision = 0, url = null, card = null, image = null, prior = null;
  const clear = () => {
    revision++;
    if (url) URL.revokeObjectURL(url);
    url = null;
    // Keep the last durable result underneath this temporary card. If the new render fails/cancels, clearing the
    // preview restores it; if a slot succeeds, onSlot replaces it immediately after this call.
    if (card && card.parentNode === target) target.replaceChildren(...(prior || []));
    card = null; image = null; prior = null;
  };
  const show = async payload => {
    if (!target || payload == null) return;
    const mine = ++revision;
    const blob = payload instanceof Blob ? payload : new Blob([payload]);
    if (blob.size <= COMFY_PREVIEW_HEADER_BYTES) return;

    let header;
    try { header = await blob.slice(0, COMFY_PREVIEW_HEADER_BYTES).arrayBuffer(); }
    catch (e) { console.debug("preview header could not be read:", e); return; }
    if (mine !== revision) return;   // a newer sampler frame decoded first
    const view = new DataView(header);
    if (view.getUint32(0) !== COMFY_PREVIEW_IMAGE_EVENT) return;
    const format = view.getUint32(4);
    const type = format === COMFY_PREVIEW_JPEG ? "image/jpeg" : format === COMFY_PREVIEW_PNG ? "image/png" : null;
    if (!type) return;

    const nextUrl = URL.createObjectURL(blob.slice(COMFY_PREVIEW_HEADER_BYTES, blob.size, type));
    if (mine !== revision) { URL.revokeObjectURL(nextUrl); return; }
    if (!card) {
      card = document.createElement("div"); card.className = "result-card live-preview";
      image = document.createElement("img"); image.alt = "Live generation preview";
      const badge = document.createElement("div"); badge.className = "live-preview-badge"; badge.textContent = "Live preview";
      prior = [...target.childNodes]; card.append(image, badge); target.replaceChildren(card);
    }
    const previous = url; url = nextUrl; image.src = nextUrl;
    if (previous) URL.revokeObjectURL(previous);
  };
  return { show, clear };
}

// The ONE render-progress/preview engine (#145), shared by every page so status·ETA·bar·cancel·preview behave
// identically and can never drift again. Tracks ONE multi-slot job to completion: opens /ws for live fraction, polls
// /jobs, records each finished slot (diffing on slot id, skipping `changed === false` no-ops), drives the bar via
// newBatchProgress, restarts the ETA when the running slot changes, and finishes when the job leaves the active feed
// (then reads /job/{id} for stragglers). Resolves the number of images actually made.
//
// The page supplies only what is page-specific, via `o`:
//   onProgress(fraction)                paint the bar (0..1)
//   onSlot(slot)                        render one finished slot into the preview box
//   previewTarget                       optional element where live ComfyUI preview frames render temporarily
//   onRunning(slot|null, job)           optional: called each poll with the running slot (e.g. show its model)
//   eta                                 optional .eta element to drive on running-slot change
//   activeStatus(recorded, total, job, activeJobs)
//                                       optional -> status string while running (null = leave). The current job and
//                                       full active feed let a surface show both batch-local and queue-wide progress.
//   finalStatus(made, total, cancelled, errors) -> status string on finish (null = leave); `errors` is the array of
//                                       real server error messages from slots that FAILED (empty when none failed)
//   setStatus(text)                     write the page's status line
//   onCancelHandle(handle)              receive { cancel } so the page's Cancel button can reach this job
//   onSettle(made)                      optional page cleanup after finish (before resolve)
function trackJobBatch(jobId, o) {
  const N = o.total || 1;
  return new Promise(resolve => {
    let settled = false, timer = null, ws = null, runningId = null, frameJobId = null, lastEtaIdx = -1;
    const recorded = new Set();
    // Failed slots, keyed by slot index → the server's real error text. A slot that ERRORS produces no image, so it
    // never lands in `recorded`; without capturing it here the only signal a page gets is a zero made-count, which
    // reads identically to a genuine no-change edit. Keyed so a straggler seen twice (poll + final fetch) counts once.
    const failed = new Map();
    const prog = newBatchProgress();
    const preview = livePreviewPainter(o.previewTarget);
    const draw = () => { if (o.onProgress) o.onProgress(prog.value(N)); };
    const recordSlot = s => {
      if (!s || !s.id || s.changed === false || recorded.has(s.id)) return;
      recorded.add(s.id);
      preview.clear();
      if (o.onSlot) o.onSlot(s);
    };
    const recordFailure = s => { if (s && s.status === "error") failed.set(s.index, s.error || "The render failed."); };
    const finish = cancelled => {
      if (settled) return; settled = true;
      if (timer) clearInterval(timer);
      try { ws && ws.close(); } catch (e) { console.debug("ws close failed:", e); }
      preview.clear();
      document.removeEventListener("visibilitychange", onVis);
      if (o.eta) stopEta(o.eta);
      const status = o.finalStatus ? o.finalStatus(recorded.size, N, cancelled, [...failed.values()]) : null;
      if (status != null && o.setStatus) o.setStatus(status);
      if (o.onSettle) o.onSettle(recorded.size);
      resolve(recorded.size);
    };
    function openWs() {
      if (settled || ws) return;
      try {
        ws = new WebSocket(gwWs("/ws"));
        ws.binaryType = "blob";
        ws.onmessage = ev => {
          if (typeof ev.data !== "string") {
            if (frameJobId === jobId) preview.show(ev.data);
            return;
          }
          let m; try { m = JSON.parse(ev.data); } catch (e) { console.debug("batch ws non-JSON:", e); return; }
          const id = m.data && m.data.prompt_id;
          if (id) frameJobId = id;
          if (id && id === runningId) { const f = wsFraction(m); if (f != null) { prog.fraction(f); draw(); } }
          if (m.type === "executed" || m.type === "execution_error" || m.type === "execution_success") poll();
        };
        ws.onclose = () => { ws = null; frameJobId = null; }; ws.onerror = () => { try { ws && ws.close(); } catch (e) { console.debug("ws close failed:", e); } ws = null; frameJobId = null; };
      } catch (e) { console.debug("batch ws open failed:", e); ws = null; }
    }
    async function poll() {
      if (settled) return;
      let res; try { const r = await fetch(`${GATEWAY}/jobs`); if (!r.ok) return; res = await r.json(); } catch (e) { console.debug("job poll failed:", e); return; }
      const job = (res.jobs || []).find(j => j.jobId === jobId);
      if (!job) {
        let final = null;
        try { const r = await fetch(`${GATEWAY}/job/${encodeURIComponent(jobId)}`); if (r.ok) { final = await r.json(); (final.slots || []).forEach(s => { recordSlot(s); recordFailure(s); }); } } catch (e) { console.debug("final job fetch failed:", e); }
        finish(!!(final && final.status === "cancelled"));
        return;
      }
      const runSlot = (job.slots || []).find(s => s.status === "running");
      runningId = runSlot ? job.jobId : null;   // /ws frames carry the job id (every slot maps to it)
      if (o.onRunning) o.onRunning(runSlot || null, job);
      if (o.eta && runSlot && runSlot.index !== lastEtaIdx) { lastEtaIdx = runSlot.index; startEta(o.eta, job.expectedSeconds, job.startedAt); }
      (job.slots || []).forEach(s => { if (s.status === "done") recordSlot(s); else if (s.status === "error") recordFailure(s); });
      prog.finished(recorded.size); draw();
      if (o.activeStatus && o.setStatus) { const t = o.activeStatus(recorded.size, N, job, res.jobs || []); if (t != null) o.setStatus(t); }
    }
    const onVis = () => { if (document.visibilityState === "visible" && !settled) { poll(); openWs(); } };
    if (o.onCancelHandle) o.onCancelHandle({ cancel: async () => { try { await fetch(`${GATEWAY}/cancel/${encodeURIComponent(jobId)}`, { method: "POST" }); } catch (e) { console.debug("cancel request failed:", e); } } });
    document.addEventListener("visibilitychange", onVis);
    timer = setInterval(poll, 2000); poll(); openWs();
  });
}

// The ONE recovery path (#210). Every gen surface (composer + the edit page's modes) re-attaches to an
// already-running job through THIS — the same trackJobBatch a fresh submit uses — instead of hand-writing its own
// "pick up an existing generation" filter. Adoption is UNFILTERED: any of the user's active jobs (running preferred,
// else the first queued) lights the bar on every page. The ONLY per-surface divergence lives inside the panel's
// `onSlot` — whether THIS job's finished image is painted here — which the caller decides (the composer always paints;
// the editor paints only its own mode's source + workflow). One adoption at a time; when the tracked job leaves the
// feed the next tick picks up whatever is queued behind it (queue-more), draining the queue continuously.
//   isBusy()          -> true while the page runs its OWN submit (that flow already owns the panel; don't also adopt)
//   onAdopt(job)      -> the page marks itself busy + shows its bar (and can set an opening status)
//   options(job)      -> the trackJobBatch options for the visible surface (its onSlot applies the relevance filter,
//                        its onSettle tears the bar/busy state back down). `total` defaults to job.total.
// Returns { tick } so a page can force an immediate adoption check (e.g. on entering a tab).
function attachLiveRecover(o) {
  let tracking = false;
  async function tick() {
    if (tracking || o.isBusy()) return;
    // Claimed BEFORE the await: the interval tick, the initial tick and the visibilitychange tick all fire together
    // when a tab wakes up, and checking the flag on entry alone let two of them pass it while the first's /jobs fetch
    // was still in flight — both then adopted the same job and ran two trackers over one panel.
    tracking = true;
    try {
      let res;
      try { const r = await fetch(`${GATEWAY}/jobs`); if (!r.ok) return; res = await r.json(); }
      catch (e) { console.debug("live recover poll failed:", e); return; }
      const jobs = res.jobs || [];
      const job = jobs.find(j => j.status === "running") || jobs.find(j => j.status === "queued");
      if (!job) return;
      o.onAdopt(job);
      const opts = o.options(job);
      await trackJobBatch(job.jobId, { total: job.total || 1, ...opts });
    } finally { tracking = false; }
  }
  tick();
  setInterval(tick, 2500);
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") tick(); });
  return { tick };
}

// The ONE submit control (#147). EVERY button that enqueues work — the composer's Generate (and its Reload), and each
// edit mode's Generate/Apply — is attached to THIS, so the entire submit lifecycle lives here once and can never drift:
//   • click / the hold-to-count picker / the form's submit event,
//   • collect the page's items and POST them as ONE /enqueue job,
//   • track that one job through trackJobBatch (status·ETA·bar·preview),
//   • queue MORE (a separate job) when pressed while a job is running.
// The page supplies ONLY what is page-specific, via `o`:
//   button                              the submit button element (the picker + click attach here)
//   form?                               its form, if any (submit is intercepted)
//   isBusy()                            whether a render this control owns is in flight (the page's own flag)
//   buildItems(n) -> built | Promise    what to submit. Either an items array, or { items, meta } when the panel needs
//                                       per-submission context (e.g. which prompt/models/shapes this batch used). Return
//                                       [] (or {items:[]}) to abort after showing your own message; throw to error.
//   onBusy(bool)                        the page marks itself busy/idle (and shows/hides its Cancel button)
//   onActiveGen(handle|null)            the page stores the { cancel } handle so its Cancel button reaches this job
//   panel                               the progress/preview wiring for trackJobBatch: show(bool), onProgress(f),
//                                       onSlot(slot, meta), previewTarget?, onRunning?(slot, job, meta), eta,
//                                       activeStatus?, finalStatus,
//                                       onSettle? — onSlot/onRunning receive the built `meta` so a queue-more submission
//                                       (its own job + meta) can never corrupt the running job's rendering.
//   setStatus(text, opts?)              write the page's status line
//   startStatus(count) -> text          status shown the instant a submit is accepted
//   queuedToast?(count) -> text         toast when a press queues behind a running job
// Returns { submit(n), enqueue(built, startText), queueMore(n) } — `enqueue` submits a page-built value directly (the
// composer's Reload uses it), sharing the exact same track/queue lifecycle as a button press.
function attachEnqueueSubmit(o) {
  let pending = false;   // synchronous guard so two fast clicks can't both pass the async build before onBusy lands
  const itemsOf = built => Array.isArray(built) ? built : (built && built.items) || [];
  const metaOf = built => Array.isArray(built) ? undefined : (built && built.meta);
  async function collect(n) {
    try { return (await o.buildItems(Math.max(1, n || 1))); }
    catch (e) { o.setStatus(friendlyError(e), { error: true }); return null; }
  }
  async function enqueue(built, startText) {
    const items = itemsOf(built), meta = metaOf(built);
    if (!items.length) return;
    o.onBusy(true); o.panel.show(true);
    o.setStatus(items.length === 1 ? (startText || o.startStatus(1)) : `Making ${items.length}…`);
    try {
      const r = await fetch(`${GATEWAY}/enqueue`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ jobs: items }) });
      if (!r.ok) throw new Error(await gwError(r));
      const resp = await r.json(); const jobId = resp.jobId;
      if (!jobId) throw new Error("The queue accepted no jobs.");
      await trackJobBatch(jobId, {
        total: resp.total || items.length,
        eta: o.panel.eta, onProgress: o.panel.onProgress, previewTarget: o.panel.previewTarget,
        onSlot: s => o.panel.onSlot(s, meta),
        onRunning: o.panel.onRunning ? (s, job) => o.panel.onRunning(s, job, meta) : undefined,
        activeStatus: o.panel.activeStatus, finalStatus: o.panel.finalStatus, setStatus: o.setStatus,
        onCancelHandle: h => o.onActiveGen(h),
        onSettle: made => { o.panel.show(false); if (o.panel.onSettle) o.panel.onSettle(made); },
      });
    } catch (e) {
      o.setStatus((e && e.name === "AbortError") ? "Cancelled." : friendlyError(e), { error: true });
      o.panel.show(false); if (o.panel.onSettle) o.panel.onSettle(0);
    } finally { o.onBusy(false); o.onActiveGen(null); }
  }
  async function submit(n) {
    if (pending || o.isBusy()) return;
    pending = true;
    try { const built = await collect(n); if (built) await enqueue(built); }
    finally { pending = false; }
  }
  async function queueMore(n) {
    const items = itemsOf(await collect(n));
    if (!items.length) return;
    try {
      const r = await fetch(`${GATEWAY}/enqueue`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ jobs: items }) });
      if (!r.ok) throw new Error(await gwError(r));
      toast(o.queuedToast ? o.queuedToast(items.length) : (items.length > 1 ? `Queued ${items.length} more — they start when the current one finishes.` : "Queued another — starts when the current one finishes."));
    } catch (e) { console.error("queue-more failed:", e); toast(friendlyError(e)); }
  }
  const picker = attachCountPicker(o.button, { onPick: n => o.isBusy() ? queueMore(n) : submit(n) });
  if (o.form) o.form.addEventListener("submit", e => { e.preventDefault(); if (picker.opened) { picker.opened = false; return; } o.isBusy() ? queueMore(1) : submit(1); });
  return { submit, enqueue, queueMore };
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
// Tags are a DEFINITION property now; what the client stores is the DELTA over them — the labels the user added and
// the base tags they removed. Both maps go in one PUT (both halves are replaced wholesale, so a removal can't outlive
// the edit that dropped it).
const saveWorkflowTags = (added, removed) =>
  Api.send("/api/settings/workflow-tags", "PUT", { customWorkflowTags: added, removedWorkflowTags: removed });

// The effective tag set for a workflow: its base (definition) tags plus the user's added labels, minus the ones they
// removed. Base order first, then additions; a removed base tag drops out, and a base tag added to the definition
// LATER still shows because it was never in the removed set. Dedupes, so a user "adding" a base tag is a no-op.
function computeWorkflowTags(baseTags, added, removed) {
  const rm = new Set(removed || []);
  const out = [], seen = new Set();
  for (const t of (baseTags || []).concat(added || [])) {
    if (!rm.has(t) && !seen.has(t)) { seen.add(t); out.push(t); }
  }
  return out;
}
const saveHiddenWorkflows = ids => Api.send("/api/settings/hidden", "PUT", { hiddenWorkflowIds: ids });
// Workflows hidden from the API workflow list — a separate per-user set from the UI-picker one above.
const saveHiddenApiWorkflows = ids => Api.send("/api/settings/hidden-api", "PUT", { hiddenApiWorkflowIds: ids });
// Per-workflow parameter-visibility overrides (configId -> paramKey -> bool) as an opaque JSON string on its own
// route. Null clears the column (no overrides left).
const saveParamVisibility = json => Api.send("/api/settings/param-visibility", "PUT", { paramVisibilityPrefs: json });
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
function parseRemovedTags(s) {
  const m = s.removedWorkflowTags ?? {};
  if (!m || typeof m !== "object" || Array.isArray(m)) throw new Error("removedWorkflowTags is not an object");
  return m;
}
function parseParamVis(s) {
  const raw = s.paramVisibilityPrefs;
  if (raw == null || raw === "") return {};
  const m = JSON.parse(raw);
  if (!m || typeof m !== "object" || Array.isArray(m)) throw new Error("paramVisibilityPrefs is not an object");
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
  const empty = { favs: new Set(), hidden: new Set(), hiddenApi: new Set(), tags: {}, removed: {}, paramVis: {} };
  let s;
  try {
    s = await fetchSettings();
  } catch (e) {
    console.error("Workflow preferences could not be loaded:", e);
    PARAM_VIS = {};
    return { ok: false, settings: null, ...empty };
  }
  try {
    const prefs = { ok: true, settings: s, favs: new Set(parseFavs(s)), hidden: new Set(parseHidden(s)), hiddenApi: new Set(parseHiddenApi(s)), tags: parseWorkflowTags(s), removed: parseRemovedTags(s), paramVis: parseParamVis(s) };
    // The param renderers below overlay this without every caller having to thread it through — reads fall back to
    // the shipped visibility when it never loaded, exactly like the un-personalized workflow list.
    PARAM_VIS = prefs.paramVis;
    return prefs;
  } catch (e) {
    // The response arrived; these blobs are what is unreadable. Other blobs in it are still fine, so it is
    // handed back — but ok stays false, so nothing writes over the ones that failed.
    console.error("Stored workflow preferences are not readable:", e);
    PARAM_VIS = {};
    return { ok: false, settings: s, ...empty };
  }
}
