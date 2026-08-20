// Compose page: generate images, live progress, the Recent strip, batch, and tag/artist autocomplete.
// Browsing/editing live on their own routes, so a result/recent thumbnail navigates to /image/{id} and
// "Edit" to /edit/{id}. Uses core.js.

const $prompt = $("prompt"), $tagPop = $("tagPop"), $generate = $("generate"),
      $modelSelect = $("modelSelect"), $modelToggle = $("modelToggle"), $modelMenu = $("modelMenu"), $status = $("status"),
      $bar = $("bar"), $barFill = $bar.querySelector("i"), $result = $("result"), $genModel = $("genModel"),
      $cancelGen = $("cancelGen"),
      $composer = $("composer"), $modelTip = $("modelTip"), $aspect = $("aspect"),
      $randomArtist = $("randomArtist"), $randomArtistBar = $("randomArtistBar"),
      $promptTemp = $("promptTemp"), $promptTempVal = $("promptTempVal"), $randomPromptBar = $("randomPromptBar"),
      $negWrap = $("negWrap"), $negPrompt = $("negativePrompt"), $negTagPop = $("negTagPop"),
      $negToggle = $("negToggle"), $negBody = $("negBody"),
      $loraSection = $("loraSection"), $loraToggle = $("loraToggle"), $loraBody = $("loraBody"),
      $loraList = $("loraList"), $loraAdd = $("loraAdd"), $loraCount = $("loraCount");

let CATALOG = null;
const MODELS = {};
// This page runs its OWN live tracker (startLiveSync, below) — which also drives the compose bar and busy state.
// Claim the role synchronously here so the shared tracker.js (loaded after this file by _Layout) stands down.
window.__liveTrackerOwned = true;
let busy = false, activeGen = null, cancelRequested = false;
// Shape is a SET, not one value (#213): a tap picks exactly one, a long-press ADDS one (the style picker's gesture).
// The composer still sends WIDTH/HEIGHT, never an aspect name (#209) — with two or more picked, each slot of a batch
// ROLLS its own shape at build time, resolved through the model's aspect map to that shape's dims before submit, so
// the wire is unchanged. Custom never participates: it is one arbitrary size, mutually exclusive with a multi-pick.
const ASPECTS = ["square", "landscape", "portrait"];
let aspects = ["square"];
const primaryAspect = () => aspects[0] || "square";
// True while several shapes are picked (Custom excludes itself by collapsing the roll to the primary).
const multiShape = () => !customActive && aspects.length > 1;
// The shape one slot renders at: rolled from the picked set when several are picked, otherwise the single pick.
const pickAspect = () => multiShape() ? aspects[Math.floor(Math.random() * aspects.length)] : primaryAspect();
let hasEditors = false;
// Artist mode: when the composer is on an artist page it carries a locked artist (data-artist). Every gen is
// locked to that artist, and the Random-artist option and '@' artist autocomplete are suppressed.
const $composeView = $("composeView");
const LOCKED_ARTIST = ($composeView && $composeView.dataset.artist) || "";
const ARTIST_MODE = !!LOCKED_ARTIST;
// Append the locked artist to a prompt for artist-capable models (the gateway formats the '@' tag per model and
// records the artist mark). No-op when not in artist mode, the model can't do artists, or it's already present.
function lockArtist(model, prompt) {
  if (!ARTIST_MODE) return prompt;
  const tg = model && model.tagging;
  if (!(tg && tg.artists)) return prompt;
  const norm = normToken(LOCKED_ARTIST);
  if (String(prompt || "").split(/[,\n]/).some(s => normToken(s) === norm)) return prompt;
  const p = String(prompt || "").trim().replace(/,\s*$/, "");
  return p ? p + ", @" + LOCKED_ARTIST : "@" + LOCKED_ARTIST;
}

// `html` is for the handful of statuses that carry a link (a dead end with nowhere to go is the worst kind of
// error message). Only ever called with a literal from this file — never with anything a server or a user supplied.
function setStatus(t, { error = false, html = false } = {}) {
  $status.classList.toggle("error", error);
  if (html) $status.innerHTML = t; else $status.textContent = t;
}

// Tab title + favicon progress ring so a backgrounded tab shows work.
const DEFAULT_TITLE = document.title;
let $favicon = document.querySelector('link[rel="icon"]');
const FAVICON_OURS = !$favicon;
if (FAVICON_OURS) { $favicon = document.createElement("link"); $favicon.rel = "icon"; }
const DEFAULT_FAVICON_HREF = $favicon.getAttribute("href");
function faviconRing(p) {
  const s = 64, cv = document.createElement("canvas"); cv.width = cv.height = s; const x = cv.getContext("2d");
  const cx = s / 2, cy = s / 2, r = s / 2 - 7;
  x.lineWidth = 8; x.strokeStyle = "rgba(110,120,110,.28)"; x.beginPath(); x.arc(cx, cy, r, 0, Math.PI * 2); x.stroke();
  x.lineWidth = 8; x.lineCap = "round"; x.strokeStyle = "#6b7f6e"; x.beginPath();
  x.arc(cx, cy, r, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * Math.max(0.02, Math.min(1, p))); x.stroke();
  return cv.toDataURL("image/png");
}
function setTabProgress(p) {
  const pct = Math.round(Math.min(1, Math.max(0, p)) * 100);
  document.title = `⏳ ${pct}% · Make a Picture`;
  try { if (FAVICON_OURS && !$favicon.parentNode) document.head.appendChild($favicon); $favicon.href = faviconRing(p); } catch (e) { console.debug("favicon update failed:", e); }
}
function clearTabProgress() {
  document.title = DEFAULT_TITLE;
  try { if (FAVICON_OURS) { $favicon.remove(); } else if (DEFAULT_FAVICON_HREF) { $favicon.href = DEFAULT_FAVICON_HREF; } } catch (e) { console.debug("favicon reset failed:", e); }
}
function showBar(p) { const w = Math.round(p * 100) + "%"; $bar.classList.add("show"); $barFill.style.width = w; setTabProgress(p); }
function hideBar() { $bar.classList.remove("show"); $barFill.style.width = "0"; clearTabProgress(); stopEta($("eta")); setGenModel(""); }
// The model line under the bar — only used by multi-model gens, to show which model is rendering right now.
function setGenModel(name) { if (!$genModel) return; if (name) { $genModel.textContent = name; $genModel.hidden = false; } else { $genModel.hidden = true; $genModel.textContent = ""; } }
// The ONE rule for that line, shared by every gen surface (fresh Generate, recovery, live-sync) so the readout can
// never drift between them again. Its only source is the wire job: each slot carries its own workflow id, so a run
// spans more than one workflow iff its slots hold more than one distinct id — and only THEN is "which one is
// rendering now" a question worth answering. Single-workflow runs (the common case) show nothing.
function showRunningModel(runSlot, job) {
  if (!runSlot) return;
  const ids = new Set((job.slots || []).map(s => s.model));
  if (ids.size < 2) { setGenModel(""); return; }
  setGenModel((MODELS[runSlot.model] && MODELS[runSlot.model].friendly_name) || runSlot.model || "");
}

// --- model catalog ------------------------------------------------------------------------------
const gwModel = m => (m && m._gw) || "";

// Per-model bans are NOT sent from here. The worker reads the user's banned tags/artists from the store when it
// runs a random prompt/artist, so a ban binds every generate the moment it is saved — nothing to cache or attach.

function exampleFor(model) {
  const p = model.prompt || {};
  let ex = (p.example || "").trim();
  if (!ex) return "";
  if (/\(user\)/i.test(ex)) ex = ex.split(/\(user\)/i).pop().trim();
  ex = ex.replace(/^\(with input image\)\s*/i, "").trim();
  if (p.required_prefix) {
    const drop = new Set(p.required_prefix.split(",").map(t => t.trim().toLowerCase()).filter(Boolean));
    ex = ex.split(",").map(t => t.trim()).filter(t => t && !drop.has(t.toLowerCase())).join(", ");
  }
  return ex;
}
function nsfwFlag(raw) {
  const r = String(raw || "").trim().toLowerCase();
  if (r.startsWith("not")) return { text: "Not documented", cls: "nsfw-unknown" };
  if (r.startsWith("yes")) return { text: "Yes", cls: "nsfw-yes" };
  if (r.startsWith("no")) return { text: "No", cls: "nsfw-no" };
  if (r.startsWith("limited")) return { text: "Limited", cls: "nsfw-limited" };
  return { text: "Not documented", cls: "nsfw-unknown" };
}
function updatePlaceholder() {
  const m = primaryModel();   // null when 0 or 2+ checked: the per-model panel/tip/autocomplete then stay hidden
  if ($randomArtistBar) $randomArtistBar.hidden = !(m && m.tagging && m.tagging.artists);
  if ($randomPromptBar) $randomPromptBar.hidden = !(m && m.tagGeneratorEnabled);
  syncTagTypesBar();   // the mask hides with the slider when this workflow's tag generator is off
  updateNegativeField();   // reveal the negative field iff any checked model supports one (independent of primary)
  updateLoraSection();     // the LoRA accordion shows only when a selected model produces images
  updateCustomShape();     // the "Custom" aspect shows only for a single workflow that enabled custom sizing
  const ex = m && exampleFor(m);
  $prompt.placeholder = ex || "Describe the picture you'd like to make…";
  const help = m && m.ui_help;
  if (!m || !help) { $modelTip.textContent = ""; $modelTip.hidden = true; renderParams(m); return; }
  const parts = [];
  if (help.good_for) parts.push(`<div><b>Good for:</b> ${escapeHtml(help.good_for)}</div>`);
  const nf = nsfwFlag(m.nsfw_capable);
  parts.push(`<div><b>Adult content:</b> <span class="${nf.cls}">${nf.text}</span></div>`);
  if (help.note) {
    let note = escapeHtml(help.note);
    const helpUrl = help.link && safeExternalUrl(help.link.url);
    if (helpUrl)
      note += ` <a href="${escapeHtml(helpUrl)}" target="_blank" rel="noopener noreferrer">${escapeHtml(help.link.text || "Learn more")}</a>`;
    parts.push(`<div class="mi-note">${note}</div>`);
  }
  $modelTip.innerHTML = parts.join("");
  $modelTip.hidden = false;
  renderParams(m);
}

