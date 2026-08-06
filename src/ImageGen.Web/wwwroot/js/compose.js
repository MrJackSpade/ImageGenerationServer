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
// Shape is a SET, not one value: a tap picks exactly one, a long-press ADDS one (the style picker's gesture). With
// two or more picked, every picture ROLLS its own shape — so each slot of a batch is submitted with, and recorded
// under, the shape it was actually made at (Reload then reuses that one, not whichever is first in the set).
const ASPECTS = ["square", "landscape", "portrait"];
let aspects = ["square"];
const primaryAspect = () => aspects[0] || "square";
// While Custom is active the flat width/height overrides carry the size, so a valid aspect string is still submitted
// (the graph ignores the map for that job) — "square" keeps NormalizeAspect happy.
const pickAspect = () => customActive ? "square" : (aspects[Math.floor(Math.random() * aspects.length)] || "square");
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
  if ($randomPromptBar) $randomPromptBar.hidden = !(m && m.tagging && m.tagging.tags);
  syncTagTypesBar();   // the mask hides with the slider when the model can't take random tags
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
    if (help.link && help.link.url)
      note += ` <a href="${encodeURI(help.link.url)}" target="_blank" rel="noopener noreferrer">${escapeHtml(help.link.text || "Learn more")}</a>`;
    parts.push(`<div class="mi-note">${note}</div>`);
  }
  $modelTip.innerHTML = parts.join("");
  $modelTip.hidden = false;
  renderParams(m);
}


// --- custom size (per-workflow toggle, #150) -----------------------------------------------------
// A workflow whose settings page enabled "Custom size" (r.customSizeEnabled) gets a "Custom" aspect on the shape
// row. Picking it reveals width/height boxes; on submit their values ride as flat width/height overrides (with the
// aspect map emptied so the flat size wins in the graph's Dims()), and the server validates the pair against the
// model's resolution envelope. Offered only for a SINGLE picked generate workflow — "the current workflow" is
// meaningless with a multi-pick, each of which carries its own sizes.
const $aspectCustom = $("aspectCustom"), $customSize = $("customSize"),
      $customW = $("customW"), $customH = $("customH"), $customSizeNote = $("customSizeNote");
let customActive = false, customEnv = null;

// The single generate workflow whose custom sizing we'd offer, or null (0 or 2+ picked, or it hasn't enabled it).
function customCapable() { const m = primaryModel(); return (m && m.kind === "generate" && m.customSizeEnabled) ? m : null; }
const customDim = el => { const n = parseInt(el && el.value, 10); return Number.isFinite(n) && n > 0 ? n : 0; };
// True only when Custom is active with both boxes filled — the gate the submit path checks before enqueueing.
function customReady() { return customActive && customDim($customW) > 0 && customDim($customH) > 0; }

function updateCustomShape() {
  const on = !!customCapable();
  if ($aspectCustom) $aspectCustom.classList.toggle("hidden", !on);
  if (!on && customActive) setCustomActive(false);   // selection moved to a workflow that doesn't offer custom sizing
}

function setCustomActive(on) {
  customActive = on;
  if ($customSize) $customSize.hidden = !on;
  if ($aspectCustom) $aspectCustom.classList.toggle("active", on);
  if (on) {
    for (const b of $aspect.children) if (b !== $aspectCustom) b.classList.remove("active");   // Custom is exclusive
    loadCustomEnv();
  } else {
    setAspects(aspects);   // restore the normal shape's active markers
  }
}