// A workflow card is server-shipped today, but its URL still crosses into an executable browser sink. Encoding a
// javascript: URL does not change its scheme; parse it and allow only ordinary web links before it reaches href.
function safeExternalUrl(raw) {
  if (!raw) return null;
  try {
    const url = new URL(raw, window.location.origin);
    return url.protocol === "http:" || url.protocol === "https:" ? url.href : null;
  } catch (_) {
    return null;
  }
}


// --- custom size (per-workflow toggle, #150) -----------------------------------------------------
// A workflow whose settings page enabled "Custom size" (r.customSizeEnabled) gets a "Custom" aspect on the shape
// row and shows the REAL width/height inputs ALL the time (#225) — the same #modelParams fields #191 can reveal,
// which write straight through to the submitted #genW/#genH (#224: there is no separate Custom W/H pair) — bounded
// by the model's resolution envelope. The fields are the readout of the selected shape's dims AND the editor of a
// Custom size: typing in one selects Custom; clicking an aspect deselects it. Every shape submits width/height now
// (#209) — Custom differs only in being an ARBITRARY size (plus megapixels = its area, so the exact pixels are
// reproduced) rather than one of the model's aspect-map dims, so the server envelope-checks it. Offered only for a
// SINGLE picked generate workflow — "the current workflow" is meaningless with a multi-pick, each of which carries
// its own sizes.
const $aspectCustom = $("aspectCustom");
// The width/height actually submitted (#209): always in the DOM, written by an aspect click, read at submit. The single
// source of the render size — a revealed width/height field is this same value, shown.
const $genW = $("genW"), $genH = $("genH");
let customActive = false, customEnv = null;
const ENV_CACHE = new Map();   // config id -> resolution envelope (null = the config declares none)

// The single generate workflow whose custom sizing we'd offer, or null (0 or 2+ picked, or it hasn't enabled it).
function customCapable() { const m = primaryModel(); return (m && m.kind === "generate" && m.customSizeEnabled) ? m : null; }
const customDim = el => { const n = parseInt(el && el.value, 10); return Number.isFinite(n) && n > 0 ? n : 0; };
// True only when Custom is active with both dims filled — the gate the submit path checks before enqueueing.
function customReady() { return customActive && customDim($genW) > 0 && customDim($genH) > 0; }

function updateCustomShape() {
  const on = !!customCapable();
  if ($aspectCustom) $aspectCustom.classList.toggle("hidden", !on);
  if (!on && customActive) setCustomActive(false);   // selection moved to a workflow that doesn't offer custom sizing
}

// Selection state only — the size fields stay on screen either way (#225); this just moves the chip highlight.
function setCustomActive(on) {
  customActive = on;
  if ($aspectCustom) $aspectCustom.classList.toggle("active", on);
  if (on) {
    for (const b of $aspect.children) if (b !== $aspectCustom) b.classList.remove("active");   // Custom is exclusive
  } else {
    setAspects(aspects);   // restore the normal shape's active markers
  }
  updateSizeControlsVisibility();
}

// #213: with several shapes picked there is no single size to show or edit — each slot rolls its own — so the size
// fields, the megapixels control, and the envelope note come off screen while the multi-pick is active. They are the
// same DOM the single-shape flow uses (readout/editor of the one submitted pair), just hidden, so collapsing back to
// one shape restores them as-is.
function updateSizeControlsVisibility() {
  const hide = multiShape();
  const box = document.getElementById("modelParams");
  if (!box) return;
  const row = box.querySelector(".mp-size-row");
  if (row) row.classList.toggle("hidden", hide);
  for (const k of ["width", "height"]) {   // a #191-revealed field outside the inline row hides too
    const el = sizeField(k), f = el && el.closest(".mp-field");
    if (f && !(row && row.contains(f))) f.classList.toggle("hidden", hide);
  }
  const mp = $mpField(), mpWrap = mp && mp.closest(".mp-field");
  if (mpWrap) mpWrap.classList.toggle("hidden", hide);
  const note = box.querySelector("#customSizeNote");
  if (note) note.classList.toggle("hidden", hide);
}

// The width/height specs to force into the params panel for a custom-capable workflow: the model's own shipped specs
// (exposed or revealable), so the forced field matches what #191's reveal would render.
function sizeSpecs(m) {
  const find = k => ((m.exposedParams || []).find(p => p.key === k))
    || ((m.hiddenParams || []).find(p => p.key === k))
    || { key: k, type: "int", label: k === "width" ? "Width" : "Height", value: null };
  return [find("width"), find("height")];
}
// The rendered width/height fields (null when not currently in the panel).
const sizeField = k => document.querySelector(`#modelParams [data-key="${k}"]`);

// Lay the size fields out as one inline "W × H" row, directly above the megapixels budget they feed (the fields
// render at the panel's end by default; this moves them where the size belongs).
function layoutSizeFields() {
  const box = document.getElementById("modelParams");
  const wEl = sizeField("width"), hEl = sizeField("height");
  if (!(box && wEl && hEl)) return;
  const row = document.createElement("div");
  row.className = "mp-size-row";
  const x = document.createElement("span"); x.className = "wf-aspect-x"; x.textContent = "×";
  row.append(wEl.closest(".mp-field"), x, hEl.closest(".mp-field"));
  const mp = $mpField();
  if (mp) box.insertBefore(row, mp.closest(".mp-field")); else box.appendChild(row);
}

// Bound the size fields by the model's declared envelope and attach the same note the settings size editor shows.
// The envelope is fetched once per config and re-applied on every re-render for that config.
async function applyEnvelope(m) {
  if (!ENV_CACHE.has(m.id)) {
    try {
      const r = await fetch(`${GATEWAY}/catalog/config/${encodeURIComponent(m.id)}/settings`);
      if (!r.ok) return;
      ENV_CACHE.set(m.id, (await r.json()).resolution || null);
    } catch (e) { console.debug("custom size envelope load failed:", e); return; }
  }
  const pm = primaryModel();
  if (!pm || pm.id !== m.id) return;   // the selection moved while the fetch was in flight
  customEnv = ENV_CACHE.get(m.id);
  if (!customEnv) return;
  const wEl = sizeField("width"), hEl = sizeField("height");
  for (const [el, lo, hi] of [[wEl, customEnv.minW, customEnv.maxW], [hEl, customEnv.minH, customEnv.maxH]]) {
    if (!el) continue;
    el.min = lo; el.max = hi; el.step = customEnv.step;
  }
  // The note rides under the height field so the bounds sit next to the boxes they bound.
  const box = document.getElementById("modelParams");
  if (hEl && box) {
    let note = box.querySelector("#customSizeNote");
    if (!note) { note = document.createElement("p"); note.id = "customSizeNote"; note.className = "wf-aspect-note"; (hEl.closest(".mp-size-row") || hEl.closest(".mp-field")).after(note); }
    note.textContent =
      `This model supports ${customEnv.minW}–${customEnv.maxW} wide and ${customEnv.minH}–${customEnv.maxH} tall, in multiples of ${customEnv.step}.`;
  }
  decorateMpSlider();
  updateSizeControlsVisibility();   // the note lands async — re-hide it if a multi-pick is active
}

// A slider beside the megapixels number input — the same value with a draggable handle. Its range is the model's own
// envelope, as an area: smallest supported W×H to largest, so every slider position is a size the model can render.
// Built only once the envelope is known; a config that declares none keeps the plain number input.
function decorateMpSlider() {
  const f = $mpField(); if (!f || !customEnv) return;
  const wrap = f.closest(".mp-field"); if (!wrap) return;
  const lo = Math.ceil((customEnv.minW * customEnv.minH) / (1024 * 1024) * 100) / 100;
  const hi = Math.floor((customEnv.maxW * customEnv.maxH) / (1024 * 1024) * 100) / 100;
  let slider = wrap.querySelector(".mp-slider");
  if (!slider) {
    const row = document.createElement("div"); row.className = "mp-num-row";
    slider = document.createElement("input"); slider.type = "range"; slider.className = "mp-slider";
    f.replaceWith(row); row.append(slider, f);
    // Dragging routes through the number input's own input event, so the M edit behaves exactly like a typed one
    // (W/H rescale, Custom selection, prefs save); typing keeps the handle in step the other way.
    slider.addEventListener("input", () => { f.value = slider.value; f.dispatchEvent(new Event("input", { bubbles: true })); });
    f.addEventListener("input", () => { slider.value = f.value; });
  }
  slider.min = lo; slider.max = hi; slider.step = 0.01;
  slider.value = f.value;
}
// Programmatic M writes (aspect reset, W/H edits) don't fire input events — re-seat the handle by hand.
function syncMpSlider() {
  const f = $mpField(); if (!f) return;
  const slider = f.closest(".mp-field")?.querySelector(".mp-slider");
  if (slider) slider.value = f.value;
}

// --- W/H ⇄ M coupling (#186) ---------------------------------------------------------------------
// Megapixels is a first-class render-SIZE control: revealed via #191 it renders in the params panel (works for image
// AND video models — its aspect ratio comes from the picked shape server-side). On a custom-capable workflow the W/H
// fields are always on screen, and the two stay coherent live: editing W/H recomputes M from the area; editing M
// rescales W/H to that budget, preserving the current W:H ratio and snapping to the envelope step — either edit moves
// the shape selection to Custom. Whichever the user last touched wins, and currentOverrides submits the coherent pair.
// M-only (workflow without custom size): an M edit still rescales the hidden genW/H, so the submitted pair tracks the
// budget — it just can't select a Custom shape that isn't on offer.
const mpFromWH = (w, h) => Math.round((w * h) / (1024 * 1024) * 100) / 100;
const $mpField = () => document.querySelector('#modelParams [data-key="megapixels"]');
function syncMpFromWH() {
  const f = $mpField(); if (!f) return;
  const w = customDim($genW), h = customDim($genH);
  if (w > 0 && h > 0) { f.value = mpFromWH(w, h).toFixed(2); syncMpSlider(); }
}
function rescaleWHtoMp(mp) {
  const w = customDim($genW), h = customDim($genH);
  const step = customEnv && customEnv.step;
  if (!(w > 0 && h > 0) || !(mp > 0) || !step) return false;   // no envelope step yet → skip the cosmetic rescale (the server snap is authoritative)
  const scale = Math.sqrt((mp * 1024 * 1024) / (w * h));
  writeSize(Math.max(step, Math.round(w * scale / step) * step), Math.max(step, Math.round(h * scale / step) * step));
  return true;
}
// The megapixels DEFAULT this model ships (its config value). Every aspect of a model now shares one budget (#186), so
// that default IS each shape's size — used to reset the M readout when the shape changes.
function modelMpDefault(m) {
  const find = a => (a || []).find(p => p.key === "megapixels");
  const p = m && (find(m.exposedParams) || find(m.hiddenParams));
  return p ? p.value : null;
}
// #186: clicking an aspect picks that shape's DEFAULT size, so reset the (revealed) M control back to the model's
// budget — a manual M isn't carried across a shape change, mirroring the ticket's "aspect button recomputes M".
function resetMpFromAspect() {
  const f = $mpField(); if (!f) return;
  const d = modelMpDefault(selectedModels()[0]);
  if (d != null) { f.value = d; syncMpSlider(); }
}
// The M field is re-rendered per model, so couple to it by delegation: on a custom-capable workflow an edit to M
// rescales the W/H fields (programmatic writes to W/H don't re-fire input, so there's no feedback loop), persists the
// sizes, and — like typing in a size field — moves the shape selection to Custom: the resulting size is no longer the
// selected aspect's dims.
document.getElementById("modelParams").addEventListener("input", e => {
  const t = e.target;
  if (!(t && t.dataset)) return;
  if (t.dataset.key === "megapixels" && primaryModel()) {
    const mp = Number(t.value);
    if (Number.isFinite(mp) && rescaleWHtoMp(mp)) {
      // The rescaled size is submitted either way (it lands in genW/H); Custom is a shape on offer only for a
      // custom-capable workflow, so only there does the edit also move the selection.
      if (!customActive && customCapable()) setCustomActive(true);
      savePrefs();
    }
    return;
  }
  // A revealed width/height field IS the submitted size box (#209): an edit writes straight through to genW/H, and the
  // (revealed) M readout follows the new area. Per-field: editing width leaves the aspect's height standing.
  if (t.dataset.key === "width" || t.dataset.key === "height") {
    const el = t.dataset.key === "width" ? $genW : $genH;
    if (el) el.value = customDim(t) || "";
    const f = $mpField(), w = customDim($genW), h = customDim($genH);
    if (f && w > 0 && h > 0) { f.value = mpFromWH(w, h).toFixed(2); syncMpSlider(); }
    // #225: editing the size IS choosing a custom size — typing in either field moves the shape selection to Custom
    // (the prefs save riding on this same event then records it).
    if (!customActive && customCapable()) setCustomActive(true);
  }
});

// Adapt a /workflows configuration row into the model shape the rest of this page expects. The server already
// resolved presence + VRAM, so a returned row is runnable on this machine; `_gw` is the configuration id the
// client submits as `model`. `exposedParams` are the configuration's UI-exposed parameters (steps/cfg/...).
function adaptWorkflow(r) {
  const c = r.card || {};
  return {
    id: r.id, friendly_name: r.friendlyName || r.id, _gw: r.id, default: !!r.default, avgSeconds: r.avgSeconds,
    kind: r.kind, canEdit: !!r.canEdit, media: r.media === "video" ? "video" : "image", hasAudio: !!r.hasAudio, exposedParams: r.exposedParams || [],
    hiddenParams: r.hiddenParams || [],   // shipped hidden-but-revealable params — shown only where the user's visibility prefs reveal them (#191)
    loraFolder: r.loraFolder || "",   // the workflow's default LoRA-picker folder (Part H); "" = smart-route by id
    negativeSupported: c.negativeSupported === true,   // model's card declares it uses a negative prompt
    speed: { class: c.speed }, nsfw_capable: c.nsfwCapable,
    prompt: { example: c.example, required_prefix: c.requiredPrefix },
    ui_help: { good_for: c.uiGoodFor, note: c.uiNote, link: c.uiLink || null },
    tagging: c.tagging || null,
    tagGeneratorEnabled: !!r.tagGeneratorEnabled,
    customSizeEnabled: !!r.customSizeEnabled,
    aspects: r.aspects || null   // this model's aspect→[w,h] map; clicking a shape writes its dims into W/H (#209)
  };
}

// --- multi-select model picker (shared createModelPicker; see modelpicker.js) -------------------
// The Style picker fans the SAME prompt out to every checked model (one slot per model — see buildComposerItems).
// "Primary" = the model when EXACTLY one is checked; it alone drives the model tip and #/@ autocomplete. With 2+
// checked there's no primary, so those single-model affordances hide — but the param panel shows the params common
// to ALL checked models (their intersection) and applies them to every one. selectedModelIds/selectedModels/
// primaryModel just read the picker's live state.
let modelFavs = new Set(), modelHidden = new Set(), modelTags = {};   // from /api/settings: favorites, hidden, custom tags
const modelPicker = createModelPicker({
  select: $modelSelect, toggle: $modelToggle, menu: $modelMenu,
  nameOf: m => m.friendly_name,
  favOf: m => modelFavs.has(m.id),
  timeOf: m => m.avgSeconds,
  tagsOf: m => modelTags[m.id] || [],
  groups: [
    { label: "Text → image", match: m => m.media === "image" && !modelHidden.has(m.id) },
    { label: "Text → video", match: m => m.media === "video" && !modelHidden.has(m.id) },
  ],
  hint: "Long-press a style to pick several",
  onChange: () => updatePlaceholder(),               // any change refreshes the primary-model panel/tip/autocomplete
  onCommit: () => { savePrefs(); closeTagPop(); },   // user change also persists prefs + drops the tag popup
});
const selectedModelIds = () => modelPicker.getSelectedIds();
const selectedModels = () => modelPicker.getSelected();
const primaryModel = () => modelPicker.getPrimary();

async function loadModels() {
  try {
    const resp = await fetch(`${GATEWAY}/workflows`);
    if (!resp.ok) throw new Error(await gwError(resp));
    const all = ((await resp.json()) || []).map(adaptWorkflow);
    const models = all.filter(m => m.kind === "generate");
    if (!models.length) {
      $modelToggle.textContent = "No workflows found";
      // To the LIBRARY, not the models page. This is a fact about workflows — none of them can run — and the
      // library is the page that says which ones, and what each is waiting for, and opens the dialog that sets
      // it. The models page is 140 slots with nothing to say about which workflow wanted any of them, so it
      // answered "set up your models" with "which ones?".
      setStatus('No workflows are ready — they are waiting on model files. '
        + '<a href="/settings/workflows">See what they need</a>.', { error: true, html: true });
      return;
    }
    // Per-user favorites / hidden / custom tags drive the picker (sort, ★, drop hidden, tag sublines).
    // Read-only here (this page never writes favorites/hidden/tags), so an unreadable set degrades to the
    // un-personalized picker — which is honest, and loadWorkflowPrefs has already logged the reason.
    const prefs = await loadWorkflowPrefs();
    modelFavs = prefs.favs; modelHidden = prefs.hidden; modelTags = prefs.tags;
    for (const k in MODELS) delete MODELS[k];
    for (const m of models) MODELS[m.id] = m;   // MODELS keeps ALL (so reload-from-history works even for hidden)
    modelPicker.rebuild(models);
    const visible = models.filter(m => !modelHidden.has(m.id));
    const def = visible.find(m => m.default) || visible.find(m => m.id === "sdxl") || visible[0] || models[0];
    modelPicker.setSelectedIds([def.id]);
    hasEditors = all.some(m => m.canEdit);   // editors span several kinds now (#163); "editor" = can_edit, not kind==="edit"
    setStatus("");
  } catch (e) { $modelToggle.textContent = "Unavailable"; setStatus(friendlyError(e), { error: true }); }
}

// Compose-page bindings of the shared param helpers (defined in core.js) to the side-pane #modelParams container.
// paramPrefs is the FLAT by-name override map persisted in the account prefs blob (like the edit page) so every
// exposed knob (steps/cfg/polish_denoise/...) survives a reload. renderParams re-applies it after each rebuild;
// changing any field merges back into it and persists (debounced via savePrefs).
let paramPrefs = {};
// While Custom is active the panel carries the forced size fields, preserving the current (typed) genW/H; otherwise
// every rebuild writes the shape's dims so genW/H always hold the current model's size — a model switch while Custom
// is active must not leave the previous model's envelope bounds or note behind (the envelope re-applies per config).
const renderParams = () => {
  const box = document.getElementById("modelParams");
  // #225: a custom-capable workflow shows the size fields ALL the time — readout of the selected shape's dims and
  // the editor of a Custom size in one — not only while Custom is the active shape.
  const cm = customCapable();
  renderParamFields(box, selectedModels(), cm ? sizeSpecs(cm) : null);
  layoutSizeFields();
  applyParamPrefs(box, paramPrefs);
  if (customActive) { writeSize(customDim($genW) || "", customDim($genH) || ""); syncMpFromWH(); }
  else writeAspectSize(primaryAspect());
  updateSizeControlsVisibility();   // a multi-pick keeps the freshly-rendered size/M controls off screen
  // The envelope loads for ANY single generate workflow, not just a custom-capable one: its step also drives the
  // M → W/H rescale, which runs wherever an M field is on screen. Bounds + note only land on rendered size fields.
  const pm = primaryModel();
  if (pm && pm.kind === "generate") applyEnvelope(pm);
};
function currentOverrides() {
  const ov = readOverrides(document.getElementById("modelParams")) || {};
  // #209: the render size IS the width/height pair the aspect button (or a size-field edit) wrote — the single source,
  // read here. A Custom size additionally rides with megapixels = its exact area: the server takes the W:H RATIO from
  // the pair and sizes to megapixels (#186), so without M it would rescale the typed size to the config's default
  // budget; with it, the ratio × this-M snap reproduces what the user typed. Envelope-checked at submit.
  const w = customDim($genW), h = customDim($genH);
  if (w > 0 && h > 0) {
    ov.width = w; ov.height = h;
    if (customActive) ov.megapixels = mpFromWH(w, h);
  }
  return ov;
}
// This shape's dims on `model`, from its aspect→[w,h] map, or null when the model has no map (a fixed-size video, which
// the server then sizes from its own config).
function aspectDims(model, shape) {
  const wh = model && model.aspects && model.aspects[shape];
  return Array.isArray(wh) && wh.length >= 2 ? [wh[0], wh[1]] : null;
}
// Write the clicked shape's dims into the submitted width/height (#209). Resolved from the primary model's aspect map; a
// model with no map clears them, so the server sizes that generation from its own config. genW/H are set only here and
// by a user edit to a revealed width/height field (the input listener above) — there is no parallel submit-time resolver.
function writeAspectSize(shape) {
  const wh = aspectDims(selectedModels()[0], shape);
  // A map-less model clears the submitted pair but writes nothing to the fields — its revealed fields keep showing
  // the config's own dims.
  if (wh) writeSize(wh[0], wh[1]);
  else { if ($genW) $genW.value = ""; if ($genH) $genH.value = ""; }
}
// Write a size into the submitted pair AND any rendered width/height fields — a visible field (#191 reveal or the
// Custom force) is the SAME value, shown, so the two are written together everywhere.
function writeSize(w, h) {
  if ($genW) $genW.value = w || "";
  if ($genH) $genH.value = h || "";
  const wEl = sizeField("width"), hEl = sizeField("height");
  if (wEl) wEl.value = w || "";
  if (hEl) hEl.value = h || "";
}
// Capture on BOTH input (fires live per keystroke/spinner tick) and change (commit) — a number input only fires
// "change" on blur, which can be missed, so "input" is what makes edits reliably persist.
["input", "change"].forEach(ev => document.getElementById("modelParams").addEventListener(ev, () => { collectComposerParamPrefs(); savePrefs(); }));
// width/height are the submitted size, model-derived per shape (#209) — never sticky by-name prefs, which would leak
// one model's dims onto another model's revealed fields.
function collectComposerParamPrefs() {
  collectParamPrefs(document.getElementById("modelParams"), paramPrefs);
  delete paramPrefs.width;
  delete paramPrefs.height;
}