// Bound the width/height boxes by the model's declared envelope and show the same note the settings size editor does.
async function loadCustomEnv() {
  const m = customCapable(); if (!m) return;
  try {
    const r = await fetch(`${GATEWAY}/catalog/config/${encodeURIComponent(m.id)}/settings`);
    if (!r.ok) return;
    customEnv = (await r.json()).resolution || null;
  } catch (e) { console.debug("custom size envelope load failed:", e); return; }
  if (!customEnv) { if ($customSizeNote) $customSizeNote.textContent = ""; return; }
  for (const [el, lo, hi] of [[$customW, customEnv.minW, customEnv.maxW], [$customH, customEnv.minH, customEnv.maxH]]) {
    if (!el) continue;
    el.min = lo; el.max = hi; el.step = customEnv.step;
  }
  if ($customSizeNote) $customSizeNote.textContent =
    `This model supports ${customEnv.minW}–${customEnv.maxW} wide and ${customEnv.minH}–${customEnv.maxH} tall, in multiples of ${customEnv.step}.`;
}

if ($customW) $customW.addEventListener("change", savePrefs);
if ($customH) $customH.addEventListener("change", savePrefs);

// Adapt a /workflows configuration row into the model shape the rest of this page expects. The server already
// resolved presence + VRAM, so a returned row is runnable on this machine; `_gw` is the configuration id the
// client submits as `model`. `exposedParams` are the configuration's UI-exposed parameters (steps/cfg/...).
function adaptWorkflow(r) {
  const c = r.card || {};
  return {
    id: r.id, friendly_name: r.friendlyName || r.id, _gw: r.id, default: !!r.default, avgSeconds: r.avgSeconds,
    kind: r.kind, media: r.media === "video" ? "video" : "image", hasAudio: !!r.hasAudio, exposedParams: r.exposedParams || [],
    loraFolder: r.loraFolder || "",   // the workflow's default LoRA-picker folder (Part H); "" = smart-route by id
    negativeSupported: c.negativeSupported === true,   // model's card declares it uses a negative prompt
    speed: { class: c.speed }, nsfw_capable: c.nsfwCapable,
    prompt: { example: c.example, required_prefix: c.requiredPrefix },
    ui_help: { good_for: c.uiGoodFor, note: c.uiNote, link: c.uiLink || null },
    tagging: c.tagging || null,
    customSizeEnabled: !!r.customSizeEnabled
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
    hasEditors = all.some(m => m.kind === "edit");
    setStatus("");
  } catch (e) { $modelToggle.textContent = "Unavailable"; setStatus(friendlyError(e), { error: true }); }
}