// Reload pickup is no longer device-local: the always-on liveSync (below) reconstructs the user's active
// generation from the server (/jobs + /ws) on every device, including this tab after a reload.

// --- count picker (hold-to-reveal) --------------------------------------------------------------
// The flyout + custom-amount modal are core.js's shared attachCountPicker (the edit page's inpaint Generate uses the
// same one). The gesture is the SAME in both states — hold always offers the count; only what the count means changes:
// while IDLE it starts n renders, while BUSY (Generate stays Generate) it stacks n onto the queue behind the live one.
// --- the ONE submit control (core.js attachEnqueueSubmit) ---------------------------------------
// The composer's Generate — and the detail card's Reload — go through the SAME shared submit component every edit mode
// uses. It owns the click / hold-to-count / queue-while-busy gestures, POSTs one /enqueue job with N slots, tracks it,
// and cancels it. This file supplies ONLY what to submit (buildComposerItems) and how to render progress (composePanel).
function composerCreatingStatus(_recorded, total, job, activeJobs) {
  const batchTotal = Math.max(1, Number(total) || 1);
  const batchProgress = Math.min(batchTotal, Math.max(0, Number(job && job.progress) || 0));
  const position = Math.min(batchProgress + 1, batchTotal);
  // The composer reports generation work, not an edit that happens to share the user's active feed. A fresh submit
  // and live recovery both read this same server-sourced list, so queue-more/reload/cancel cannot leave a local count.
  const generations = (Array.isArray(activeJobs) ? activeJobs : []).filter(j => j && j.kind === "generate");
  const relevant = job && job.kind === "generate" ? generations : [job].filter(Boolean);
  const remaining = relevant.length
    ? relevant.reduce((sum, j) => sum + Math.max(0, (Number(j.total) || 0) - (Number(j.progress) || 0)), 0)
    : Math.max(0, batchTotal - batchProgress);
  const current = batchTotal > 1 ? `Creating ${position}/${batchTotal}` : "Creating";
  return remaining > 1 ? `${current} · ${remaining} remaining` : `${current}…`;
}

const composePanel = {
  eta: $("eta"),
  previewTarget: $result,
  show: b => { if (b) showBar(0.02); else hideBar(); },
  onProgress: showBar,   // also drives the tab-title/favicon ring
  // `meta` is THIS submission's context (prompt/model/shapes), threaded by the control — so a queue-more job (its own
  // meta) can never make the running job record its slots against the wrong prompt/model.
  onSlot: (s, meta) => recordResult({ id: s.id, effectivePrompt: s.effectivePrompt, marks: s.marks, notice: s.notice }, meta.prompt, meta.model, meta.modelId, (meta.slotAspects && meta.slotAspects[s.index]) || ""),
  onRunning: showRunningModel,   // multi-model runs show which workflow is rendering now (see showRunningModel)
  activeStatus: composerCreatingStatus,
  // The job's OWN final status, not this tab's cancel flag: it may have been stopped from another device, and the
  // missing images weren't ones that "couldn't be made" — they weren't asked for any more.
  finalStatus: (made, total, cancelled, errors) => cancelled ? (made ? `Cancelled — made ${made} of ${total}.` : "Cancelled.")
    // A failed slot carries the server's real error — show it rather than only "couldn't be made", which says nothing.
    : (errors && errors.length) ? (made ? `Made ${made} of ${total}; ${errors.length} failed — ${errors[0]}` : `Couldn't make ${total > 1 ? `any of ${total}` : "the image"} — ${errors[0]}`)
    : (total - made) > 0 ? `Done — made ${made} of ${total} (${total - made} couldn't be made).`
    : `Done — made all ${made}.`,
};
const composeSubmit = attachEnqueueSubmit({
  button: $generate, form: $composer, panel: composePanel, buildItems: n => buildComposerItems(n),
  isBusy: () => busy,
  onBusy: b => { if (b) cancelRequested = false; setBusy(b); },
  onActiveGen: h => { activeGen = h; },
  onJob: (jobId, _items, meta) => postPending({ jobId, prompt: meta.prompt, model: meta.model, modelId: meta.modelId, aspect: (meta.slotAspects && meta.slotAspects[0]) || primaryAspect() }).catch(e => console.debug("record pending job failed:", e)),
  setStatus,
  startStatus: () => "Generating…",
});
function generate() { composeSubmit.submit(1); }
function startBatch(n) { composeSubmit.submit(n); }   // kept for any external callers

// Build what the composer's Generate submits: fan the prompt across every checked model, n images PER model, as ONE
// /enqueue job. One checked model → a single-model batch (its random-artist/prompt + param overrides); two-or-more →
// the multi-model fan-out (shared-param panel applies to all; random artist/prompt are single-model, so off). Returns
// { items, meta } — meta is what the panel renders each finished slot against. [] aborts (after messaging).
function buildComposerItems(n) {
  const prompt = $prompt.value.trim();
  const models = selectedModels();
  if (!models.length) { setStatus("Please pick at least one workflow.", { error: true }); return []; }
  // Custom size chosen but not filled in: refuse rather than silently fall back to the model's default size.
  if (customActive && !customReady()) { setStatus("Enter a width and height for the custom size.", { error: true }); return []; }
  savePrefs();
  n = Math.max(1, n || 1);
  // Exhaustive: {{a|b}} sets fan the prompt into one variant per option, multiplying across sets, models, and the batch.
  // Warn before a genuinely multiplicative run (2+ sets) with the real total; a single set just makes one-of-each.
  const info = explodeInfo(prompt);
  if (info.groupCount >= 2) {
    const total = info.combos * n * models.length;
    if (!confirm(`This prompt has ${info.groupCount} explode sets — it will create ${total} generations. Continue?`)) return [];
  }
  if (models.length === 1) {
    const model = models[0];
    const { items, slotAspects } = buildBatchItems(prompt, model, n);
    return { items, meta: { prompt, model: model.friendly_name, modelId: model.id, slotAspects } };
  }
  const { items, slotAspects } = buildMultiItems(prompt, models, n);
  return { items, meta: { prompt, model: `${models.length} workflows`, modelId: "", slotAspects } };
}

// The slots for one model: n copies of the prompt, each rolling its own aspect from the picked set (so a batch comes
// back mixed when several shapes are selected). The prompt is sent RAW — the SERVER resolves Comfy `{a|b}` random
// choices and `{{a|b}}` exhaustive groups, so browser, API, generation, and editing all share one implementation. `exact`
// (Reload) reproduces a picture verbatim — its own (already-resolved) prompt/negative/loras/shape, no re-roll.
function buildBatchItems(prompt, model, n, exact, aspect, negative, loras) {
  // One submitted width/height pair by default (#209, from currentOverrides). With a multi-shape pick (#213) each
  // slot rolls its own shape and carries THAT shape's dims instead — resolved client-side, so the wire still sees
  // width/height only. Reload reproduces the image's OWN size: its recorded shape overrides the composer's.
  const rollAspect = () => aspect || pickAspect();
  const slotAspects = Array.from({ length: n }, rollAspect);   // slotAspects[i] = the shape slot i is submitted with
  let ov = currentOverrides();
  if (exact && !customReady()) {
    const wh = aspectDims(model, aspect || primaryAspect());
    if (wh) { ov = { ...ov, width: wh[0], height: wh[1] }; }
  }
  // The rolled shape's dims ride as that slot's overrides; a map-less model keeps the shared pair (the server then
  // sizes it from its own config, exactly as the single-shape flow does).
  const ovFor = shape => {
    if (!multiShape() || exact) return ov;
    const wh = aspectDims(model, shape);
    return wh ? { ...ov, width: wh[0], height: wh[1] } : ov;
  };
  let items;
  if (exact) {
    // `negative ?? null` keeps "no negative was submitted" distinct from an empty one; the image's OWN LoRA stack.
    const one = { workflow: gwModel(model), prompt, originalPrompt: prompt, negativePrompt: negative ?? null, randomArtist: false, randomPrompt: false, temperature: null, overrides: ov, loras: loras || [], resolvePromptSyntax: false };
    items = Array.from({ length: n }, () => ({ ...one }));
  } else {
    const base = { workflow: gwModel(model), negativePrompt: negFor(model), randomArtist: wantsRandomArtist(model), randomPrompt: wantsRandomPrompt(model), temperature: promptTemp(), tagTypes: tagTypes(), loras: lorasPayload() };
    items = Array.from({ length: n }, (_, i) => ({ ...base, overrides: ovFor(slotAspects[i]), prompt: lockArtist(model, prompt), originalPrompt: prompt }));
  }
  return { items, slotAspects };
}
// Fan ONE prompt across several models — n copies per model, sent RAW (the server resolves the groups). The shared-param
// panel (params common to every selected model) applies to all; random artist/prompt stay single-model affordances (off
// here). Artist-mode locks per model.
function buildMultiItems(prompt, models, n) {
  const ov = currentOverrides();
  const items = [], slotAspects = [];
  for (const model of models) {
    const base = { workflow: gwModel(model), negativePrompt: negFor(model), randomArtist: false, randomPrompt: false, temperature: null, loras: lorasPayload() };
    for (let i = 0; i < n; i++) {
      // Each slot's shape (the single pick, or its roll from a multi-pick set, #213) resolves against ITS model's
      // aspect map (#212) — the same shape can be different dims per model, and the primary's pair must never ride
      // onto a model whose map doesn't hold it (the server would treat it as a custom size). A map-less model
      // carries no width/height at all, so the server sizes it from its own config — exactly the single-model flow.
      // A Custom size is the exception: the one typed pair goes to every model, and the server snaps it onto any
      // model it doesn't fit, with a notice on that slot.
      const shape = pickAspect();
      let slotOv = ov;
      if (!customActive) {
        const wh = aspectDims(model, shape);
        slotOv = { ...ov };
        delete slotOv.width; delete slotOv.height;
        if (wh) { slotOv.width = wh[0]; slotOv.height = wh[1]; }
      }
      items.push({ ...base, overrides: slotOv, prompt: lockArtist(model, prompt), originalPrompt: prompt });
      slotAspects.push(shape);
    }
  }
  return { items, slotAspects };
}

// Reload/Regenerate from a detail card: kick off a fresh generation with an image's EXACT prompt/model/aspect
// (no new random artist/prompt), without touching the composer's inputs. n>1 sends an exact batch of n. Returns
// true if a generation was started, false if it was refused (busy / missing data).
//
// Submits rec.markerPrompt AND rec.negativePrompt — both stored verbatim at render time, in the marker form a prompt box
// speaks ('#tag, @artist'). rec.prompt is the FINALIZED text (markers stripped, underscores folded): re-submitting that
// renders the same picture but the finalizer can no longer see which segments were tags, so the image comes back with an
// empty marks map — no chips, nothing to bookmark or ban. Dropping the negative (null) would silently re-render the
// image WITHOUT the negative that shaped it, so both come from the image, not the composer.
function regenerate(rec, n) {
  if (busy) { toast("A generation is already running — wait for it to finish before reloading."); return false; }
  const model = rec && rec.modelId && MODELS[rec.modelId];
  if (!model) { toast("Can't reload — that image's workflow isn't available."); return false; }
  const prompt = ((rec.markerPrompt || rec.prompt) || "").trim();
  if (!prompt) { toast("Can't reload — this image has no stored prompt."); return false; }
  const negative = rec.negativePrompt ?? null;   // null = none was submitted; do NOT flatten to ""
  // The image's OWN LoRA stack, so Reload reproduces it exactly rather than using whatever the composer shows now.
  const recLoras = Array.isArray(rec.loras) ? rec.loras.filter(l => l && l.name).map(l => ({ name: l.name, weight: l.weight })) : [];
  n = Math.max(1, n || 1);
  // Reload reproduces a picture, so it never re-rolls: the image's own shape, or the primary pick if it has none. It
  // goes through the SAME shared submit control a fresh Generate uses — one /enqueue job, tracked identically.
  const { items, slotAspects } = buildBatchItems(prompt, model, n, true, rec.aspect || primaryAspect(), negative, recLoras);
  composeSubmit.enqueue({ items, meta: { prompt, model: model.friendly_name, modelId: model.id, slotAspects } });
  return true;
}
window.composerRegenerate = regenerate;   // kept for compatibility / presence checks

// The detail Reload button gets the same picker: a plain click regenerates 1, holding offers 2/4/6/10/✎. onStarted
// fires once a gen kicks off (the lightbox uses it to close). detail.js calls attachComposerRegenerate; its existence
// is how the card decides to show the button at all (composer-only pages). A hold while busy is swallowed —
// regenerate() would only refuse anyway.
window.attachComposerRegenerate = function (btn, rec, onStarted) {
  const go = (n) => { if (regenerate(rec, n) && onStarted) onStarted(); };
  const count = attachCountPicker(btn, { onPick: go, onHold: () => busy });
  btn.addEventListener("click", () => { if (count.opened) { count.opened = false; return; } go(1); });
};

// --- tag & artist autocomplete ('#'/'@', Advanced only) -----------------------------------------
const tagModel = () => { const m = primaryModel(); return (m && m.tagging) ? m : null; };
// The '#'/'@'/'~' autocomplete on the main prompt box. One shared implementation (tagbox.js) — the same one the
// negative box and the whole edit page use — rather than an inlined twin that would drift the moment either side
// changed.
//
// The popup consumes Enter/Tab to accept the highlighted suggestion while it is open, and preventDefault keeps
// that from also inserting a newline. Nothing downstream competes for Enter any more: it does not generate.
const promptTags = initTagBox({
  input: $prompt, pop: $tagPop, getModel: tagModel, onAccept: savePrefs,
  allowArtist: () => !ARTIST_MODE,   // the artist page locks the artist; a second one would just be excluded
});
// The model picker drops the popup when the selection changes (a suggestion list ranked for the old model is
// worse than none). Nothing else needs to reach into it — navigation and Enter are the popup's own.
const closeTagPop = () => promptTags.close();

function wantsRandomArtist(model) {
  if (ARTIST_MODE) return false;   // the artist is fixed to the page's artist
  const tg = model && model.tagging; return !!(tg && tg.artists && $randomArtist && $randomArtist.checked);
}
// The slider IS the on/off switch: 0 means don't randomize at all, so there's no separate checkbox to consult.
function wantsRandomPrompt(model) { return !!(model && model.tagGeneratorEnabled && promptTempValue() > 0); }

// --- optional negative prompt -------------------------------------------------------------------
// The negative prompt is offered only for models whose card declares support (negativeSupported). The field
// shows when ANY checked model supports it; the gateway drops the negative for any slot whose model doesn't
// (and for distilled cfg<=1 models), so sending it broadly is safe. Whatever's typed is APPENDED to the model's
// built-in default negative server-side (ComfyGraph.ComposeNegative) — never replaces it; a blank negative just
// yields the default.
function anyNegativeSupported() { return selectedModels().some(m => m && m.negativeSupported); }
function updateNegativeField() { if ($negWrap) $negWrap.hidden = !anyNegativeSupported(); }
function currentNegative() { return $negPrompt ? $negPrompt.value.trim() : ""; }
// The per-model negative to submit: the composer's text for a supporting model (blank => null => the model's default
// alone; non-blank => appended to it server-side), or null for a model that doesn't support one. Never carries the
// composer's negative onto an exact Reload.
function negFor(model) { return (model && model.negativeSupported && currentNegative()) ? currentNegative() : null; }
// Same booru '#'/'@' autocomplete the positive prompt has, on the negative box. Uses the shared tagbox module
// gated on the primary model's tagging; onAccept persists the draft.
if ($negPrompt && $negTagPop) initTagBox({ input: $negPrompt, pop: $negTagPop, getModel: primaryModel, onAccept: savePrefs });
// Random-prompt strength: ONE per-generation slider where 0 is off and anything above it is the tag model's sampling
// temperature (1 = its natural sampling, 5 = wildest). One slider rather than a checkbox plus a separate account-level
// temperature on the Settings page — two controls for one idea, neither reachable while composing. The value rides in
// the composer prefs blob, so it follows the user across devices like the rest of the draft state. Unset = 0 = off.
function promptTempValue() { return $promptTemp ? Number($promptTemp.value) || 0 : 0; }
function setPromptTemp(v) {
  if (!$promptTemp) return;
  $promptTemp.value = Math.min(5, Math.max(0, Number(v) || 0));
  showPromptTemp();
}
function showPromptTemp() {
  if ($promptTempVal) {
    const t = promptTempValue();
    $promptTempVal.textContent = t > 0 ? t.toFixed(1) : "off";
  }
  syncTagTypesBar();   // the mask below only means anything while the slider is up
}
// Null when off, so a non-random generate never pins a temperature server-side (the gateway then leaves the tag
// model on its own default).
function promptTemp() { const t = promptTempValue(); return t > 0 ? t : null; }

// --- LoRAs (stackable, per-generation) ----------------------------------------------------------
// A collapsible list of [× name ‹ weight ›] rows below the negative prompt. The picker (loraPicker.js) adds files;
// each carries a name (subfolder-qualified lora_name, sent verbatim) and a weight (chevrons step 0.05, any value by keyboard).
// The stack rides in the composer prefs blob and in every generate/enqueue body, and Reload reproduces it exactly.
let loras = [];   // [{ name, weight, clipCapable?, compatible? }]

// The wire shape: name + weight only (the server validates names against the machine's LoRA list and applies them).
// LoRAs are model-specific, so they only apply to a SINGLE selected model — a multi-model generation sends none.
function lorasPayload() { return selectedModels().length === 1 ? loras.map(l => ({ name: l.name, weight: l.weight })) : []; }

// A LoRA's short label: its filename without folder or extension.
function loraLabel(name) { return String(name || "").split(/[\\/]/).pop().replace(/\.(safetensors|ckpt|pt|gguf)$/i, ""); }
function normWeight(v) { return Number.isFinite(v) ? Math.round(v * 100) / 100 : 1.0; }
function fmtWeight(v) { return normWeight(v).toString(); }