// Compose-page bindings of the shared param helpers (defined in core.js) to the side-pane #modelParams container.
// paramPrefs is the FLAT by-name override map persisted in the account prefs blob (like the edit page) so every
// exposed knob (steps/cfg/polish_denoise/...) survives a reload. renderParams re-applies it after each rebuild;
// changing any field merges back into it and persists (debounced via savePrefs).
let paramPrefs = {};
const renderParams = () => { const box = document.getElementById("modelParams"); renderParamFields(box, selectedModels()); applyParamPrefs(box, paramPrefs); };
function currentOverrides() {
  const ov = readOverrides(document.getElementById("modelParams")) || {};
  // Custom size rides as flat width/height overrides, with the aspect map emptied so the flat size wins in Dims().
  // The server re-validates the pair against the model's resolution envelope and refuses a bad one with its numbers.
  if (customReady()) { ov.width = customDim($customW); ov.height = customDim($customH); ov.aspect = {}; }
  return ov;
}
// Capture on BOTH input (fires live per keystroke/spinner tick) and change (commit) — a number input only fires
// "change" on blur, which can be missed, so "input" is what makes edits reliably persist.
["input", "change"].forEach(ev => document.getElementById("modelParams").addEventListener(ev, () => { collectParamPrefs(document.getElementById("modelParams"), paramPrefs); savePrefs(); }));

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
const composePanel = {
  eta: $("eta"),
  show: b => { if (b) showBar(0.02); else hideBar(); },
  onProgress: showBar,   // also drives the tab-title/favicon ring
  // `meta` is THIS submission's context (prompt/model/shapes), threaded by the control — so a queue-more job (its own
  // meta) can never make the running job record its slots against the wrong prompt/model.
  onSlot: (s, meta) => recordResult({ id: s.id, effectivePrompt: s.effectivePrompt, marks: s.marks }, meta.prompt, meta.model, meta.modelId, (meta.slotAspects && meta.slotAspects[s.index]) || ""),
  onRunning: (runSlot, _job, meta) => { if (runSlot && meta.slotModels) setGenModel(meta.slotModels[runSlot.index] || ""); },   // multi-model: show the current model
  activeStatus: (recorded, total) => `Creating ${Math.min(recorded + 1, total)} of ${total}…`,   // 1-indexed: the one being made now
  // The job's OWN final status, not this tab's cancel flag: it may have been stopped from another device, and the
  // missing images weren't ones that "couldn't be made" — they weren't asked for any more.
  finalStatus: (made, total, cancelled) => cancelled ? (made ? `Cancelled — made ${made} of ${total}.` : "Cancelled.")
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
  // Explode: {a|b} sets fan the prompt into one variant per option, multiplying across sets, models, and the batch.
  // Warn before a genuinely multiplicative run (2+ sets) with the real total; a single set just makes one-of-each.
  const info = explodeInfo(prompt);
  if (info.groupCount >= 2) {
    const total = info.combos * n * models.length;
    if (!confirm(`This prompt has ${info.groupCount} explode sets — it will create ${total} generations. Continue?`)) return [];
  }
  if (models.length === 1) {
    const model = models[0];
    const { items, slotAspects } = buildBatchItems(prompt, model, n);
    return { items, meta: { prompt, model: model.friendly_name, modelId: model.id, slotModels: null, slotAspects } };
  }
  const { items, slotModels, slotAspects } = buildMultiItems(prompt, models, n);
  return { items, meta: { prompt, model: `${models.length} workflows`, modelId: "", slotModels, slotAspects } };
}