function renderLoras() {
  if (!$loraList) return;
  $loraList.innerHTML = "";
  loras.forEach((lora, i) => {
    const row = document.createElement("div");
    row.className = "lora-row" + (lora.compatible === false ? " incompatible" : "");
    // Remove on the far LEFT (before the name), so tweaking the weight can't trigger an accidental delete.
    const rm = document.createElement("button");
    rm.type = "button"; rm.className = "lora-remove"; rm.textContent = "×"; rm.title = "Remove this LoRA";
    rm.addEventListener("click", () => {
      const removed = loras.splice(i, 1)[0];
      if (removed && removed.autoAttach && removed.triggers) removeTriggers(removed.triggers);   // pull its words back out
      renderLoras(); savePrefs();
    });
    const name = document.createElement("span");
    name.className = "lora-name"; name.textContent = lora.displayName || loraLabel(lora.name);
    name.title = lora.name + (lora.clipCapable === false ? " — model-only (no CLIP effect)" : "");
    // Weight stepper: the chevrons step 0.05; the number input takes any value by keyboard (native spinner hidden in CSS).
    const wrap = document.createElement("div"); wrap.className = "num-row lora-weight";
    const dec = document.createElement("button"); dec.type = "button"; dec.textContent = "‹"; dec.setAttribute("aria-label", "Less");
    const inp = document.createElement("input"); inp.type = "number"; inp.step = "0.05"; inp.value = fmtWeight(lora.weight);
    const inc = document.createElement("button"); inc.type = "button"; inc.textContent = "›"; inc.setAttribute("aria-label", "More");
    const commit = v => { lora.weight = normWeight(v); inp.value = fmtWeight(lora.weight); savePrefs(); };
    dec.addEventListener("click", () => commit(lora.weight - 0.05));
    inc.addEventListener("click", () => commit(lora.weight + 0.05));
    inp.addEventListener("change", () => commit(Number(inp.value)));
    wrap.append(dec, inp, inc);
    row.append(rm, name, wrap);
    $loraList.appendChild(row);
  });
  if ($loraCount) $loraCount.textContent = loras.length ? `(${loras.length})` : "";
}

// Add picked files to the stack (default weight 1.0), skipping any already present, then persist. A LoRA whose
// auto-attach is on drops its trigger words into the prompt box on add (visible + editable).
function addLoras(picked) {
  const have = new Set(loras.map(l => l.name));
  for (const p of (picked || [])) {
    const nm = typeof p === "string" ? p : p.name;
    if (!nm || have.has(nm)) continue;
    have.add(nm);
    const triggers = (p && p.triggers) || "";
    const autoAttach = !p || p.autoAttach !== false;
    loras.push({ name: nm, weight: 1.0, clipCapable: p && p.clipCapable, compatible: p && p.compatible, triggers, autoAttach, displayName: p && p.displayName });
    if (autoAttach && triggers) insertTriggers(triggers);
  }
  renderLoras(); savePrefs();
  refreshLoraMeta();
}

// Keep the composer's LoRA rows in step with the server's background CivitAI population (requirement: this fires on
// the gen screen too). Polls /forge/loras/meta for the stacked files — which also KICKS OFF population server-side for
// any not seen yet — and, as each resolves, adopts its CivitAI display name and (for a LoRA added before its trigger
// words were known) backfills + inserts the triggers when auto-attach is on. Non-blocking; nothing here gates a generate.
let loraMetaPoll = null;
function refreshLoraMeta() {
  if (loraMetaPoll) { loraMetaPoll(); loraMetaPoll = null; }
  const names = loras.map(l => l.name);
  if (!names.length || typeof pollLoraMeta !== "function") return;
  loraMetaPoll = pollLoraMeta(names, map => {
    let changed = false;
    for (const l of loras) {
      const m = map[l.name];
      if (!m) continue;
      if (m.displayName && l.displayName !== m.displayName) { l.displayName = m.displayName; changed = true; }
      // Triggers discovered after the LoRA was already in the stack: adopt them, and if auto-attach is on and they
      // were never inserted (it had none at add time), drop them into the prompt now.
      if (m.triggers && !l.triggers) {
        l.triggers = m.triggers;
        if (l.autoAttach) insertTriggers(m.triggers);
        changed = true;
      }
    }
    if (changed) { renderLoras(); savePrefs(); }
  });
}

// The prompt box, as comma-separated segments (trimmed), for inserting/removing a LoRA's trigger words.
function promptSegments() { return $prompt.value.split(",").map(s => s.trim()).filter(Boolean); }
function insertTriggers(text) {
  if (!$prompt || !text) return;
  const have = new Set(promptSegments().map(s => s.toLowerCase()));
  const add = text.split(",").map(s => s.trim()).filter(s => s && !have.has(s.toLowerCase()));
  if (!add.length) return;
  const cur = $prompt.value.replace(/,\s*$/, "").trimEnd();
  $prompt.value = (cur ? cur + ", " : "") + add.join(", ");
  $prompt.dispatchEvent(new Event("change"));   // persists the draft + updates any listeners
}
function removeTriggers(text) {
  if (!$prompt || !text) return;
  const drop = new Set(text.split(",").map(s => s.trim().toLowerCase()).filter(Boolean));
  const kept = promptSegments().filter(s => !drop.has(s.toLowerCase()));
  $prompt.value = kept.join(", ");
  $prompt.dispatchEvent(new Event("change"));
}

// The LoRA accordion is offered ONLY for a single selected image model — a LoRA is model-specific, so stacking one
// across several (differently-architected) models is meaningless. With 0 or 2+ selected the section hides; the stack
// stays in the draft and returns when the user is back to one model.
function updateLoraSection() {
  if (!$loraSection) return;
  const sel = selectedModels();
  $loraSection.hidden = !(sel.length === 1 && sel[0] && sel[0].media === "image");
}

// Collapsible sections (negative prompt, LoRAs): the summary button flips its body and rotates its caret.
function setAccordion(toggle, body, open) {
  if (!toggle || !body) return;
  body.hidden = !open;
  toggle.setAttribute("aria-expanded", open ? "true" : "false");
  toggle.classList.toggle("open", open);
}
function wireAccordion(toggle, body) {
  if (toggle && body) toggle.addEventListener("click", () => setAccordion(toggle, body, body.hidden));
}
wireAccordion($loraToggle, $loraBody);
wireAccordion($negToggle, $negBody);
if ($loraAdd) {
  $loraAdd.addEventListener("click", () => {
    if (typeof window.openLoraPicker !== "function") { toast("LoRA picker unavailable"); return; }
    const sel = selectedModels();
    const pm = sel.length === 1 ? sel[0] : null;   // single-model only — compatibility is judged against this one
    if (!pm) { toast("Pick a single model to add LoRAs."); return; }
    window.openLoraPicker({
      workflow: pm._gw,
      defaultFolder: pm.loraFolder || pm.id,
      current: loras.map(l => l.name),
      onAdd: addLoras,
    });
  });
}

// --- the generation mask: which kinds of tag Random prompt may emit -----------------------------
// A PER-GENERATION control, exactly like the slider it sits under and the rest of the composer: the picked kinds ride
// in the generate/enqueue body as `tagTypes`, and the draft selection rides in the composer prefs blob (so it follows
// the account across devices). A per-generation control rather than an account setting on the Settings page — an
// account setting would sit two pages from the control it qualifies and couldn't vary per batch. A queued job keeps
// the mask it was submitted with.
//
// The options come from the server (the tag model decides which types are suppressible, so nothing is hardcoded), and
// the first-load selection comes from the account's stored mask — which is still what the server falls back to for a
// caller that sends none (the MCP), so an existing user's chips start where they left them.
let tagTypeOptions = [], tagTypesReady = false;
let tagTypesFromPrefs = null;         // the draft selection restored from the prefs blob, if any
const tagTypesPicked = new Set();
function buildTagTypes(settings) {
  const box = $("genTagTypes");
  if (!box || !settings) return;
  tagTypeOptions = settings.generationTagTypeOptions || [];
  if (!tagTypeOptions.length) return;   // nothing switchable — leave the row hidden

  // The draft wins over the account mask: restorePrefs has already run by now, and an empty draft list is a real
  // choice ("none of them"), which is why this tests for the array rather than its length.
  const initial = Array.isArray(tagTypesFromPrefs) ? tagTypesFromPrefs : (settings.generationTagTypes || []);
  tagTypesPicked.clear();
  for (const t of initial) if (tagTypeOptions.includes(t)) tagTypesPicked.add(t);

  box.innerHTML = "";
  for (const type of tagTypeOptions) {
    const btn = document.createElement("button");
    btn.type = "button";   // inside the composer form — must never submit it
    btn.textContent = type.charAt(0).toUpperCase() + type.slice(1);
    btn.className = tagTypesPicked.has(type) ? "active" : "";
    btn.addEventListener("click", () => {
      const wasPicked = tagTypesPicked.has(type);
      wasPicked ? tagTypesPicked.delete(type) : tagTypesPicked.add(type);
      btn.classList.toggle("active", !wasPicked);
      savePrefs();   // draft state, like the slider — nothing is pushed to the account
    });
    box.appendChild(btn);
  }
  tagTypesReady = true;
  syncTagTypesBar();
}
// What goes on the wire: the picked kinds in the server's own option order. Null until the chips exist, which is the
// "not specified" the server answers with the account's stored mask — never an empty array, which means "none of them".
function tagTypes() { return tagTypesReady ? tagTypeOptions.filter(t => tagTypesPicked.has(t)) : null; }
// Shown only when the chips exist AND this model can take random tags at all AND the slider is above 0.
function syncTagTypesBar() {
  const bar = $("tagTypesBar");
  if (!bar) return;
  const sliderUp = !!$randomPromptBar && !$randomPromptBar.hidden && promptTempValue() > 0;
  bar.hidden = !(tagTypesReady && sliderUp);
}

// --- wake lock + busy + cancel ------------------------------------------------------------------
let wakeLock = null;
async function acquireWakeLock() { try { if ("wakeLock" in navigator) wakeLock = await navigator.wakeLock.request("screen"); } catch (e) { console.debug("wake lock request failed:", e); wakeLock = null; } }
function releaseWakeLock() { try { wakeLock && wakeLock.release(); } catch (e) { console.debug("wake lock release failed:", e); } wakeLock = null; }
// Generate STAYS "Generate" while a render runs — clicking it again queues another job (the shared submit control's
// queue-more), so there is no cancel-adjacent gesture to misfire. Cancelling is the dedicated #cancelGen button, shown
// only while busy.
function setBusy(b) {
  busy = b;
  $cancelGen.classList.toggle("show", b);
  if (b) acquireWakeLock(); else releaseWakeLock();
}
function cancelGeneration() { if (!busy || !activeGen) return; cancelRequested = true; setStatus("Cancelling…"); activeGen.cancel(); }

// --- result preview + "image generated" broadcast -----------------------------------------------
// The composer is a component: it previews the just-made image inline (#result) and announces it. Each page owns its
// own Recent strip / grid and listens for `imagegen:generated` to reconcile from history.
// The browser does NOT write history — the worker persists it server-side at completion, before the job/slot is even
// reported done. This is purely the doorbell: "a new image exists, go re-pull /api/history". The payload is a hint
// (the id to highlight), never the source of truth. By the time this fires the row already exists, so the re-pull sees it.
function emitGenerated(rec) {
  document.dispatchEvent(new CustomEvent("imagegen:generated", { detail: rec }));
}

// Open an image in the lightbox via its recent-strip card; fall back to the full detail page if the
// lightbox isn't present or the card isn't in the DOM yet.
function openImage(id) {
  if (!(window.openImgcard && window.openImgcard(String(id)))) location.href = "/image/" + encodeURIComponent(id);
}
function showResult(r) {
  $result.innerHTML = "";
  const card = document.createElement("div"); card.className = "result-card";
  // A video model's result is a clip (animated webp) — show it as a looping <video> from the mp4 endpoint, the same
  // way the library does. Everything else is a still <img>. The record's own media/hasAudio (stated by the server on
  // the job/slot payload) win over the MODELS lookup: an ADOPTED job's model may not be in this page's map at all
  // (compose adopting an edit/animate job), and guessing "still" there rendered clips as <img> — which media.js then
  // swapped for a hover-play <video> with no click handler.
  const m = MODELS[r.modelId];
  const isVid = r.media ? r.media === "video" : !!(m && m.media === "video");
  const hasAudio = r.hasAudio != null ? !!r.hasAudio : !!(m && m.hasAudio);
  let img;
  if (isVid) {
    img = document.createElement("video");
    img.src = `${GATEWAY}/image/${encodeURIComponent(imageId(r))}/mp4`;
    img.loop = true; img.muted = true; img.autoplay = true; img.playsInline = true;
    img.setAttribute("muted", ""); img.setAttribute("playsinline", ""); img.preload = "metadata";
    // Clips with a native audio track (H3) autoplay muted like the rest (browsers require it), but get controls so the
    // user can unmute and hear the generated audio. Silent clips stay chrome-free.
    if (hasAudio) img.controls = true;
  } else {
    img = document.createElement("img"); img.src = viewUrl(r); img.alt = r.prompt || "";
  }
  img.style.cursor = "pointer";
  img.addEventListener("click", () => openImage(imageId(r)));
  const actions = document.createElement("div"); actions.className = "result-actions";
  if (hasEditors) {
    const ed = document.createElement("a"); ed.className = "link-btn"; ed.textContent = "✎ Edit this"; ed.href = "/edit/" + encodeURIComponent(imageId(r)); ed.style.marginRight = "auto"; actions.appendChild(ed);
  }
  const dl = document.createElement("a"); dl.className = "download"; dl.href = "#"; dl.textContent = "↓ Save image";
  dl.onclick = e => { e.preventDefault(); saveMedia(r, isVid); };
  actions.appendChild(dl); card.appendChild(img); card.appendChild(actions);
  // A non-fatal server notice on the slot (e.g. a custom size snapped to what this model supports, #212): the same
  // amber line the edit page's result card shows, under the preview it belongs to.
  if (r.notice) { const nt = document.createElement("div"); nt.className = "result-notice"; nt.textContent = "⚠ " + r.notice; card.appendChild(nt); }
  $result.appendChild(card);
}
// `aspect` is the shape this image was actually SUBMITTED with (rolled per slot when several shapes are picked) —
// it's what Reload re-uses, so it must be the slot's own, not whatever the composer happens to show now.
function recordResult(result, prompt, modelFriendly, modelId, aspect) {
  const r = { ts: Date.now(), prompt: (result && result.effectivePrompt) || prompt || "", marks: (result && result.marks) || null, model: modelFriendly || "", modelId: modelId || "", aspect: aspect || primaryAspect(), id: result.id, notice: (result && result.notice) || null };
  showResult(r); emitGenerated(r);
}
// uploadToInput (device photo → upload → /edit/{id}) is shared from core.js.
// --- prefs (per-user account setting, follows the user across devices) ---------------------------
// The composer's draft state lives on the account, not in this browser, so a second machine restores the
// same prompt/model/aspect/toggles. Writes are debounced (these fire on change/toggle, not per keystroke).
let prefsTimer = null;
// False until the stored blob has actually been read back (see boot). Gates every write below.
let composerPrefsLoaded = false;
function savePrefs() {
  // Never write before the stored draft has been read back. savePrefs sends the WHOLE composer state, so a save that
  // runs while the stored blob is unknown replaces the user's draft prompt with an empty box — the same hazard the
  // tagTypes note below guards, one level up.
  if (!composerPrefsLoaded) return;
  collectComposerParamPrefs();   // capture the live knob values so EVERY save is authoritative
  // `aspect` (the single primary) stays alongside `aspects` so an older client — or one that hasn't seen a multi
  // pick yet — still restores a sensible shape from the same blob.
  // tagTypes: null while the chips haven't been built (a save that early must not overwrite the stored draft with
  // "none of them" — the empty array is a real selection).
  const json = JSON.stringify({ prompt: $prompt.value, negativePrompt: $negPrompt ? $negPrompt.value : "", modelIds: selectedModelIds(), aspect: primaryAspect(), aspects: aspects.slice(), custom: customActive, customW: customActive ? customDim($genW) || null : null, customH: customActive ? customDim($genH) || null : null, randomArtist: !!($randomArtist && $randomArtist.checked), randomPromptTemp: promptTempValue(), tagTypes: tagTypes() ?? tagTypesFromPrefs, params: paramPrefs, loras: loras.map(l => ({ name: l.name, weight: l.weight, triggers: l.triggers, autoAttach: l.autoAttach, displayName: l.displayName })) });
  clearTimeout(prefsTimer);
  // This blob holds the user's draft PROMPT, so a silent failure means they keep typing into a composer that is no
  // longer being kept, and find an older draft on the next load.
  prefsTimer = setTimeout(() => {
    saveComposerPrefs(json).catch(e => {
      console.error("Composer state could not be saved:", e);
      toast("Couldn't save your composer draft");
    });
  }, 400);
}
// The picked set, in pick order: [0] is the primary (what a single-shape record falls back to). Always non-empty.
function setAspects(list) {
  const next = [];
  for (const a of (list || [])) if (ASPECTS.includes(a) && !next.includes(a)) next.push(a);
  aspects = next.length ? next : ["square"];
  for (const b of $aspect.children) b.classList.toggle("active", aspects.includes(b.dataset.aspect));
  updateSizeControlsVisibility();
}
function addAspect(a) { setAspects(aspects.concat([a])); }
function restorePrefs(p) {
  if (!p) return;
  // Seed the flat override map BEFORE the model selection below triggers renderParams, so the restored knob values
  // (polish_denoise, cfg, steps, ...) are applied onto the freshly-rendered fields.
  if (p.params && typeof p.params === "object") paramPrefs = p.params;
  // A pre-#209 blob may carry stale width/height (they were collectable prefs then); the size is model-derived now.
  delete paramPrefs.width;
  delete paramPrefs.height;
  // The full picked set restores (#213); `aspect` is the legacy single-shape fallback.
  if (Array.isArray(p.aspects) && p.aspects.length) setAspects(p.aspects);
  else if (p.aspect) setAspects([p.aspect]);
  // modelIds (multi) is current; fall back to the legacy single modelId. setSelectedIds refreshes the panel.
  const ids = (Array.isArray(p.modelIds) ? p.modelIds : (p.modelId ? [p.modelId] : [])).filter(id => MODELS[id] && !modelHidden.has(id));
  if (ids.length) modelPicker.setSelectedIds(ids);
  // Re-activate Custom only after the selection resolved (updateCustomShape ran) and only if the workflow still offers
  // it. The stored custom size goes straight into the single submitted pair; the renderParams below re-renders the
  // forced size fields carrying it.
  if (p.custom && customCapable()) {
    setCustomActive(true);
    if (p.customW || p.customH) writeSize(p.customW || "", p.customH || "");
  }
  if (p.prompt && !$prompt.value) $prompt.value = p.prompt;
  if ($negPrompt && p.negativePrompt != null && !$negPrompt.value) $negPrompt.value = p.negativePrompt;
  if ($negPrompt && $negPrompt.value.trim()) setAccordion($negToggle, $negBody, true);   // don't bury an existing negative
  if ($randomArtist) $randomArtist.checked = !!p.randomArtist;
  setPromptTemp(typeof p.randomPromptTemp === "number" ? p.randomPromptTemp : 0);
  // Held for buildTagTypes (which runs after this, once the options are known). Absent = never set from a composer,
  // so the chips seed from the account's stored mask instead.
  if (Array.isArray(p.tagTypes)) tagTypesFromPrefs = p.tagTypes;
  if (Array.isArray(p.loras)) { loras = p.loras.filter(l => l && l.name).map(l => ({ name: l.name, weight: normWeight(l.weight), triggers: l.triggers || "", autoAttach: l.autoAttach !== false, displayName: l.displayName })); renderLoras(); refreshLoraMeta(); }
  renderParams();   // re-apply the restored param map even if the model selection didn't change
}
// Shape gestures, mirroring the style picker (#213): a tap picks exactly ONE shape (collapsing any multi-pick back
// down) and writes its dims into the (hidden) width/height that get submitted (#209), then resets megapixels to the
// model budget; a ~450ms hold ADDS the held shape to the set — each slot then rolls its own at build time. A press
// that turns into a hold must not also run the tap handler, so the completed hold swallows the click it releases
// into. The Custom chip is a state indicator (#225), exclusive with a multi-pick: clicking it selects Custom
// (keeping whatever the size fields show); an aspect click deselects it. No gesture re-renders the params panel.
const ASPECT_HOLD_MS = 450;   // same dwell as the style picker / count picker
let aspHoldTimer = null, aspHeld = false, aspX = 0, aspY = 0;
$aspect.addEventListener("pointerdown", e => {
  const b = e.target.closest("button"); if (!b) return;
  aspHeld = false; aspX = e.clientX; aspY = e.clientY; clearTimeout(aspHoldTimer);
  if (b === $aspectCustom) return;   // Custom is a single exclusive size — no hold-to-add-a-set gesture
  aspHoldTimer = setTimeout(() => {
    aspHeld = true;
    setCustomActive(false);   // a multi-pick and Custom are mutually exclusive; the add wins
    addAspect(b.dataset.aspect);
    savePrefs();
  }, ASPECT_HOLD_MS);
});
$aspect.addEventListener("pointermove", e => { if (aspHoldTimer && (Math.abs(e.clientX - aspX) > 10 || Math.abs(e.clientY - aspY) > 10)) { clearTimeout(aspHoldTimer); aspHoldTimer = null; } });
["pointerup", "pointerleave", "pointercancel"].forEach(ev => $aspect.addEventListener(ev, () => { clearTimeout(aspHoldTimer); aspHoldTimer = null; }));
$aspect.addEventListener("contextmenu", e => e.preventDefault());   // no callout on a mobile long-press
$aspect.addEventListener("click", e => {
  const b = e.target.closest("button"); if (!b) return;
  if (aspHeld) { aspHeld = false; return; }
  if (b === $aspectCustom) { setCustomActive(true); syncMpFromWH(); savePrefs(); return; }
  setCustomActive(false); setAspects([b.dataset.aspect]); writeAspectSize(b.dataset.aspect); resetMpFromAspect(); savePrefs();
});
$prompt.addEventListener("change", savePrefs);
if ($negPrompt) $negPrompt.addEventListener("change", savePrefs);
$randomArtist.addEventListener("change", savePrefs);
// Dragging repaints the readout live; only the settled value is persisted (change), so a drag isn't 50 PUTs.
if ($promptTemp) { $promptTemp.addEventListener("input", showPromptTemp); $promptTemp.addEventListener("change", savePrefs); }
document.addEventListener("visibilitychange", () => { if (busy && document.visibilityState === "visible") acquireWakeLock(); });
// The Generate button + its form submit + hold-to-count are wired by the shared submit control (composeSubmit above).
$cancelGen.addEventListener("click", () => cancelGeneration());
// Enter does NOT generate. A prompt is prose that wants paragraphs, and a key that submits it is a key that
// submits it half-written — the button is the only way to start work. The only Enter this box treats specially
// belongs to the suggestion popup, which consumes it to accept the highlighted tag while it is open.