// The slots for one model: every explode variant ({a|b} sets), n times each, each rolling its own aspect from the
// picked set (so a batch comes back mixed when several shapes are selected). `exact` (Reload) reproduces a picture
// verbatim — the image's own prompt/negative/loras/shape, no new random artist/prompt and no re-roll.
function buildBatchItems(prompt, model, n, exact, aspect, negative, loras) {
  const rollAspect = () => aspect || pickAspect();
  const slotAspects = [];   // slotAspects[i] = the shape slot i was submitted with (= slot.index), for the record
  const ov = currentOverrides();
  let items;
  if (exact) {
    // `negative ?? null` keeps "no negative was submitted" distinct from an empty one; the image's OWN LoRA stack.
    const one = { workflow: gwModel(model), prompt, originalPrompt: prompt, negativePrompt: negative ?? null, randomArtist: false, randomPrompt: false, temperature: null, overrides: ov, loras: loras || [] };
    items = Array.from({ length: n }, () => { const asp = rollAspect(); slotAspects.push(asp); return { ...one, aspect: asp }; });
  } else {
    const base = { workflow: gwModel(model), negativePrompt: negFor(model), randomArtist: wantsRandomArtist(model), randomPrompt: wantsRandomPrompt(model), temperature: promptTemp(), tagTypes: tagTypes(), overrides: ov, loras: lorasPayload() };
    items = [];
    for (const variant of explodePrompts(prompt))
      for (let i = 0; i < n; i++) {
        const asp = rollAspect(); slotAspects.push(asp);
        items.push({ ...base, aspect: asp, prompt: lockArtist(model, expandRandomPrompt(variant)), originalPrompt: prompt });
      }
  }
  return { items, slotAspects };
}
// Fan ONE prompt across several models — n slots per model. The shared-param panel (params common to every selected
// model) applies to all; random artist/prompt stay single-model affordances (off here). Artist-mode locks per model.
function buildMultiItems(prompt, models, n) {
  const ov = currentOverrides();
  const items = [], slotModels = [], slotAspects = [];   // [i] = friendly name / shape for slot i, in submission order (= slot.index)
  for (const model of models) {
    const base = { workflow: gwModel(model), negativePrompt: negFor(model), randomArtist: false, randomPrompt: false, temperature: null, overrides: ov, loras: lorasPayload() };
    for (const variant of explodePrompts(prompt))
      for (let i = 0; i < n; i++) {
        const asp = pickAspect();   // each slot rolls its own shape from the picked set
        items.push({ ...base, aspect: asp, prompt: lockArtist(model, expandRandomPrompt(variant)), originalPrompt: prompt });
        slotModels.push(model.friendly_name); slotAspects.push(asp);
      }
  }
  return { items, slotModels, slotAspects };
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
  composeSubmit.enqueue({ items, meta: { prompt, model: model.friendly_name, modelId: model.id, slotModels: null, slotAspects } });
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
function wantsRandomPrompt(model) { const tg = model && model.tagging; return !!(tg && tg.tags && promptTempValue() > 0); }

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
  // way the library does. Everything else is a still <img>.
  const m = MODELS[r.modelId];
  const isVid = !!(m && m.media === "video");
  let img;
  if (isVid) {
    img = document.createElement("video");
    img.src = `${GATEWAY}/image/${encodeURIComponent(imageId(r))}/mp4`;
    img.loop = true; img.muted = true; img.autoplay = true; img.playsInline = true;
    img.setAttribute("muted", ""); img.setAttribute("playsinline", ""); img.preload = "metadata";
    // Clips with a native audio track (H3) autoplay muted like the rest (browsers require it), but get controls so the
    // user can unmute and hear the generated audio. Silent clips stay chrome-free.
    if (m.hasAudio) img.controls = true;
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
  actions.appendChild(dl); card.appendChild(img); card.appendChild(actions); $result.appendChild(card);
}
// `aspect` is the shape this image was actually SUBMITTED with (rolled per slot when several shapes are picked) —
// it's what Reload re-uses, so it must be the slot's own, not whatever the composer happens to show now.
function recordResult(result, prompt, modelFriendly, modelId, aspect) {
  const r = { ts: Date.now(), prompt: (result && result.effectivePrompt) || prompt || "", marks: (result && result.marks) || null, model: modelFriendly || "", modelId: modelId || "", aspect: aspect || primaryAspect(), id: result.id };
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
  collectParamPrefs(document.getElementById("modelParams"), paramPrefs);   // capture the live knob values so EVERY save is authoritative
  // `aspect` (the single primary) stays alongside `aspects` so an older client — or one that hasn't seen a multi
  // pick yet — still restores a sensible shape from the same blob.
  // tagTypes: null while the chips haven't been built (a save that early must not overwrite the stored draft with
  // "none of them" — the empty array is a real selection).
  const json = JSON.stringify({ prompt: $prompt.value, negativePrompt: $negPrompt ? $negPrompt.value : "", modelIds: selectedModelIds(), aspect: primaryAspect(), aspects: aspects.slice(), custom: customActive, customW: customDim($customW) || null, customH: customDim($customH) || null, randomArtist: !!($randomArtist && $randomArtist.checked), randomPromptTemp: promptTempValue(), tagTypes: tagTypes() ?? tagTypesFromPrefs, params: paramPrefs, loras: loras.map(l => ({ name: l.name, weight: l.weight, triggers: l.triggers, autoAttach: l.autoAttach, displayName: l.displayName })) });
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
}
function addAspect(a) { setAspects(aspects.concat([a])); }
function restorePrefs(p) {
  if (!p) return;
  // Seed the flat override map BEFORE the model selection below triggers renderParams, so the restored knob values
  // (polish_denoise, cfg, steps, ...) are applied onto the freshly-rendered fields.
  if (p.params && typeof p.params === "object") paramPrefs = p.params;
  if (Array.isArray(p.aspects) && p.aspects.length) setAspects(p.aspects);
  else if (p.aspect) setAspects([p.aspect]);
  if ($customW && p.customW) $customW.value = p.customW;
  if ($customH && p.customH) $customH.value = p.customH;
  // modelIds (multi) is current; fall back to the legacy single modelId. setSelectedIds refreshes the panel.
  const ids = (Array.isArray(p.modelIds) ? p.modelIds : (p.modelId ? [p.modelId] : [])).filter(id => MODELS[id] && !modelHidden.has(id));
  if (ids.length) modelPicker.setSelectedIds(ids);
  // Re-activate Custom only after the selection resolved (updateCustomShape ran) and only if the workflow still offers it.
  if (p.custom && customCapable()) setCustomActive(true);
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
// Shape gestures, mirroring the style picker: a tap picks exactly ONE (collapsing any multi-pick back down), a
// ~450ms hold ADDS the held shape to the set. A press that turns into a hold must not also run the tap handler,
// so the completed hold swallows the click it releases into.
const ASPECT_HOLD_MS = 450;   // same dwell as the style picker / count picker
let aspHoldTimer = null, aspHeld = false, aspX = 0, aspY = 0;
$aspect.addEventListener("pointerdown", e => {
  const b = e.target.closest("button"); if (!b) return;
  aspHeld = false; aspX = e.clientX; aspY = e.clientY; clearTimeout(aspHoldTimer);
  if (b === $aspectCustom) return;   // Custom is a single exclusive size — no hold-to-add-a-set gesture
  aspHoldTimer = setTimeout(() => { aspHeld = true; addAspect(b.dataset.aspect); savePrefs(); }, ASPECT_HOLD_MS);
});
$aspect.addEventListener("pointermove", e => { if (aspHoldTimer && (Math.abs(e.clientX - aspX) > 10 || Math.abs(e.clientY - aspY) > 10)) { clearTimeout(aspHoldTimer); aspHoldTimer = null; } });
["pointerup", "pointerleave", "pointercancel"].forEach(ev => $aspect.addEventListener(ev, () => { clearTimeout(aspHoldTimer); aspHoldTimer = null; }));
$aspect.addEventListener("contextmenu", e => e.preventDefault());   // no callout on a mobile long-press
$aspect.addEventListener("click", e => {
  const b = e.target.closest("button"); if (!b) return;
  if (aspHeld) { aspHeld = false; return; }
  if (b === $aspectCustom) { setCustomActive(!customActive); savePrefs(); return; }
  setCustomActive(false); setAspects([b.dataset.aspect]); savePrefs();
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
//     re-pull of history, which is the real source of truth for the strips); the top preview reflects the newest too;
//   - a job we were tracking VANISHING from the feed means it finalized -> fetch /forge/job/{id} for its final array
//     to catch any straggler, then signal a history reconcile.
// It never renders a job payload AS history and never assumes completion: an image is shown because the job's array
// grew, and the strip's truth is always /api/history (so deletes stick and nothing resurrects).
let liveWs = null, liveRunning = null, liveRemote = false;
// The observed run's bar. Both writers — the 2.5s /jobs tick and the ws progress frames — go through this one
// tracker, so the bar always shows progress through the BATCH. A ws handler that called showBar itself with the
// raw per-image fraction would, on a batch of 10, paint "70% through image 3" over the tick's "25% of the batch",
// back and forth, for the whole run.
let liveProgress = newBatchProgress(), liveTotal = 1, liveJobId = null;
const watching = new Set();        // active job ids currently being tracked (to detect a vanish)
const announcedIds = new Set();    // image ids already announced this session (dedupe the diff)

function announceImage(job, id) {
  if (!id || announcedIds.has(id)) return;
  announcedIds.add(id);
  const slot = (job.slots || []).find(s => String(s.id) === String(id)) || {};
  const model = (MODELS[job.model] && MODELS[job.model].friendly_name) || job.model || "";
  const rec = { ts: Date.now(), prompt: slot.effectivePrompt || job.prompt || "", marks: slot.marks || null, model, modelId: job.model || "", aspect: primaryAspect(), id };
  // Reflect the newest image in the top preview too — but don't fight the local Generate flow, which owns it while
  // THIS tab is generating (recordResult). Show when observing a remote gen or when idle. On an artist page the
  // preview obeys the same belongs-here rule as the grid below it (belongsToArtistPage), so a gen made elsewhere
  // without this artist doesn't get previewed on their page — the status/bar still reports it's happening.
  const showable = !ARTIST_MODE || belongsToArtistPage(rec.marks, LOCKED_ARTIST);
  if (showable && (liveRemote || !busy)) showResult(rec);
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

  // Gen-state reflection — skip while THIS tab runs its own gen (the local Generate/Batch flow owns the UI then).
  if (activeGen && !liveRemote) return;
  const active = jobs.filter(j => j.status === "queued" || j.status === "running");
  const running = active.find(j => j.status === "running");
  liveRunning = running ? running.jobId : null;
  if (active.length) {
    if (!liveRemote && !busy) {
      liveRemote = true;
      // Cancel works cross-device: /interrupt stops the rendering image, /cancel drops the rest of each active job.
      activeGen = { cancel: async () => {
        try { await fetch(`${GATEWAY}/interrupt`, { method: "POST" }); } catch (e) { console.debug("interrupt request failed:", e); }
        for (const j of active) { try { await fetch(`${GATEWAY}/cancel/${encodeURIComponent(j.jobId)}`, { method: "POST" }); } catch (e) { console.debug("cancel request failed:", e); } }
      } };
      setBusy(true);
    }
    if (liveRemote) {
      // A job IS the batch now: its own total/progress drive the same "Creating X of N" on every device.
      const j = running || active[0];
      // The tracker holds one run's state, so hand it back when the run we're reflecting changes — a queue that
      // rolls straight from one job into the next never empties `active`, and a carried-over count would leave
      // the new job's bar starting where the old one left off.
      if (j.jobId !== liveJobId) { liveJobId = j.jobId; liveProgress = newBatchProgress(); }
      liveTotal = j.total || 1;
      const done = j.progress || 0;
      liveProgress.finished(done);
      setStatus(liveTotal > 1 ? `Creating ${Math.min(done + 1, liveTotal)} of ${liveTotal}…` : "Generating…");
      showBar(liveProgress.value(liveTotal));
    }
  } else if (liveRemote) {
    liveRemote = false; liveRunning = null; liveProgress = newBatchProgress(); liveTotal = 1; liveJobId = null; activeGen = null;
    setBusy(false); setStatus(""); hideBar();
  }
}

function liveOpenWs() {
  if (liveWs) return;
  try {
    liveWs = new WebSocket(gwWs("/ws"));
    liveWs.onmessage = (ev) => {
      if (typeof ev.data !== "string") return;
      let m; try { m = JSON.parse(ev.data); } catch (e) { console.debug("live ws non-JSON message:", e); return; }
      const id = m.data && m.data.prompt_id;
      if (liveRemote && id && id === liveRunning) { const f = wsFraction(m); if (f != null) { liveProgress.fraction(f); showBar(liveProgress.value(liveTotal)); } }
      if (m.type === "executed" || m.type === "execution_error" || m.type === "execution_success") liveSync();
    };
    liveWs.onclose = () => { liveWs = null; };
    liveWs.onerror = (ev) => { console.debug("live ws error:", ev); try { liveWs && liveWs.close(); } catch (e) { console.debug("ws close failed:", e); } liveWs = null; };
  } catch (e) { console.debug("live ws open failed:", e); liveWs = null; }
}

function startLiveSync() {
  liveSync(); liveOpenWs();
  setInterval(() => { liveSync(); liveOpenWs(); }, 2500);
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") { liveSync(); liveOpenWs(); } });
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