// --- live cross-device sync (server-sourced; the running gen mirrors onto every device) ----------
// The server is the source of truth for live state. /forge/jobs returns ONLY this user's ACTIVE jobs (a finalized
// job has LEFT the feed). This tracker DIFFS successive reads, exactly as the job is a projection of ComfyUI's state:
//   - a NEW id appearing in a job's positional imageIds[] means a new image exists -> announce it (highlight + a
//     re-pull of history, which is the real source of truth for the strips);
//   - a job we were tracking VANISHING from the feed means it finalized -> fetch /forge/job/{id} for its final array
//     to catch any straggler, then signal a history reconcile.
// It never renders a job payload AS history and never assumes completion: an image is shown because the job's array
// grew, and the strip's truth is always /api/history (so deletes stick and nothing resurrects).
let liveWs = null;
const watching = new Set();        // active job ids currently being tracked (to detect a vanish)
const announcedIds = new Set();    // image ids already announced this session (dedupe the diff)

function announceImage(job, id) {
  if (!id || announcedIds.has(id)) return;
  announcedIds.add(id);
  const slot = (job.slots || []).find(s => String(s.id) === String(id)) || {};
  const model = (MODELS[job.model] && MODELS[job.model].friendly_name) || job.model || "";
  const rec = { ts: Date.now(), prompt: slot.effectivePrompt || job.prompt || "", marks: slot.marks || null, model, modelId: job.model || "", aspect: primaryAspect(), id };
  // Doorbell only: the top preview is painted by whoever OWNS the job's panel — recordResult for THIS tab's own
  // Generate, showAdoptedResult for a recovered/remote one (attachLiveRecover) — never from this diff loop.
  document.dispatchEvent(new CustomEvent("imagegen:generated", { detail: rec }));
}

// A tracked job left the active feed -> it finalized. Collect its final image array (catch a straggler we polled past)
// and signal a history reconcile. If this device was reflecting it as the active gen, clear that.
async function finalizeJob(jobId) {
  try {
    const r = await fetch(`${GATEWAY}/job/${encodeURIComponent(jobId)}`);
    if (r.ok) { const j = await r.json(); for (const id of (j.imageIds || [])) if (id) announceImage(j, id); }
  } catch (e) { console.debug("finalizeJob straggler fetch failed:", e); }
  document.dispatchEvent(new CustomEvent("imagegen:refresh"));   // strips re-pull /api/history (authoritative)
}

async function liveSync() {
  let res; try { const r = await fetch(`${GATEWAY}/jobs`); if (!r.ok) return; res = await r.json(); } catch (e) { console.debug("job poll failed:", e); return; }
  const jobs = res.jobs || [];
  const activeIds = new Set(jobs.map(j => j.jobId));

  // Diff: announce any newly-produced image in each active job; detect tracked jobs that have vanished (finalized).
  for (const j of jobs) { watching.add(j.jobId); for (const id of (j.imageIds || [])) announceImage(j, id); }
  for (const jobId of [...watching]) if (!activeIds.has(jobId)) { watching.delete(jobId); finalizeJob(jobId); }

  // The strip's window is a server-side fact (/api/recents reads the batch off the job table), so this file does not
  // publish an `imagegen:batch` event to size the Recent strip to the work in flight. Assembling that size here would
  // make it live only in a tab that watched the batch happen, so a reload after it finished would silently crop the
  // last batch. announceImage's `imagegen:generated` remains the trigger to re-pull; what to show is not this file's
  // to decide.
}

// The bar / preview / cancel for an in-flight job (this tab's own OR one already running when we arrive) are driven by
// the shared recovery path below (attachLiveRecover). This ws only makes the announce/finalize poll PROMPT — a finish
// event triggers an immediate liveSync instead of waiting for the next tick; the bar has its own ws inside the tracker.
function liveOpenWs() {
  if (liveWs) return;
  try {
    liveWs = new WebSocket(gwWs("/ws"));
    liveWs.onmessage = (ev) => {
      if (typeof ev.data !== "string") return;
      let m; try { m = JSON.parse(ev.data); } catch (e) { console.debug("live ws non-JSON message:", e); return; }
      if (m.type === "executed" || m.type === "execution_error" || m.type === "execution_success") liveSync();
    };
    liveWs.onclose = () => { liveWs = null; };
    liveWs.onerror = (ev) => { console.debug("live ws error:", ev); try { liveWs && liveWs.close(); } catch (e) { console.debug("ws close failed:", e); } liveWs = null; };
  } catch (e) { console.debug("live ws open failed:", e); liveWs = null; }
}

// Paint an adopted job's finished image in the top preview. Mirrors recordResult, but sources its fields from the job
// itself — a fresh Generate carries `meta` (prompt/model/shapes); a recovered or cross-device job does not. On an
// artist page the preview obeys the same belongs-here rule as the grid (belongsToArtistPage), so a gen made elsewhere
// without this artist reports progress on the bar but isn't previewed here.
function showAdoptedResult(job, s) {
  if (!s || !s.id) return;
  const model = (MODELS[job.model] && MODELS[job.model].friendly_name) || job.model || "";
  // media/hasAudio come from the SLOT (the server states them per slot): an adopted job's model — e.g. an edit/animate
  // job picked up by this page — has no MODELS entry here, and a lookup miss must not demote a clip to a still.
  const rec = { ts: Date.now(), prompt: s.effectivePrompt || job.prompt || "", marks: s.marks || null, model, modelId: job.model || "", media: s.media || job.media, hasAudio: s.hasAudio != null ? s.hasAudio : job.hasAudio, aspect: primaryAspect(), id: s.id, notice: s.notice || null };
  if (!ARTIST_MODE || belongsToArtistPage(rec.marks, LOCKED_ARTIST)) showResult(rec);
}

function startLiveSync() {
  liveSync(); liveOpenWs();
  setInterval(() => { liveSync(); liveOpenWs(); }, 2500);
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") { liveSync(); liveOpenWs(); } });
  // The composer previews EVERY adopted job (each gen is a composer result) — the only surface with no relevance filter.
  attachLiveRecover({
    isBusy: () => busy,
    onAdopt: () => { setBusy(true); showBar(0.02); },
    options: job => ({
      eta: $("eta"),
      previewTarget: $result,
      onProgress: showBar,
      onRunning: showRunningModel,
      onSlot: s => showAdoptedResult(job, s),
      activeStatus: composerCreatingStatus,
      finalStatus: composePanel.finalStatus,
      setStatus,
      onCancelHandle: h => { activeGen = h; },
      onSettle: () => { hideBar(); setBusy(false); },
    }),
  });
}

// --- boot ---------------------------------------------------------------------------------------
(async () => {
  await loadModels();
  // Composer state comes from the account now (per user, cross-device), not localStorage.
  //
  // composerPrefsLoaded gates every later write. Resolving either failure below to "no stored state" — a failed GET
  // becoming null, an unreadable blob becoming an empty catch — would let the composer persist its blank defaults over
  // the user's real draft the moment they touched anything. Absent state and unknown state are not the same thing,
  // and only the first one is safe to overwrite.
  let s = null;
  try {
    s = await fetchSettings();
  } catch (e) {
    console.error("Composer state could not be loaded; it will be left untouched:", e);
    toast("Your saved composer draft couldn’t be loaded — reload before editing");
  }
  if (s) {
    if (s.composerPrefs) {
      try {
        restorePrefs(JSON.parse(s.composerPrefs));
        composerPrefsLoaded = true;
      } catch (e) {
        console.error("Stored composer state is not readable; it will be left untouched:", e);
        toast("Your saved composer draft couldn’t be read — reload before editing");
      }
    } else {
      composerPrefsLoaded = true;   // nothing stored yet: a first save is creating it, not overwriting one
    }
  }
  buildTagTypes(s);   // same response carries the generation mask + its options
  if (s) setTagBoxPinBookmarks(s.pinBookmarks);   // one account toggle governs every tag box on this page
  // Favorited/banned marks in the '#'/'@' popup: a one-time snapshot each, applied when they resolve. Detached (not
  // awaited) so the cosmetic decoration never gates boot, and left un-caught so a real endpoint failure surfaces in
  // the console rather than being swallowed — a missing set just means no star/cross that keystroke, nothing worse.
  fetchBookmarks().then(setTagBoxFavorites);
  fetchAllBans().then(setTagBoxBans);
  startLiveSync();
})();
