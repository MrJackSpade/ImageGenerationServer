// Edit page: four modes behind a tab bar, all over one source image (seeded from /edit/{id}).
//   • Edit    — instruction image-editing (Flux Kontext / Qwen), gen-style: source on the left, prompt box +
//               controls on the right, outputs underneath. Each "Apply" edits the SAME source image.
//   • Effects — deterministic image transforms (Line art / Pixelize), same gen-style layout; the dropdown is
//               grouped by effect type. These carry an effect_type in the catalog (Edit holds the rest).
//   • Animate — image→video editors (Wan / LTX / AnimateDiff), same gen-style layout.
//   • Inpaint — purpose-built: paint a region, edit the FULL (tag) prompt, regenerate only that region.
//   • Outpaint— purpose-built: drag the frame outward, edit the FULL (tag) prompt, generate only the new margin.
// Shares only helpers from core.js (+ tagbox.js for the inpaint prompt autocomplete). History is written
// server-side by the worker; the browser never writes it. To iterate on an OUTPUT, the user clicks it and
// chooses Edit, which re-seeds this page (/edit/{outputId}) with that output as the fixed source.

const $editTabs = $("editTabs"), $editTabsSelect = $("editTabsSelect"), $chatMode = $("chatMode"), $inpaintMode = $("inpaintMode"), $outpaintMode = $("outpaintMode"),
      $editModelSelect = $("editModelSelect"), $editModelToggle = $("editModelToggle"), $editModelMenu = $("editModelMenu"),
      $editSrc = $("editSrc"), $bar = $("bar"), $eta = $("eta"), $result = $("result"), $editComposer = $("editComposer"),
      $instruction = $("instruction"), $instructionTagPop = $("instructionTagPop"), $editSend = $("editSend"), $status = $("status"),
      $editRefs = $("editRefs"), $editRefBtn = $("editRefBtn"), $editRefFile = $("editRefFile"), $editRefHint = $("editRefHint"),
      $editLastFrame = $("editLastFrame"), $editLastFrameBtn = $("editLastFrameBtn"), $editLastFrameFile = $("editLastFrameFile"),
      $editSrcFile = $("editSrcFile"),
      // inpaint
      $inpaintModelSelect = $("inpaintModelSelect"), $inpaintModelToggle = $("inpaintModelToggle"), $inpaintModelMenu = $("inpaintModelMenu"),
      $inpaintComposer = $("inpaintComposer"), $inpaintPrompt = $("inpaintPrompt"), $inpaintTagPop = $("inpaintTagPop"),
      $inpaintParams = $("inpaintParams"), $inpaintGo = $("inpaintGo"), $inpaintResult = $("inpaintResult"),
      $inpaintBar = $("inpaintBar"), $inpaintEta = $("inpaintEta"),
      $maskStage = $("maskStage"), $brushSize = $("brushSize"), $brushErase = $("brushErase"), $maskClear = $("maskClear"),
      // outpaint
      $outpaintModelSelect = $("outpaintModelSelect"), $outpaintModelToggle = $("outpaintModelToggle"), $outpaintModelMenu = $("outpaintModelMenu"),
      $outpaintComposer = $("outpaintComposer"), $outpaintPrompt = $("outpaintPrompt"), $outpaintTagPop = $("outpaintTagPop"),
      $outpaintGo = $("outpaintGo"), $outpaintResult = $("outpaintResult"),
      $outpaintBar = $("outpaintBar"), $outpaintEta = $("outpaintEta"),
      $outpaintStage = $("outpaintStage"), $outPads = $("outPads"), $outSize = $("outSize"), $outPresets = $("outPresets"),
      // optional negative prompt (chat + inpaint + outpaint) — shown only when a selected editor's card declares support
      $editNegWrap = $("editNegWrap"), $editNeg = $("editNeg"), $editNegTagPop = $("editNegTagPop"),
      $inpaintNegWrap = $("inpaintNegWrap"), $inpaintNeg = $("inpaintNeg"), $inpaintNegTagPop = $("inpaintNegTagPop"),
      $outpaintNegWrap = $("outpaintNegWrap"), $outpaintNeg = $("outpaintNeg"), $outpaintNegTagPop = $("outpaintNegTagPop");

// The seed record names the image this page opens on — or names none, which is a legitimate starting state, not a
// failure: the rail's Edit button (GET /edit) exists precisely to open the editor with NO source and pick a file,
// and every mode already renders a picker when its base is empty (renderSrc, setupMaskStage, setupOutpaintStage).
//
// Having a source is a precondition of APPLYING an edit, not of loading the editor, so the check lives at each
// mode's submit path — sendEdit, inpaintGenerate and outpaintGenerate each refuse with "Select a file to … first".
// Asserting it here instead threw at page init, which aborted the whole script and took the file picker with it:
// the one entry point whose job is choosing a source was the one that couldn't. Checking at the point of use also
// catches what an init assertion can't, a source that goes away or is replaced mid-session.
//
// A MISSING or unparseable #editSeed is a different failure and still stops the page here — it throws inside
// JSON.parse, so it stays distinguishable without guessing. And the fallback this guard originally replaced is
// not coming back: seeding { id: "", prompt: "(image)" } left a page that looked fully functional pointed at an
// image id the browser had invented, so every Apply was rejected server-side. An empty id must never be submitted
// as a render source — which is exactly what the submit-path checks now guarantee.
const seed = (() => {
  try {
    const s = JSON.parse($("editSeed").textContent);
    if (!s || typeof s !== "object") throw new Error("its seed data isn't a record");
    return s;
  } catch (e) {
    const msg = `This page couldn't identify the image to edit — ${e.message}. Try opening it again from the gallery.`;
    const $s = $("status");
    if ($s) { $s.classList.add("error"); $s.textContent = msg; }
    throw new Error(msg);   // stop here: the page's own seed data is broken, not merely sourceless
  }
})();

const EDIT_MODELS = {};
const gwModel = m => (m && m._gw) || "";
const isInpaint = m => !!(m && /inpaint/i.test(m.workflow || ""));
// Outpaint gets its own mode for the same reason inpaint does: its pad_left/top/right/bottom are NOT exposed params
// (bare scalars in workflows.json — hidden from the param panel, still overridable per request), so the frame editor
// is the only thing that can supply them. Left in the Edit dropdown it would pad by 0 and hand back the source.
// Note /inpaint/i does NOT match "anima-outpaint" ("outpaint" has no "inpaint" substring), so the two never overlap.
const isOutpaint = m => !!(m && /outpaint/i.test(m.workflow || ""));
// A video-to-video editor CONSUMES a clip (the pixel-quantize V2V pass). These are offered ONLY when the source is a
// clip (and are the only thing offered then); image editors are kept off a clip source. See applySourceMediaUi.
const isV2V = m => !!(m && m.sourceMedia === "video");
// A whole-image REDRAW: no mask, no instruction — the prompt describes the finished picture and the entire frame is
// re-rendered from the source's own structure. It gets its own tab rather than a section inside Edit because it asks
// a different question of the user (describe the picture, not the change) and is the natural place to fan one prompt
// across several models. Declared by the catalog (edit_group), so a new redraw config lands here with no JS change.
const isRedraw = m => !!(m && m.editGroup === "Redraw");
// Upscalers (feed-forward SR + SeedVR2) are one edit_group too, promoted to their own top-level tab exactly like
// Redraw so they aren't buried as a sub-section of the plain Edit menu. New upscale config → this tab, no JS change.
const isUpscale = m => !!(m && m.editGroup === "Upscale");
// Whether the current source (editCurrent) is a video clip. Decided from /forge/media for a seeded/edited source, and
// from the file type for an upload. When true, the editor collapses to the single V2V "Pixelize" mode.
let srcIsVideo = false;
// Ask the server whether an id is a clip (content type webp/video). Used to flip into V2V mode for a clip source.
async function detectSrcVideo(id) {
  if (!id) return false;
  const key = imageId(id);
  try {
    const r = await fetch(`${GATEWAY}/media?ids=${encodeURIComponent(key)}`, { credentials: "same-origin" });
    if (!r.ok) return false;
    const map = await r.json();
    return !!map[key];
  } catch (_) { return false; }
}
// The inpaint/outpaint boxes seed from the source image's prompt VERBATIM — the marker form ('#'/'@' and underscores
// intact) the worker stored when it made the picture. Empty for prose/uploaded sources; falls back to the plain prompt
// only if it isn't the placeholder.
const seedPrompt = () => (seed.tagPrompt && seed.tagPrompt.trim()) ? seed.tagPrompt
  : ((seed.prompt && !/^\((image|uploaded photo)\)$/.test(seed.prompt)) ? seed.prompt : "");
// The image's NEGATIVE, verbatim, for the same boxes: it was typed in the same marker dialect and shaped the picture
// you're now editing, so editing it without the negative silently changes what the model is steering away from.
const seedNegative = () => (seed.negativePrompt || "").trim();

// --- last-used settings (per-user account; follows you across devices, exactly like the gen page) -----------------
// The editor restores its WHOLE last state from the account (never localStorage): the active tab, the selected
// workflow(s), the inpaint workflow, the brush size, and a FLAT by-name param-override map. The param map is NOT
// keyed per workflow — a value you set for one editor prefills the same-named field on the next — mirroring the gen
// page, so switching workflows never wipes your knobs. Writes are debounced and go to the account via saveEditPrefs;
// the blob is restored on boot in loadEditModels. The instruction/prompt text is intentionally NOT retained: it's
// tied to the specific source image being edited, so carrying a prior image's instruction to a new source is wrong.
let editParamPrefs = {};            // flat { paramKey: value }, shared across every param panel (edit + inpaint + outpaint)
let savedMode = null, savedInpaintWorkflowId = null, savedOutpaintWorkflowId = null, savedBrushSize = null;   // seeded from the account blob on boot

let prefsTimer = null;
// False until the stored blob has actually been read. savePrefs writes the WHOLE editor state, so writing before we
// know what was stored replaces the user's saved editor with this page's defaults — see loadEditModels.
let editPrefsLoaded = false;
// One blob captures the full editor state (read live from the UI), like the composer's savePrefs.
function savePrefs() {
  if (!editPrefsLoaded) return;   // never overwrite settings we failed to read
  const json = JSON.stringify({
    mode: activeMode,
    modelIds: editSelIds(),
    inpaintWorkflowId: selectedInpaintId,
    outpaintWorkflowId: selectedOutpaintId,
    params: editParamPrefs,
    brushSize: $brushSize ? $brushSize.value : null,
    // The pad amounts are NOT retained: like the instruction text they're tied to the specific source image, so
    // carrying a prior image's margins onto a new source would silently extend it by the wrong number of pixels.
  });
  clearTimeout(prefsTimer);
  // A failed save was `.catch(() => {})` — the editor went on looking exactly like one whose settings were being
  // kept, and the next page load quietly came back with older state. Say it once, where the user is looking.
  prefsTimer = setTimeout(() => {
    saveEditPrefs(json).catch(e => {
      console.error("Editor settings could not be saved:", e);
      toast("Couldn't save your editor settings");
    });
  }, 400);
}
// Apply the shared flat param map onto the just-rendered fields in `box` (every panel reads the one map).
function restoreParams(box) { applyParamPrefs(box, editParamPrefs); }
// Merge the current field values in `box` into the shared flat map, then persist. Merge (not replace) so values for
// keys that only appear on other panels/workflows survive.
function persistParams(box) { collectParamPrefs(box, editParamPrefs); savePrefs(); }

let activeMode = "edit", chatBucket = "edit";          // chatBucket ∈ {edit, redraw, upscale, effects (image), animate, video}
// Chat (Edit/Redraw/Effects/Animate) is a MULTI-select picker (the shared createModelPicker) mirroring the gen page:
// any number of models in the bucket can be checked, and Apply fans the SAME instruction across all of them to compare.
// selectedEditIds persists the pick across rebuilds (bucket switches). Inpaint stays single-select (buildMenu).
let selectedEditIds = [], selectedInpaintId = null, selectedOutpaintId = null, editPicker = null;
const editSelIds = () => editPicker ? editPicker.getSelectedIds() : [];
const editModels = () => editPicker ? editPicker.getSelected() : [];
// "Primary" = the model when EXACTLY one is checked; it alone drives the per-model params/refs/placeholder. With
// 2+ checked there is no primary (null), so those single-model affordances hide and each model runs on its defaults.
const editModel = () => editPicker ? editPicker.getPrimary() : null;
const inpaintModel = () => EDIT_MODELS[selectedInpaintId] || null;
const outpaintModel = () => EDIT_MODELS[selectedOutpaintId] || null;

// Edit-box display names: strip redundant tags and fix misleading ones, keyed by catalog friendly_name.
const EDIT_NAME = {
  "FLUX.1-Kontext (image editing)": "FLUX.1-Kontext",
  "Qwen Rapid (uncensored)": "Qwen Rapid",
  "Qwen-Image-Edit (fp8)": "Qwen-Image-Edit",
  "Anime video (SD1.5)": "AnimateDiff (SD1.5)",
  "Anime video — Lightning (SD1.5)": "AnimateDiff Lightning (SD1.5)",
  "Anime video — AnimateLCM (SD1.5)": "AnimateLCM (SD1.5)",
  "HunyuanVideo (image → video)": "HunyuanVideo",
  "HunyuanVideo 1.5 (image → video)": "HunyuanVideo 1.5",
  "HunyuanVideo anime — Anime Style": "HunyuanVideo (Anime Style)",
  "HunyuanVideo anime — AnimeShots": "HunyuanVideo (AnimeShots)",
  "LTX Video (fast image → video)": "LTX Video",
  "LTX Video 13B (image → video)": "LTX Video 13B",
  "LTX-2 (image → video)": "LTX-2",
  "LTX-2 dev (image → video)": "LTX-2 dev",
  "LTX-2.3 22B (image → video)": "LTX-2.3 22B",
  "SDXL video (AnimateDiff)": "SDXL AnimateDiff",
  "Wan 2.2 (image → video)": "Wan 2.2",
  "Wan 2.2 14B (image → video)": "Wan 2.2 14B",
  "WAN anime — Anime LoRA": "Wan 2.2 (Anime LoRA)",
  "WAN anime — Flat Color": "Wan 2.2 (Flat Color)"
};
const cleanName = m => EDIT_NAME[m.friendly_name] || m.friendly_name;
let editFavs = new Set(), editHidden = new Set(), editTags = {};

// editCurrent is the FIXED source image (the seed). It never advances on its own — every Apply edits this
// same image, so the source on the left stays put. Building on an output is an explicit click-to-edit reload.
let editCurrent = seed.id, editRefs = [], editProgressEl = null, editEtaEl = null;
// Optional END frame for i2v first/last-frame editors (a single uploaded image id, or null). Tied to the current
// source like the instruction text: cleared on a source swap and on manual removal, never persisted to the account.
let lastFrameId = null;
let busy = false, activeGen = null, cancelRequested = false;
// Overall-progress window for the shared bar: the running edit's 0..1 fraction maps into [barBase, barBase+barSpan].
// Single edit → base 0, span 1 (raw fraction). Multi-model → span 1/N, base = (models done)/N, so the one bar
// climbs smoothly across all models (the queue runs them one at a time), exactly like the gen page's batch bar.
let barBase = 0, barSpan = 1, multiDone = 0, multiTotal = 0;
// Cumulative-ETA pool: summed avgSeconds of every batch model that hasn't started rendering yet. Each model's
// onStart subtracts its own estimate (it becomes the live countdown), so the shown ETA = current image's
// countdown + everything still queued behind it. 0 for single edits (no queue tail).
let etaPending = 0;
// The FIXED image inpaint paints over. Like editCurrent, it never advances on its own: a finished inpaint leaves the
// base and the painted mask in place, so the same region can be re-rolled. Only a new source (upload / click-to-edit
// re-seed) moves it.
let inpaintBase = seed.id;
let maskCanvas = null, maskCtx = null, eraseMode = false, inpaintTag = null;

function setStatus(t, { error = false } = {}) { $status.classList.toggle("error", error); $status.textContent = t; }
function showBar(p) { const overall = Math.min(1, barBase + p * barSpan); if (editProgressEl) editProgressEl.style.width = Math.round(overall * 100) + "%"; document.title = `⏳ ${Math.round(overall * 100)}% · Edit · Make a Picture`; }
function hideBar() { document.title = "Edit · Make a Picture"; if (editEtaEl) stopEta(editEtaEl); }
function setBusy(b) {
  busy = b;
  const btn = activeMode === "inpaint" ? $inpaintGo : activeMode === "outpaint" ? $outpaintGo : $editSend;
  btn.textContent = b ? "Cancel" : (activeMode === "inpaint" || activeMode === "outpaint" ? "Generate" : "Apply edit");
  btn.classList.toggle("is-cancel", b);
}
function cancelGeneration() { if (!busy || !activeGen) return; cancelRequested = true; setStatus("Cancelling…"); activeGen.cancel(); }

// trackPrompt / wsFraction / uploadToInput are shared from core.js. These hooks bind one tracked prompt to the
// edit page's bar/ETA (editEtaEl is whatever the current run points at) and Cancel button.
const editTrackHooks = (model) => ({
  onFraction: showBar,
  onStart: res => {
    // This model is no longer queued — move its estimate out of the pending pool and into the live countdown,
    // so the displayed ETA stays cumulative (this render + the models still waiting behind it).
    etaPending = Math.max(0, etaPending - Number((model && model.avgSeconds) || 0));
    startEta(editEtaEl, res.expectedSeconds, res.startedAt, etaPending);
  },
  setActiveGen: g => { activeGen = g; },
});

// --- model catalog + buckets --------------------------------------------------------------------
async function loadEditModels() {
  try {
    const resp = await fetch(`${GATEWAY}/workflows`);
    if (!resp.ok) throw new Error("The catalog couldn't be reached.");
    const rows = (await resp.json()) || [];
    // Favorites/hidden/tags are read-only here, as on the composer: an unreadable set just means an un-personalized
    // editor picker. `s` is the same settings response, reused below for this page's own editPrefs blob.
    const prefs = await loadWorkflowPrefs();
    editFavs = prefs.favs; editHidden = prefs.hidden; editTags = prefs.tags;
    const s = prefs.settings;
    rows.filter(r => r.kind === "edit").forEach(r => {
      EDIT_MODELS[r.id] = {
        id: r.id, friendly_name: r.friendlyName || r.id, _gw: r.id, workflow: r.workflow,
        exposedParams: r.exposedParams || [], avgSeconds: r.avgSeconds,
        media: r.media === "video" ? "video" : "image", promptDirectsMotion: r.promptDirectsMotion !== false,
        sourceMedia: r.sourceMedia === "video" ? "video" : "image",
        supportsLastFrame: !!r.supportsLastFrame,   // i2v first/last-frame: offer an optional final frame to interpolate to

        effectType: r.effectType || null,
        editGroup: r.editGroup || null,   // "Redraw" gets its own tab; any other group is a section inside Edit
        promptSemantics: r.promptSemantics || "instruction",   // instruction | whole_image | masked_region
        takesPrompt: r.takesPrompt !== false,   // false = no text encoder in the graph (upscalers): hide the box
        negativeSupported: !!(r.card && r.card.negativeSupported),   // editor uses a negative prompt (append-on-top)
        tagging: (r.card && r.card.tagging) || null,
        edit: { reference: r.reference || null, default: !!r.default }
      };
    });
    // Restore the editor's last state from the account blob (mode, workflows, flat params, inpaint workflow, brush).
    // editPrefsLoaded is set ONLY on a clean read, and savePrefs refuses to write until it is: the swallow here used
    // to drop the user back to defaults and then persist those defaults on the first knob they touched, so a one-off
    // bad read permanently became their saved editor state. A missing blob is a first visit, which is safe to write.
    if (!s) {
      toast("Your saved editor settings couldn’t be loaded — reload before changing them");
    } else {
      try {
        const p = JSON.parse(s.editPrefs || "null");
        if (p && typeof p === "object") {
          if (p.params && typeof p.params === "object") editParamPrefs = p.params;
          if (typeof p.mode === "string") savedMode = p.mode;
          if (typeof p.inpaintWorkflowId === "string") savedInpaintWorkflowId = p.inpaintWorkflowId;
          if (typeof p.outpaintWorkflowId === "string") savedOutpaintWorkflowId = p.outpaintWorkflowId;
          if (p.brushSize != null) savedBrushSize = p.brushSize;
          // Selection (multi) restored if still installed.
          const ids = Array.isArray(p.modelIds) ? p.modelIds.filter(id => EDIT_MODELS[id]) : [];
          if (ids.length) selectedEditIds = ids;
        }
        editPrefsLoaded = true;
      } catch (e) {
        console.error("Stored editor settings are not readable; they will be left untouched:", e);
        toast("Your saved editor settings couldn’t be read — reload before changing them");
      }
    }
    return s || {};
  } catch (e) { $editModelToggle.textContent = "Unavailable"; setStatus(friendlyError(e), { error: true }); return {}; }
}
const visibleOf = list => list.filter(m => !editHidden.has(m.id));
// edit   = image editors with NO effect_type (pure instruction editors); effects = image with an effect_type
// (Line art / Pixelize, grouped by type in the dropdown); animate = video editors. Inpaint is its own mode.
const chatModels = () => visibleOf(Object.values(EDIT_MODELS).filter(m => {
  if (chatBucket === "video") return isV2V(m);   // the V2V (clip-source) bucket — only video-source editors
  if (isV2V(m)) return false;                     // video-source editors never appear in the image buckets
  if (chatBucket === "animate") return m.media === "video";
  if (m.media !== "image" || isInpaint(m) || isOutpaint(m)) return false;
  if (chatBucket === "redraw") return isRedraw(m);
  if (isRedraw(m)) return false;                  // redraws have their own tab — never also in Edit/Effects
  if (chatBucket === "upscale") return isUpscale(m);
  if (isUpscale(m)) return false;                 // upscalers have their own tab — never also in Edit/Effects
  return chatBucket === "effects" ? !!m.effectType : !m.effectType;
}));
const inpaintModelList = () => visibleOf(Object.values(EDIT_MODELS).filter(isInpaint));
const outpaintModelList = () => visibleOf(Object.values(EDIT_MODELS).filter(isOutpaint));
const sortModels = ms => ms.slice().sort((a, b) => {
  const af = editFavs.has(a.id) ? 0 : 1, bf = editFavs.has(b.id) ? 0 : 1;
  // sensitivity:'base' — order by name case-insensitively, so casing never decides the position.
  return af !== bf ? af - bf : cleanName(a).localeCompare(cleanName(b), undefined, { sensitivity: "base" });
});

// Styled single-select popover (mirrors the gen page): ★ favorites first, render time, tag chips.
function buildMenu(menuEl, models, selectedId) {
  menuEl.innerHTML = "";
  for (const m of sortModels(models)) {
    const opt = document.createElement("div"); opt.className = "model-opt" + (m.id === selectedId ? " selected" : ""); opt.dataset.id = m.id; opt.setAttribute("role", "option");
    const text = document.createElement("div"); text.className = "model-opt-text";
    const nameRow = document.createElement("div"); nameRow.className = "model-opt-namerow";
    const nm = document.createElement("span"); nm.className = "model-opt-nm"; nm.textContent = (editFavs.has(m.id) ? "★ " : "") + cleanName(m); nameRow.appendChild(nm);
    if (m.avgSeconds) { const tm = document.createElement("span"); tm.className = "model-opt-time"; tm.textContent = fmtDuration(m.avgSeconds); nameRow.appendChild(tm); }
    text.appendChild(nameRow);
    const tg = editTags[m.id] || [];
    if (tg.length) { const sub = document.createElement("div"); sub.className = "model-opt-tags"; for (const t of tg) { const chip = document.createElement("span"); chip.className = "model-opt-tag"; chip.textContent = t; sub.appendChild(chip); } text.appendChild(sub); }
    opt.appendChild(text); menuEl.appendChild(opt);
  }
  // Size the toggle box to the WIDEST option name so its width is stable — it no longer shrinks/grows to the
  // currently-selected name. Measured with canvas (the menu is hidden, so offsetWidth would be 0).
  const sel = menuEl.closest(".model-select");
  const toggle = sel && sel.querySelector(".model-toggle");
  if (toggle) {
    const cs = getComputedStyle(toggle);
    const ctx = buildMenu._cv || (buildMenu._cv = document.createElement("canvas").getContext("2d"));
    ctx.font = `${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
    let max = 0;
    for (const m of models) { const w = ctx.measureText((editFavs.has(m.id) ? "★ " : "") + cleanName(m)).width; if (w > max) max = w; }
    sel.style.minWidth = max ? Math.ceil(max + 11 * 2 + 8 + 14 + 4) + "px" : "";   // text + toggle padding + gap + caret
  }
}
function openMenu(menuEl, toggleEl, open) { menuEl.hidden = !open; toggleEl.setAttribute("aria-expanded", String(open)); }

// --- chat editor (Edit + Animate): multi-select via the shared picker --------------------------
// onChange runs after any selection change (keeps selectedEditIds current for bucket switches + refreshes the
// single-model affordances); onCommit persists the primary id on user changes. editModel()/editModels() above
// read the picker's live state: refs/placeholder are primary-only (hide when 2+ checked), but the param panel
// shows the params common to ALL checked models (their intersection) and applies them to every one.
editPicker = createModelPicker({
  select: $editModelSelect, toggle: $editModelToggle, menu: $editModelMenu,
  nameOf: cleanName,
  favOf: m => editFavs.has(m.id),
  timeOf: m => m.avgSeconds,
  tagsOf: m => editTags[m.id] || [],
  // Effects bucket → grouped by effect type. Redraw and Upscale are each ONE edit_group promoted to its own top-level
  // tab, so inside those tabs the group renders flat — a lone "Redraw"/"Upscale" header would just repeat the tab name.
  // Edit and Animate items have neither an effectType nor a remaining edit_group, so they render flat too. The buckets
  // never collide: a config with an effectType only ever appears in the Effects bucket.
  groupBy: m => m.effectType || (chatBucket === "redraw" || chatBucket === "upscale" ? null : m.editGroup) || null,
  hint: "Long-press a workflow to pick several and compare",
  onChange: ids => { selectedEditIds = ids; updateEditRefBtn(); updateEditRefHint(); renderEditLastFrame(); updateEditParams(); updateInstructionPlaceholder(); updateEditNeg(); },
  onCommit: () => savePrefs(),   // user-driven selection change → persist the whole editor state
});
function populateChatMenu() {
  const models = chatModels();
  if (!models.length) { $editModelToggle.textContent = chatBucket === "video" ? "No video pixelizer installed" : chatBucket === "animate" ? "No video editors installed" : chatBucket === "effects" ? "No effects installed" : chatBucket === "redraw" ? "No redraw models installed" : chatBucket === "upscale" ? "No upscalers installed" : "No image editors installed"; return; }
  editPicker.rebuild(models);
  // Keep the prior pick that's valid in THIS bucket; else fall back to the bucket's default/first.
  let ids = selectedEditIds.filter(id => models.some(m => m.id === id));
  if (!ids.length) ids = [(models.find(m => m.edit && m.edit.default) || models[0]).id];
  editPicker.setSelectedIds(ids);
  // Re-sync the affordances that depend on the selection, in case setSelectedIds didn't fire onChange: the negative
  // box's visibility and the Change/Prompt wording would otherwise stay on the previous bucket's model.
  updateEditNeg();
  updateInstructionPlaceholder();
  renderEditLastFrame();
}

function updateEditParams() {
  renderParamFields($("editParams"), editModels());
  restoreParams($("editParams"));   // prefill from the shared flat param map (carries across workflow switches)
}
// Persist tuned values the moment they change.
$("editParams").addEventListener("change", () => persistParams($("editParams")));
// Honest wording for whatever the primary model actually consumes. An instruction editor is told to name a CHANGE; a
// redraw re-renders the whole frame from the prompt, so it is asked for the picture itself (saying "describe a change"
// there is simply wrong); a video editor is asked about motion per promptDirectsMotion. Outpaint is not reachable from
// here — it has its own tab, because its pad_* amounts need a frame editor no dropdown can provide.
// An editor with no text encoder (the upscalers) gets no instruction box at all: the field is hidden whenever NOT ONE
// selected editor consumes a prompt. Mixed selections keep it — the models that read it still would. Submitting with
// an empty instruction is already legal (see sendEdit), so hiding the box changes nothing about the request.
function updateInstructionVisibility() {
  const field = $("instructionField");
  if (field) field.hidden = !editModels().some(m => m && m.takesPrompt);
}

function updateInstructionPlaceholder() {
  updateInstructionVisibility();
  const m = editModel();
  const label = $("instructionLabel");
  const setLabel = t => { if (label) label.textContent = t; };
  if (m && m.media === "video") {
    setLabel("Change");
    $instruction.placeholder = m.promptDirectsMotion
      ? "Optional: describe the motion (e.g. gentle breeze, slow zoom)"
      : "Optional: describe the scene — motion is automatic, not prompt-controlled";
    return;
  }
  if (m && m.promptSemantics === "whole_image") {
    setLabel("Prompt");
    $instruction.placeholder = m.tagging
      ? "Full prompt for the picture — type # for tags, @ for artists"
      : "Full prompt for the picture — e.g. a woman in a sunlit pine forest";
    return;
  }
  if (m && m.promptSemantics === "masked_region") {
    setLabel("Prompt");
    $instruction.placeholder = "What fills the painted area — e.g. a white paper cup";
    return;
  }
  setLabel("Change");
  $instruction.placeholder = "Describe a change… e.g. add a red party hat";
}

// Optional negative prompt (chat + inpaint). The field is offered only for editors whose card declares support;
// whatever's typed is APPENDED to the model's built-in default negative server-side (ComfyGraph.ComposeNegative),
// never replaces it — a blank negative just yields the default. Chat shows it when ANY selected editor supports it;
// the per-model value is dropped for any model that doesn't (mirrors the gen page's negFor).
function updateEditNeg() { if ($editNegWrap) $editNegWrap.hidden = !editModels().some(m => m && m.negativeSupported); }
function editNegFor(model) { const t = $editNeg ? $editNeg.value.trim() : ""; return (model && model.negativeSupported && t) ? t : null; }
function updateInpaintNeg() { const m = inpaintModel(); if ($inpaintNegWrap) $inpaintNegWrap.hidden = !(m && m.negativeSupported); }
function inpaintNegFor(model) { const t = $inpaintNeg ? $inpaintNeg.value.trim() : ""; return (model && model.negativeSupported && t) ? t : null; }
function updateOutpaintNeg() { const m = outpaintModel(); if ($outpaintNegWrap) $outpaintNegWrap.hidden = !(m && m.negativeSupported); }
function outpaintNegFor(model) { const t = $outpaintNeg ? $outpaintNeg.value.trim() : ""; return (model && model.negativeSupported && t) ? t : null; }

// --- source pane + result (gen-style) -----------------------------------------------------------
// Left pane shows the FIXED source image being edited. Clicking it opens the lightbox/detail.
function renderSrc() {
  $editSrc.innerHTML = "";
  if (!editCurrent) { $editSrc.appendChild(selectFileButton("Select a file to edit")); return; }
  if (srcIsVideo) {
    // A clip source: play it looping (the mp4 endpoint transcodes our animated-webp clips and passes real containers through).
    const v = document.createElement("video");
    v.src = `${GATEWAY}/image/${encodeURIComponent(imageId(editCurrent))}/mp4`;
    v.loop = true; v.muted = true; v.autoplay = true; v.playsInline = true; v.controls = true;
    v.setAttribute("muted", ""); v.setAttribute("playsinline", "");
    $editSrc.appendChild(v);
    return;
  }
  const im = document.createElement("img"); im.src = viewUrl(editCurrent); im.alt = "image being edited";
  im.addEventListener("click", () => openImage(imageId(editCurrent)));
  $editSrc.appendChild(im);
}
// Empty-state control shown in the image area when the editor is opened with no source (the rail's Edit button).
// Picking a file uploads it to the input store and makes it the source for EVERY mode.
function selectFileButton(label) {
  const b = document.createElement("button");
  b.type = "button"; b.className = "edit-pick-src";
  const ic = document.createElement("span"); ic.className = "eps-icon"; ic.textContent = "⇪";
  const tx = document.createElement("span"); tx.textContent = label || "Select a file to edit";
  b.appendChild(ic); b.appendChild(tx);
  b.addEventListener("click", () => $editSrcFile.click());
  return b;
}
$editSrcFile.addEventListener("change", async e => {
  const f = e.target.files && e.target.files[0]; e.target.value = "";
  if (!f) return;
  const isImg = /^image\//.test(f.type) || /\.(png|jpe?g|webp|gif|bmp|avif|heic|heif)$/i.test(f.name);
  const isVid = /^video\//.test(f.type) || /\.(mp4|webm|mov|mkv)$/i.test(f.name);
  if (!isImg && !isVid) { setStatus("Please choose an image or video file.", { error: true }); return; }
  setStatus("Uploading…");
  try {
    const id = await uploadToInput(f, f.name || (isVid ? "edit_src.mp4" : "edit_src.png"));
    editCurrent = id; inpaintBase = id; stagedBase = null;   // the new upload is the source for chat, inpaint AND outpaint
    outpaintBase = id; outStagedBase = null;
    lastFrameId = null;                                      // the end frame was tied to the old source — drop it
    srcIsVideo = isVid;                                       // a clip upload flips the editor into V2V-only mode
    renderSrc(); renderEditLastFrame();
    applySourceMediaUi();
    if (srcIsVideo) setMode("video");                         // clip → the single Pixelize (V2V) mode
    else if (activeMode === "video") setMode("edit");         // switched back to an image source
    else if (activeMode === "inpaint") { setupMaskStage(); stagedBase = inpaintBase; }
    else if (activeMode === "outpaint") { setupOutpaintStage(); outStagedBase = outpaintBase; }
    setStatus("");
  } catch (err) { setStatus(friendlyError(err), { error: true }); }
});
// Open in the lightbox (which carries the detail fragment + its Edit button) if available, else the detail page.
function openImage(id) {
  if (!(window.openImgcard && window.openImgcard(String(id)))) location.href = "/image/" + encodeURIComponent(id);
}
function showProgressBar(show) { $bar.classList.toggle("show", show); if (!show) { $bar.querySelector("i").style.width = "0"; } }
const loadingCardHtml = () => '<div class="result-card loading"><span class="b-dots">working</span></div>';
// One result card, rendered like the gen page: click → lightbox, "Edit this" → re-seed, download. `model` is the
// SLOT's model (needed because in multi-select editModel() is null) — used only to pick video vs still rendering.
// A non-fatal yellow notice (e.g. "30 frames isn't valid — rendering 33"): the server normalized an input rather
// than silently changing it or rejecting the job. Shown on the placeholder the moment /edit returns and kept under
// the result. null/blank → nothing rendered.
function noticeEl(text) {
  if (!text) return null;
  const n = document.createElement("div"); n.className = "result-notice"; n.textContent = "⚠ " + text; return n;
}
function buildResultCard(id, model, instruction, notice) {
  const card = document.createElement("div"); card.className = "result-card";
  const isVid = !!(model && model.media === "video");
  let media;
  if (isVid) {
    media = document.createElement("video");
    media.src = `${GATEWAY}/image/${encodeURIComponent(imageId(id))}/mp4`;
    media.loop = true; media.muted = true; media.autoplay = true; media.playsInline = true;
    media.setAttribute("muted", ""); media.setAttribute("playsinline", ""); media.preload = "metadata";
  } else {
    media = document.createElement("img"); media.src = viewUrl(id); media.alt = instruction || "edited image";
  }
  media.style.cursor = "pointer";
  media.addEventListener("click", () => openImage(imageId(id)));
  const actions = document.createElement("div"); actions.className = "result-actions";
  const ed = document.createElement("a"); ed.className = "link-btn"; ed.textContent = "✎ Edit this"; ed.href = "/edit/" + encodeURIComponent(imageId(id)); ed.style.marginRight = "auto"; actions.appendChild(ed);
  const dl = document.createElement("a"); dl.className = "download"; dl.href = viewUrl(id); dl.textContent = "↓ Save"; dl.setAttribute("download", ""); actions.appendChild(dl);
  card.appendChild(media); card.appendChild(actions);
  const n = noticeEl(notice); if (n) card.appendChild(n);
  return card;
}
// Single-model output: one big card. The source on the left stays put, so the NEXT Apply edits the original again.
function showEditResult(id, instruction, model, notice) {
  $result.innerHTML = ""; $result.appendChild(buildResultCard(id, model || editModel(), instruction, notice));
}
function renderResultLoading() { $result.innerHTML = loadingCardHtml(); }
// The result box holds ONLY the newest finished picture, exactly like the gen page's #result: a fan-out across N
// workflows lands N images there in turn (last one wins) and each also reconciles into the Recent strip below, which
// is where you compare them. Progress lives in the page-level bar (#bar), never inside a card, so a batch can't turn
// the box into a grid of loading cells. Returns whether an image landed, so the batch can count real makes.
function applyEditOutput(result, model, instruction, notice) {
  if (result.changed === false) return false;
  showEditResult(result.id, instruction, model, notice);
  document.dispatchEvent(new CustomEvent("imagegen:generated", { detail: { id: result.id } }));   // Recent reconciles from history
  return true;
}
// Drop the yellow notice onto the loading placeholder the instant /edit returns it — before the render starts.
function showPendingNotice(notice) {
  if (!notice) return;
  if ($result && !$result.querySelector(".result-notice")) { const n = noticeEl(notice); if (n) $result.appendChild(n); }
}

// --- reference images ---------------------------------------------------------------------------
function editRefMax() { const m = editModel(); const r = m && m.edit && m.edit.reference; return (r && r.max) || 0; }
function updateEditRefBtn() { const max = editRefMax(); $editRefBtn.classList.toggle("hidden", max <= 0); $editRefBtn.disabled = editRefs.length >= max; $editRefBtn.textContent = max > 0 ? `＋ ref (${editRefs.length}/${max})` : "＋ ref"; }
function renderEditRefs() {
  $editRefs.innerHTML = "";
  editRefs.forEach((rf, i) => {
    const chip = document.createElement("div"); chip.className = "ref-chip";
    const im = document.createElement("img"); im.src = viewUrl(rf.id); im.alt = "reference"; chip.appendChild(im);
    const x = document.createElement("button"); x.type = "button"; x.textContent = "×"; x.title = "Remove reference"; x.addEventListener("click", () => { editRefs.splice(i, 1); renderEditRefs(); });
    chip.appendChild(x); $editRefs.appendChild(chip);
  });
  $editRefs.classList.toggle("hidden", editRefs.length === 0); updateEditRefBtn(); updateEditRefHint();
}
function editRefHint() { const m = editModel(); const r = m && m.edit && m.edit.reference; return (r && r.hint) || ""; }
function updateEditRefHint() { const txt = editRefHint(); $editRefHint.textContent = txt; $editRefHint.classList.toggle("hidden", editRefs.length === 0 || !txt); }
$editRefBtn.addEventListener("click", () => $editRefFile.click());
$editRefFile.addEventListener("change", async e => {
  const f = e.target.files && e.target.files[0]; e.target.value = "";
  if (!f || editRefs.length >= editRefMax()) return;
  if (!(/^image\//.test(f.type) || /\.(png|jpe?g|webp|gif|bmp|avif|heic|heif)$/i.test(f.name))) { setStatus("Please choose an image file.", { error: true }); return; }
  setStatus("Uploading reference…");
  try { const id = await uploadToInput(f, f.name || "ref.png"); editRefs.push({ id }); renderEditRefs(); setStatus(""); }
  catch (err) { setStatus(friendlyError(err), { error: true }); }
});

// --- last frame (i2v first/last-frame editors) --------------------------------------------------
// A single optional END frame, offered only when the primary editor accepts one (supportsLastFrame) — a single-model
// affordance like references (there's no primary with 2+ checked). The chip mirrors a ref chip; the button hides once
// one is picked (only one end frame). runOneEdit sends it as lastFrameImageId so the graph swaps to
// WanFirstLastFrameToVideo, interpolating from the source (first frame) to this one.
const editSupportsLastFrame = () => { const m = editModel(); return !!(m && m.supportsLastFrame); };
function updateEditLastFrameBtn() { $editLastFrameBtn.classList.toggle("hidden", !editSupportsLastFrame() || !!lastFrameId); }
function renderEditLastFrame() {
  $editLastFrame.innerHTML = "";
  const supported = editSupportsLastFrame();
  if (lastFrameId && supported) {
    const chip = document.createElement("div"); chip.className = "ref-chip";
    const im = document.createElement("img"); im.src = viewUrl(lastFrameId); im.alt = "last frame"; chip.appendChild(im);
    const x = document.createElement("button"); x.type = "button"; x.textContent = "×"; x.title = "Remove last frame";
    x.addEventListener("click", () => { lastFrameId = null; renderEditLastFrame(); });
    chip.appendChild(x); $editLastFrame.appendChild(chip);
  }
  $editLastFrame.classList.toggle("hidden", !(lastFrameId && supported));
  updateEditLastFrameBtn();
}
$editLastFrameBtn.addEventListener("click", () => $editLastFrameFile.click());
$editLastFrameFile.addEventListener("change", async e => {
  const f = e.target.files && e.target.files[0]; e.target.value = "";
  if (!f) return;
  if (!(/^image\//.test(f.type) || /\.(png|jpe?g|webp|gif|bmp|avif|heic|heif)$/i.test(f.name))) { setStatus("Please choose an image file.", { error: true }); return; }
  setStatus("Uploading last frame…");
  try { lastFrameId = await uploadToInput(f, f.name || "last_frame.png"); renderEditLastFrame(); setStatus(""); }
  catch (err) { setStatus(friendlyError(err), { error: true }); }
});

// --- chat edit: fan the instruction across every selected model --------------------------------
async function sendEdit() {
  const instruction = $instruction.value.trim();
  const models = editModels();
  if (!models.length) { setStatus("Pick at least one workflow.", { error: true }); return; }
  if (busy) return;
  if (!editCurrent) { setStatus("Select a file to edit first.", { error: true }); return; }
  // Empty prompts are allowed — never block submit on a blank instruction.
  await runEdit(instruction, models);   // keep the prompt in the box so it can be tweaked + re-applied
}
// Every run of a fan-out is its OWN queued job, so Cancel has to reach all of them: /interrupt (what trackPrompt's own
// canceller posts) only kills the graph ComfyUI happens to be rendering, which leaves the rest of the fan-out to render
// anyway. Same reasoning as the inpaint batch — collect each id as the server accepts it and cancel BY ID.
let editJobIds = [];
let editMade = 0;   // images actually produced this batch (excludes no-change / failed runs)
const cancelEditBatch = () => Promise.all(editJobIds.map(id =>
  fetch(`${GATEWAY}/cancel/${encodeURIComponent(id)}`, { method: "POST" }).catch(() => {})));
// Batch bookkeeping + the shared bar/ETA wiring, shared by a fresh Apply and reconnect-on-return. One shared bar for
// single and multi: the queue renders edits one at a time, so only the running model emits frames, and barBase/barSpan
// map that model's fraction into its slice of the overall total (see showBar).
function beginEditBatch(n, etaPool) {
  multiDone = 0; multiTotal = n; editMade = 0; editJobIds = [];
  barBase = 0; barSpan = n > 1 ? 1 / n : 1; etaPending = etaPool || 0;
  cancelRequested = false; setBusy(true);
  activeGen = { cancel: cancelEditBatch };   // Cancel stops the WHOLE fan-out, not just the one being rendered
  showProgressBar(true); editProgressEl = $bar.querySelector("i"); editEtaEl = $eta;
}
function endEditBatch() {
  // Nothing landed (every run failed / declined / cancelled): the box still holds the loading placeholder, so drop it
  // rather than leave it spinning forever. The reason is already on the status line.
  if (!editMade) $result.innerHTML = "";
  hideBar();   // stops the ETA countdown on editEtaEl — must run BEFORE we drop the reference
  showProgressBar(false); editProgressEl = null; editEtaEl = null;
  setBusy(false); activeGen = null; editJobIds = [];
  barBase = 0; barSpan = 1; multiDone = 0; multiTotal = 0; etaPending = 0;
}
// Track ONE queued/running chat edit into the shared bar + big box. Shared by a fresh Apply (after its POST) and by
// reconnect-on-return (which already has the job id from /jobs).
async function trackEditRun(promptId, model, instruction, notice) {
  try {
    const result = await trackPrompt(promptId, editTrackHooks(model));
    if (applyEditOutput(result, model, instruction, notice)) { editMade++; setStatus(""); }
    else setStatus("No visible change — the editor likely declined this edit. Try rephrasing or a different editor.", { error: true });
  } catch (e) {
    setStatus((cancelRequested || (e && e.name === "AbortError")) ? "Cancelled." : friendlyError(e), { error: true });
  } finally {
    // Advance the overall-progress window so the shared bar steps forward as each model finishes.
    multiDone++; barBase = multiDone / Math.max(1, multiTotal);
    if (!cancelRequested && multiTotal > 1 && multiDone < multiTotal) setStatus(`Made ${multiDone} of ${multiTotal}…`);
  }
}
// POST one edit, then track it into the single result box.
async function runOneEdit(model, instruction, refIds, overrides, single) {
  let promptId, notice;
  try {
    // The end frame is a single-model affordance (no primary with 2+ checked) and only for editors that accept one.
    const lastFrame = (single && model.supportsLastFrame) ? lastFrameId : null;
    const r = await fetch(`${GATEWAY}/edit`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ workflow: gwModel(model), instruction, negativePrompt: editNegFor(model), imageId: editCurrent, referenceImageIds: refIds, lastFrameImageId: lastFrame, overrides }) });
    if (!r.ok) throw new Error(await gwError(r));
    const resp = await r.json();
    promptId = resp.promptId; notice = resp.notice;
    editJobIds.push(promptId);
    // Cancel can land while this POST is still in flight — the job exists on the server now, so cancel it here rather
    // than let it render behind the user's back. trackPrompt then reads it back as cancelled and reports it.
    if (cancelRequested) await fetch(`${GATEWAY}/cancel/${encodeURIComponent(promptId)}`, { method: "POST" }).catch(() => {});
    showPendingNotice(notice);   // yellow text on the placeholder right away (before the render)
    postPending({ jobId: promptId, prompt: instruction, model: model.friendly_name, modelId: model.id, aspect: "" }).catch(() => {});
  } catch (e) {
    setStatus((cancelRequested || (e && e.name === "AbortError")) ? "Cancelled." : friendlyError(e), { error: true });
    multiDone++; barBase = multiDone / Math.max(1, multiTotal); return;
  }
  await trackEditRun(promptId, model, instruction, notice);
}
async function runEdit(instruction, models) {
  if (busy || !editCurrent || !models.length) return;
  const single = models.length === 1;
  // Reference images stay a single-model affordance (no primary when 2+). Shared params (the intersection panel,
  // params common to every selected model) apply to all of them.
  const refIds = single ? editRefs.map(r => r.id) : [];
  const overrides = readOverrides($("editParams"));
  if (single) { editRefs = []; renderEditRefs(); }
  renderResultLoading();
  // Seed the cumulative-ETA pool with every model's estimate; each onStart peels its own off as it begins.
  beginEditBatch(models.length, single ? 0 : models.reduce((a, m) => a + Number(m.avgSeconds || 0), 0));
  setStatus(single ? "" : `Editing across ${models.length} workflows…`);
  try {
    // Re-roll [a|b|…] randomization per model so a multi-model fan-out can differ across workflows.
    await Promise.all(models.map(m => runOneEdit(m, expandRandomPrompt(instruction), refIds, overrides, single)));
    if (cancelRequested) return;
    if (!single) setStatus(editMade === models.length ? `Done — made all ${models.length}.` : `Done — made ${editMade} of ${models.length}.`);
  } finally { endEditBatch(); }
}
$editComposer.addEventListener("submit", e => { e.preventDefault(); if (busy) cancelGeneration(); else sendEdit(); });
// The chat instruction box doubles as the full TAG prompt for whole-image redraws (Anima/Photanima): same '#'/'@'
// autocomplete, gated on the primary editor's tagging — inert for instruction/animate editors, which have none.
// Enter does NOT apply the edit; Apply is the only way to start one. The popup still consumes Enter to accept a
// highlighted tag while it is open, which is the only special meaning Enter has in this box.
if ($instruction && $instructionTagPop) initTagBox({ input: $instruction, pop: $instructionTagPop, getModel: editModel });

// --- inpaint mode -------------------------------------------------------------------------------
function populateInpaintMenu() {
  const models = inpaintModelList();
  if (!models.length) { $inpaintModelToggle.textContent = "No inpaint workflows installed"; $inpaintGo.disabled = true; return; }
  $inpaintGo.disabled = false;
  // Restore the last-used inpaint workflow from the account; else the catalog default / first.
  if (!models.some(m => m.id === selectedInpaintId)) {
    selectedInpaintId = (models.find(m => m.id === savedInpaintWorkflowId) || models.find(m => m.edit && m.edit.default) || models[0]).id;
  }
  buildMenu($inpaintModelMenu, models, selectedInpaintId);
  syncInpaintLabel(); renderInpaintParams(); updateInpaintNeg();
}
function syncInpaintLabel() { const m = inpaintModel(); $inpaintModelToggle.innerHTML = ""; const s = document.createElement("span"); s.textContent = m ? cleanName(m) : "Pick a workflow…"; $inpaintModelToggle.appendChild(s); }
// Render the inpaint param panel and prefill it from the shared flat param map.
function renderInpaintParams() { renderParamFields($inpaintParams, inpaintModel()); restoreParams($inpaintParams); }
function selectInpaint(id) { selectedInpaintId = id; savePrefs(); $inpaintModelMenu.querySelectorAll(".model-opt").forEach(o => o.classList.toggle("selected", o.dataset.id === id)); syncInpaintLabel(); renderInpaintParams(); updateInpaintNeg(); }
$inpaintParams.addEventListener("change", () => persistParams($inpaintParams));
$inpaintModelToggle.addEventListener("click", () => openMenu($inpaintModelMenu, $inpaintModelToggle, $inpaintModelMenu.hidden));
$inpaintModelMenu.addEventListener("click", e => { const opt = e.target.closest(".model-opt"); if (!opt) return; selectInpaint(opt.dataset.id); openMenu($inpaintModelMenu, $inpaintModelToggle, false); });
document.addEventListener("pointerdown", e => { if (!$inpaintModelMenu.hidden && !$inpaintModelSelect.contains(e.target)) openMenu($inpaintModelMenu, $inpaintModelToggle, false); }, true);

// Paint canvas. The mask is painted at FULL opacity (solid → unambiguous binary mask); the see-through tint is
// purely a CSS opacity on the canvas element, so the extracted mask is always 100% solid, never 50%.
function setupMaskStage() {
  $maskStage.innerHTML = ""; maskCanvas = maskCtx = null;
  if (!inpaintBase) { $maskStage.appendChild(selectFileButton("Select a file to inpaint")); return; }
  const img = new Image(); img.className = "mask-img"; img.alt = ""; img.decoding = "async";
  const canvas = document.createElement("canvas"); canvas.className = "mask-canvas";
  img.onload = () => { canvas.width = img.naturalWidth || 1024; canvas.height = img.naturalHeight || 1024; maskCtx = canvas.getContext("2d"); };
  img.src = viewUrl(inpaintBase);
  $maskStage.appendChild(img); $maskStage.appendChild(canvas);
  maskCanvas = canvas; bindPaint(canvas);
}
function bindPaint(canvas) {
  let drawing = false;
  const stamp = e => {
    if (!maskCtx) return;
    const r = canvas.getBoundingClientRect(); if (!r.width) return;
    const scale = canvas.width / r.width;
    const x = (e.clientX - r.left) * scale, y = (e.clientY - r.top) * scale;
    const radius = Math.max(1, (Number($brushSize.value) || 56) * scale / 2);
    maskCtx.globalCompositeOperation = eraseMode ? "destination-out" : "source-over";
    maskCtx.fillStyle = "rgba(255,40,60,1)";              // SOLID — display tint comes from CSS canvas opacity
    maskCtx.beginPath(); maskCtx.arc(x, y, radius, 0, Math.PI * 2); maskCtx.fill();
  };
  canvas.addEventListener("pointerdown", e => { drawing = true; try { canvas.setPointerCapture(e.pointerId); } catch (_) {} stamp(e); });
  canvas.addEventListener("pointermove", e => { if (drawing) stamp(e); });
  const stop = () => { drawing = false; };
  canvas.addEventListener("pointerup", stop); canvas.addEventListener("pointercancel", stop); canvas.addEventListener("pointerleave", stop);
}
function clearMask() { if (maskCtx && maskCanvas) maskCtx.clearRect(0, 0, maskCanvas.width, maskCanvas.height); }
$brushErase.addEventListener("click", () => { eraseMode = !eraseMode; $brushErase.classList.toggle("active", eraseMode); });
$maskClear.addEventListener("click", clearMask);
$brushSize.addEventListener("change", savePrefs);   // brush size rides along in the editor-state blob

// Build a SEPARATE white-on-black mask PNG (white = the painted region to regenerate) and upload it; returns its id.
// The source image is sent untouched, so the model keeps the original pixels outside the mask AND has the real face
// inside it to partially-denoise from. (Baking the mask into the source's alpha blacked out the masked region,
// because PNG drops the RGB under transparent pixels.)
async function buildMaskPng() {
  if (!maskCanvas || !maskCtx) throw new Error("Paint the area to change first.");
  const W = maskCanvas.width, H = maskCanvas.height;
  const md = maskCtx.getImageData(0, 0, W, H).data;
  let any = false; for (let i = 3; i < md.length; i += 4) if (md[i] > 12) { any = true; break; }
  if (!any) throw new Error("Paint the area to change first.");
  const c = document.createElement("canvas"); c.width = W; c.height = H;
  const ctx = c.getContext("2d"); const out = ctx.createImageData(W, H);
  for (let i = 0; i < out.data.length; i += 4) {
    const on = md[i + 3] > 12 ? 255 : 0;            // painted (overlay alpha) → white, opaque
    out.data[i] = on; out.data[i + 1] = on; out.data[i + 2] = on; out.data[i + 3] = 255;
  }
  ctx.putImageData(out, 0, 0);
  const blob = await new Promise(res => c.toBlob(res, "image/png"));
  if (!blob) throw new Error("Couldn't build the mask.");
  return await uploadToInput(blob, "inpaint_mask.png");
}
// One inpaint output card. Clicking opens the LIGHTBOX rather than navigating, so the stage and the painted mask
// survive a look at the result.
function inpaintCard(id) {
  const c = document.createElement("div"); c.className = "result-card";
  const im = document.createElement("img"); im.src = viewUrl(id); im.alt = "result"; im.style.cursor = "pointer";
  im.addEventListener("click", () => openImage(imageId(id)));
  c.appendChild(im);
  return c;
}
// The result box holds ONLY finished pictures, exactly like the gen page's #result: the newest lands in the big box
// and each also reconciles into the Recent strip below. Progress lives in the page-level bar (#inpaintBar), not in a
// card, so a batch never turns the box into a grid of loading cells.
function renderInpaintResult(id) { $inpaintResult.innerHTML = ""; $inpaintResult.appendChild(inpaintCard(id)); }
// Show a fresh inpaint output in the big box AND announce it so the Recent strip re-pulls history (the strip's source
// of truth). A "no visible change" result produced no image — nothing to show or announce. Returns whether an image
// landed, so the batch can count real makes.
function applyInpaintOutput(result) {
  if (result.changed === false) return false;
  renderInpaintResult(result.id);
  document.dispatchEvent(new CustomEvent("imagegen:generated", { detail: { id: result.id } }));
  return true;
}
// Every run of a batch is its OWN queued job, so Cancel has to reach all of them: /interrupt (what trackPrompt's own
// canceller posts) only kills the graph ComfyUI happens to be rendering — which leaves the rest of the batch to render
// anyway, and could even belong to someone else's job. So we collect each job id as the server accepts it and cancel
// BY ID: RenderOrchestrator.Cancel marks the queued slots cancelled and interrupts the running one.
let inpaintJobIds = [];
let inpaintMade = 0;   // images actually produced this batch (excludes no-change / failed runs)
const cancelInpaintBatch = () => Promise.all(inpaintJobIds.map(id =>
  fetch(`${GATEWAY}/cancel/${encodeURIComponent(id)}`, { method: "POST" }).catch(() => {})));
// The whole batch drives ONE shared page bar. The backend renders the queued jobs one at a time and /ws frames carry
// the running job's prompt_id, so trackPrompt only fires onFraction for whichever run is actually rendering — the
// queued siblings stay silent. That lets the overall fraction be (finished + this run's p) / total without the idle
// runs dragging it back down.
function inpaintHooks() {
  return {
    onFraction: p => showBar(Math.min(1, (multiDone + p) / Math.max(1, multiTotal))),
    onStart: res => startEta($inpaintEta, res.expectedSeconds, res.startedAt),
    setActiveGen: () => {},   // the batch canceller (cancelInpaintBatch) owns activeGen — a per-run one would clobber it
  };
}
function showInpaintBar(show) { $inpaintBar.classList.toggle("show", show); if (!show) $inpaintBar.querySelector("i").style.width = "0"; }
// Batch bookkeeping + the shared bar/ETA wiring, shared by a fresh Generate and reconnect-on-return.
function beginInpaintBatch(n) {
  multiDone = 0; multiTotal = n; inpaintMade = 0; inpaintJobIds = [];
  cancelRequested = false; setBusy(true);
  activeGen = { cancel: cancelInpaintBatch };   // Cancel stops the WHOLE batch, not just the one being rendered
  showInpaintBar(true); editProgressEl = $inpaintBar.querySelector("i"); editEtaEl = $inpaintEta; barBase = 0; barSpan = 1;
  showBar(0.02);
}
function endInpaintBatch() {
  hideBar();   // stops the ETA countdown on editEtaEl — must run BEFORE we drop the reference
  showInpaintBar(false); editProgressEl = null; editEtaEl = null;
  setBusy(false); activeGen = null; inpaintJobIds = []; multiDone = 0; multiTotal = 1;
}
// Track ONE queued/running inpaint job into the shared bar + big box. Shared by a fresh Generate (after its POST) and
// reconnect-on-return (which already has the job id from /jobs). The stage stays exactly as it was — same base image,
// same painted mask — so the region can be re-rolled without re-painting.
async function trackInpaintRun(promptId) {
  try {
    const result = await trackPrompt(promptId, inpaintHooks());
    if (applyInpaintOutput(result)) { inpaintMade++; setStatus(""); }
    else setStatus("No visible change — try a bigger mask, a higher change amount, or a different prompt.", { error: true });
  } catch (e) {
    setStatus((cancelRequested || (e && e.name === "AbortError")) ? "Cancelled." : friendlyError(e), { error: true });
  } finally {
    multiDone++;
    if (!cancelRequested && multiTotal > 1 && multiDone < multiTotal) setStatus(`Made ${multiDone} of ${multiTotal}…`);
  }
}
// POST one inpaint, then track it. Every run of a batch posts the IDENTICAL base + mask + prompt; the server fills a
// fresh random seed per job (RenderOrchestrator.WithSeed fills one unless the caller pinned it, and no inpaint
// workflow exposes a seed param), so n runs give n different takes on the same region.
async function runOneInpaint(model, prompt, maskId, overrides) {
  let promptId;
  try {
    const r = await fetch(`${GATEWAY}/edit`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ workflow: gwModel(model), instruction: prompt, negativePrompt: inpaintNegFor(model), imageId: inpaintBase, maskImageId: maskId, referenceImageIds: [], overrides }) });
    if (!r.ok) throw new Error(await gwError(r));
    promptId = (await r.json()).promptId;
    inpaintJobIds.push(promptId);
    // Cancel can land while this POST is still in flight — the job exists on the server now, so cancel it here rather
    // than let it render behind the user's back. trackPrompt then reads it back as cancelled and reports it.
    if (cancelRequested) await fetch(`${GATEWAY}/cancel/${encodeURIComponent(promptId)}`, { method: "POST" }).catch(() => {});
    postPending({ jobId: promptId, prompt, model: model.friendly_name, modelId: model.id, aspect: "" }).catch(() => {});
  } catch (e) {
    setStatus((cancelRequested || (e && e.name === "AbortError")) ? "Cancelled." : friendlyError(e), { error: true });
    multiDone++; return;
  }
  await trackInpaintRun(promptId);
}
// Inpaint n images from the same base + mask + prompt. n comes from the Generate button's hold-to-reveal count picker
// (a plain click = 1), exactly like the gen page. The mask is built ONCE and every run shares it; the runs are posted
// together and the queue renders them one at a time.
async function inpaintGenerate(n) {
  const prompt = $inpaintPrompt.value.trim();
  const model = inpaintModel();
  if (busy || !model) return;
  if (!inpaintBase) { setStatus("Select a file to inpaint first.", { error: true }); return; }
  n = Math.max(1, n || 1);
  let maskId; setStatus("Preparing mask…");
  try { maskId = await buildMaskPng(); }
  catch (e) { setStatus(friendlyError(e), { error: true }); return; }
  setStatus(n === 1 ? "Generating…" : `Making ${n}…`);
  const overrides = readOverrides($inpaintParams);
  beginInpaintBatch(n);
  try {
    // Re-roll [a|b|…] randomization per run so the n takes can differ, not just via the server's random seed.
    await Promise.all(Array.from({ length: n }, () => runOneInpaint(model, expandRandomPrompt(prompt), maskId, overrides)));
    if (cancelRequested) return;
    if (n > 1) setStatus(inpaintMade === n ? `Done — made all ${n}.` : `Done — made ${inpaintMade} of ${n}.`);
    else if (inpaintMade === 0) renderInpaintResult(inpaintBase);   // single run failed/no-change: don't leave the box empty
  } finally { endInpaintBatch(); }
}
inpaintTag = initTagBox({ input: $inpaintPrompt, pop: $inpaintTagPop, getModel: inpaintModel });
// The same booru '#'/'@' autocomplete on the negative boxes (chat + inpaint), gated on the active editor's tagging
// (so it's inert for non-tag editors — which don't show a negative box anyway). Uses the primary model per mode.
if ($editNeg && $editNegTagPop) initTagBox({ input: $editNeg, pop: $editNegTagPop, getModel: editModel });
if ($inpaintNeg && $inpaintNegTagPop) initTagBox({ input: $inpaintNeg, pop: $inpaintNegTagPop, getModel: inpaintModel });
// Hold Generate to pick how many to make (core.js's shared picker — the same one behind the gen page's Generate
// button). A plain click makes 1. A hold while busy is swallowed rather than offering a count: the button reads
// "Cancel" then.
const inpaintCount = attachCountPicker($inpaintGo, { onPick: n => inpaintGenerate(n), onHold: () => busy });
$inpaintComposer.addEventListener("submit", e => {
  e.preventDefault();
  if (inpaintCount.opened) { inpaintCount.opened = false; return; }   // the press was a long-press; the pick submits
  if (busy) cancelGeneration(); else inpaintGenerate(1);
});
let stagedBase = null;
function enterInpaint() {
  if (!$inpaintPrompt.value.trim() && seedPrompt()) $inpaintPrompt.value = seedPrompt();
  if ($inpaintNeg && !$inpaintNeg.value.trim() && seedNegative()) $inpaintNeg.value = seedNegative();
  populateInpaintMenu();
  if (stagedBase !== inpaintBase) { setupMaskStage(); stagedBase = inpaintBase; }   // re-stage only when the base changed
  recoverInpaintJob();   // entering the tab with a batch already running (left the page and came back) → re-attach now
}

// --- outpaint mode ------------------------------------------------------------------------------
// The editor is a FRAME, not a brush: the source is pinned inside an enlarged canvas and you drag its edges out.
// Everything is tracked in SOURCE-NATIVE pixels (what pad_left/top/right/bottom mean to ImagePadForOutpaint); the
// on-screen scale is only ever a rendering detail. Pads snap to 8 so the padded canvas stays VAE-friendly.
let outpaintBase = seed.id, outSrcW = 0, outSrcH = 0, outStagedBase = null, outFrame = null, outScale = 1;
let pads = { left: 0, top: 0, right: 0, bottom: 0 };
const PAD_SNAP = 8, PAD_MAX = 4096;   // PAD_MAX mirrors the workflow schema's own Max — not a cap invented here
const snapPad = v => Math.max(0, Math.min(PAD_MAX, Math.round(v / PAD_SNAP) * PAD_SNAP));
const clampPad = v => Math.max(0, Math.min(PAD_MAX, Math.round(v || 0)));
const padsTotal = () => pads.left + pads.top + pads.right + pads.bottom;

function populateOutpaintMenu() {
  const models = outpaintModelList();
  if (!models.length) { $outpaintModelToggle.textContent = "No outpaint workflows installed"; $outpaintGo.disabled = true; return; }
  $outpaintGo.disabled = false;
  if (!models.some(m => m.id === selectedOutpaintId)) {
    selectedOutpaintId = (models.find(m => m.id === savedOutpaintWorkflowId) || models.find(m => m.edit && m.edit.default) || models[0]).id;
  }
  buildMenu($outpaintModelMenu, models, selectedOutpaintId);
  syncOutpaintLabel(); updateOutpaintNeg();
}
function syncOutpaintLabel() { const m = outpaintModel(); $outpaintModelToggle.innerHTML = ""; const s = document.createElement("span"); s.textContent = m ? cleanName(m) : "Pick a workflow…"; $outpaintModelToggle.appendChild(s); }
function selectOutpaint(id) { selectedOutpaintId = id; savePrefs(); $outpaintModelMenu.querySelectorAll(".model-opt").forEach(o => o.classList.toggle("selected", o.dataset.id === id)); syncOutpaintLabel(); updateOutpaintNeg(); }
$outpaintModelToggle.addEventListener("click", () => openMenu($outpaintModelMenu, $outpaintModelToggle, $outpaintModelMenu.hidden));
$outpaintModelMenu.addEventListener("click", e => { const opt = e.target.closest(".model-opt"); if (!opt) return; selectOutpaint(opt.dataset.id); openMenu($outpaintModelMenu, $outpaintModelToggle, false); });
document.addEventListener("pointerdown", e => { if (!$outpaintModelMenu.hidden && !$outpaintModelSelect.contains(e.target)) openMenu($outpaintModelMenu, $outpaintModelToggle, false); }, true);

// Fit the padded canvas into the stage and place the original at its pad offset. Scale is derived, never stored as
// truth — pads are the model. Capped at 1 so a small source isn't blown up into a blurry preview.
function outLayout() {
  if (!outFrame || !outSrcW) return;
  const fw = outSrcW + pads.left + pads.right, fh = outSrcH + pads.top + pads.bottom;
  const cs = getComputedStyle($outpaintStage);
  const availW = $outpaintStage.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
  const availH = $outpaintStage.clientHeight - parseFloat(cs.paddingTop) - parseFloat(cs.paddingBottom);
  if (availW <= 0 || availH <= 0) return;
  outScale = Math.min(availW / fw, availH / fh, 1);
  outFrame.style.width = fw * outScale + "px"; outFrame.style.height = fh * outScale + "px";
  const img = outFrame.querySelector(".op-img");
  if (img) {
    img.style.left = pads.left * outScale + "px"; img.style.top = pads.top * outScale + "px";
    img.style.width = outSrcW * outScale + "px"; img.style.height = outSrcH * outScale + "px";
  }
}
function updateOutSize() {
  if (!outSrcW) { $outSize.textContent = ""; return; }
  const fw = outSrcW + pads.left + pads.right, fh = outSrcH + pads.top + pads.bottom;
  $outSize.textContent = padsTotal() ? `${outSrcW} × ${outSrcH} → ${fw} × ${fh}` : `${outSrcW} × ${outSrcH}`;
}
function syncPadInputs() { for (const i of $outPads.querySelectorAll("input[data-side]")) i.value = pads[i.dataset.side]; }
// The one place pads change. `snap` is off while a number field is mid-typing (snapping every keystroke fights the
// user); `sync` is off when the change CAME from an input, so we don't clobber the caret.
function setPads(next, { snap = true, sync = true } = {}) {
  const f = snap ? snapPad : clampPad;
  pads = { left: f(next.left), top: f(next.top), right: f(next.right), bottom: f(next.bottom) };
  outLayout(); updateOutSize(); if (sync) syncPadInputs();
}

// Drag a handle outward. The pointer delta is divided by the scale AT DRAG START, not the live scale: the frame
// shrinks as it grows (it refits the stage), so using the live scale would feed back on itself and run away.
function bindOutHandles(frame) {
  frame.addEventListener("pointerdown", e => {
    const h = e.target.closest(".op-h"); if (!h) return;
    e.preventDefault();
    const dir = h.dataset.dir, sx = e.clientX, sy = e.clientY, s0 = outScale || 1, p0 = { ...pads };
    try { h.setPointerCapture(e.pointerId); } catch (_) {}
    const move = ev => {
      const dx = (ev.clientX - sx) / s0, dy = (ev.clientY - sy) / s0;
      const n = { ...p0 };
      if (dir.includes("w")) n.left = p0.left - dx;      // dragging left/up grows the pad, hence the sign flip
      if (dir.includes("e")) n.right = p0.right + dx;
      if (dir.includes("n")) n.top = p0.top - dy;
      if (dir.includes("s")) n.bottom = p0.bottom + dy;
      setPads(n);
    };
    const up = () => { h.removeEventListener("pointermove", move); h.removeEventListener("pointerup", up); h.removeEventListener("pointercancel", up); };
    h.addEventListener("pointermove", move); h.addEventListener("pointerup", up); h.addEventListener("pointercancel", up);
  });
}
function setupOutpaintStage() {
  $outpaintStage.innerHTML = ""; outFrame = null; pads = { left: 0, top: 0, right: 0, bottom: 0 };
  if (!outpaintBase) { outSrcW = outSrcH = 0; syncPadInputs(); updateOutSize(); $outpaintStage.appendChild(selectFileButton("Select a file to outpaint")); return; }
  const frame = document.createElement("div"); frame.className = "op-frame";
  const img = document.createElement("img"); img.className = "op-img"; img.alt = ""; img.decoding = "async";
  frame.appendChild(img);
  for (const dir of ["nw", "n", "ne", "e", "se", "s", "sw", "w"]) {
    const h = document.createElement("div"); h.className = `op-h op-h-${dir}`; h.dataset.dir = dir;
    h.setAttribute("aria-hidden", "true"); frame.appendChild(h);
  }
  // Mount + publish the frame BEFORE the src is set: outLayout() bails on a null outFrame, and a cached image can
  // fire onload on the very next task — before a trailing `outFrame = frame` would have run.
  $outpaintStage.appendChild(frame);
  outFrame = frame; bindOutHandles(frame);
  img.onload = () => { outSrcW = img.naturalWidth || 1024; outSrcH = img.naturalHeight || 1024; outLayout(); syncPadInputs(); updateOutSize(); };
  img.src = viewUrl(outpaintBase);
}
// Extend-only: pad the short axis out to the target ratio, centred. Never crops, so the original always survives.
function padToAspect(r) {
  if (!outSrcW || !r) return;
  let tw = outSrcW, th = outSrcH;
  if (outSrcW / outSrcH < r) tw = Math.round(outSrcH * r); else th = Math.round(outSrcW / r);
  const dx = Math.max(0, tw - outSrcW), dy = Math.max(0, th - outSrcH);
  setPads({ left: dx / 2, right: dx / 2, top: dy / 2, bottom: dy / 2 });
}
$outPresets.addEventListener("click", e => {
  const b = e.target.closest("button"); if (!b) return;
  if (b.id === "outReset") setPads({ left: 0, top: 0, right: 0, bottom: 0 });
  else if (b.dataset.aspect) padToAspect(parseFloat(b.dataset.aspect));
});
$outPads.addEventListener("input", e => {
  const i = e.target.closest("input[data-side]"); if (!i) return;
  setPads({ ...pads, [i.dataset.side]: Number(i.value) }, { snap: false, sync: false });
});
$outPads.addEventListener("change", e => { if (e.target.closest("input[data-side]")) setPads(pads); });
addEventListener("resize", () => { if (activeMode === "outpaint") outLayout(); });

// Clicking opens the LIGHTBOX rather than navigating, so the staged frame and the pad amounts survive a look at the
// result — same as inpaint. (Navigating away threw the whole stage out.)
function renderOutpaintResult(id) {
  $outpaintResult.innerHTML = "";
  const c = document.createElement("div"); c.className = "result-card";
  const im = document.createElement("img"); im.src = viewUrl(id); im.alt = "result"; im.style.cursor = "pointer";
  im.addEventListener("click", () => openImage(imageId(id))); c.appendChild(im);
  $outpaintResult.appendChild(c);
}
function showOutpaintBar(show) { $outpaintBar.classList.toggle("show", show); if (!show) $outpaintBar.querySelector("i").style.width = "0"; }
// Progress lives in the page-level bar (#outpaintBar), not in a card, so the result box holds ONLY finished pictures —
// the gen page's arrangement, and inpaint's. Shared by a fresh Generate and reconnect-on-return.
function beginOutpaintRun() {
  multiDone = 0; multiTotal = 1; barBase = 0; barSpan = 1; etaPending = 0;
  cancelRequested = false; setBusy(true);
  showOutpaintBar(true); editProgressEl = $outpaintBar.querySelector("i"); editEtaEl = $outpaintEta;
  showBar(0.02);
}
function endOutpaintRun() {
  hideBar();   // stops the ETA countdown on editEtaEl — must run BEFORE we drop the reference
  showOutpaintBar(false); editProgressEl = null; editEtaEl = null;
  setBusy(false); activeGen = null; multiTotal = 1;
}
// A finished outpaint: the extended image becomes the new base so you can keep pushing the frame out, side by side.
function applyOutpaintOutput(result) {
  if (result.changed === false) return false;
  outpaintBase = result.id; renderOutpaintResult(result.id); setupOutpaintStage(); outStagedBase = outpaintBase;
  document.dispatchEvent(new CustomEvent("imagegen:generated", { detail: { id: result.id } }));   // Recent reconciles from history
  return true;
}
// Track ONE queued/running outpaint into the page bar + big box. Shared by a fresh Generate and reconnect-on-return.
async function trackOutpaintRun(promptId) {
  try {
    const result = await trackPrompt(promptId, editTrackHooks());
    if (applyOutpaintOutput(result)) setStatus("");
    else { setStatus("No visible change — try extending further or a different prompt.", { error: true }); renderOutpaintResult(outpaintBase); }
  } catch (e) {
    setStatus((cancelRequested || (e && e.name === "AbortError")) ? "Cancelled." : friendlyError(e), { error: true });
    renderOutpaintResult(outpaintBase);
  }
}
async function outpaintGenerate() {
  const prompt = expandRandomPrompt($outpaintPrompt.value.trim());
  const model = outpaintModel();
  if (busy || !model) return;
  if (!outpaintBase) { setStatus("Select a file to outpaint first.", { error: true }); return; }
  // Zero pads would pad by nothing and hand back the source — the outpaint equivalent of an unpainted mask.
  if (!padsTotal()) { setStatus("Drag an edge outward to extend the canvas first.", { error: true }); return; }
  setStatus("");
  beginOutpaintRun();
  // The pads are the ONLY override. Everything else (fill strength, feather, mask grow, LLLite) stays at the
  // configuration's defaults, exactly as a bare API call gets them.
  //
  // Do NOT reintroduce readOverrides() here. The editor's param map (editParamPrefs) is flat and keyed by param NAME
  // across every panel, and `denoise` is "Change amount" (default 0.6, min 0.2) to anima-inpaint but "Fill strength"
  // (default 1.0, min 0.5) to anima-outpaint. Feeding inpaint's denoise in half-denoised the grey padding that
  // ImagePadForOutpaint lays down, so the border came back grey instead of painted.
  const overrides = { pad_left: pads.left, pad_top: pads.top, pad_right: pads.right, pad_bottom: pads.bottom };
  try {
    const r = await fetch(`${GATEWAY}/edit`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ workflow: gwModel(model), instruction: prompt, negativePrompt: outpaintNegFor(model), imageId: outpaintBase, referenceImageIds: [], overrides }) });
    if (!r.ok) throw new Error(await gwError(r));
    const promptId = (await r.json()).promptId;
    // Cancel can land while this POST is still in flight — the job exists on the server now, so cancel it by id.
    if (cancelRequested) await fetch(`${GATEWAY}/cancel/${encodeURIComponent(promptId)}`, { method: "POST" }).catch(() => {});
    postPending({ jobId: promptId, prompt, model: model.friendly_name, modelId: model.id, aspect: "" }).catch(() => {});
    await trackOutpaintRun(promptId);
  } catch (e) {
    setStatus((cancelRequested || (e && e.name === "AbortError")) ? "Cancelled." : friendlyError(e), { error: true });
    renderOutpaintResult(outpaintBase);
  } finally { endOutpaintRun(); }
}
initTagBox({ input: $outpaintPrompt, pop: $outpaintTagPop, getModel: outpaintModel });
if ($outpaintNeg && $outpaintNegTagPop) initTagBox({ input: $outpaintNeg, pop: $outpaintNegTagPop, getModel: outpaintModel });
$outpaintComposer.addEventListener("submit", e => { e.preventDefault(); if (busy) cancelGeneration(); else outpaintGenerate(); });
function enterOutpaint() {
  if (!$outpaintPrompt.value.trim() && seedPrompt()) $outpaintPrompt.value = seedPrompt();
  if ($outpaintNeg && !$outpaintNeg.value.trim() && seedNegative()) $outpaintNeg.value = seedNegative();
  populateOutpaintMenu();
  if (outStagedBase !== outpaintBase) { setupOutpaintStage(); outStagedBase = outpaintBase; }   // re-stage only when the base changed
  else outLayout();   // the stage had no size while hidden, so re-fit on every entry
  recoverOutpaintJob();   // switching INTO the tab reattaches to an outpaint still running for this base
}

// --- tabs ---------------------------------------------------------------------------------------
function setMode(mode) {
  if (!["edit", "redraw", "upscale", "effects", "animate", "inpaint", "outpaint", "video"].includes(mode)) mode = "edit";
  activeMode = mode;
  for (const t of $editTabs.querySelectorAll(".edit-tab")) t.classList.toggle("active", t.dataset.mode === mode);
  const chat = mode !== "inpaint" && mode !== "outpaint";
  $chatMode.classList.toggle("hidden", !chat);
  $inpaintMode.classList.toggle("hidden", mode !== "inpaint");
  $outpaintMode.classList.toggle("hidden", mode !== "outpaint");
  // V2V (video) has no prompt — the quantize is deterministic — so hide the instruction box; only its params matter.
  const instrField = $instruction.closest(".field");
  if (instrField) instrField.hidden = (mode === "video");
  if (chat) { chatBucket = mode; populateChatMenu(); }   // chat modes: edit | redraw | upscale | effects | animate | video
  else if (mode === "inpaint") enterInpaint();
  else enterOutpaint();
  refreshTabSelect();   // keep the mobile mirror's selected option on the active mode
}
// Reflect the source's media type in the tab bar: a clip source shows ONLY the "Pixelize" (V2V) tab; an image source
// shows the four image-editing tabs and hides the video one. Called on boot and whenever the source changes.
function applySourceMediaUi() {
  for (const t of $editTabs.querySelectorAll(".edit-tab")) {
    const isVideoTab = t.dataset.mode === "video";
    t.hidden = srcIsVideo ? !isVideoTab : isVideoTab;
  }
  refreshTabSelect();   // the source-media split changed which tabs exist — rebuild the mobile mirror to match
}
// The mobile tab select (shown in place of the pill row on a phone) is DERIVED from the tab bar: one <option> per
// VISIBLE tab, its value tracking activeMode. Rebuilt whenever the tabs or the mode change, so there's no second list
// to keep in sync and the source-media split (image tabs vs the lone Pixelize tab) carries over for free.
function refreshTabSelect() {
  if (!$editTabsSelect) return;
  $editTabsSelect.innerHTML = "";
  for (const t of $editTabs.querySelectorAll(".edit-tab")) {
    if (t.hidden) continue;
    const o = document.createElement("option");
    o.value = t.dataset.mode; o.textContent = t.textContent;
    $editTabsSelect.appendChild(o);
  }
  $editTabsSelect.value = activeMode;
}
// User tab switch persists the active tab (the account blob); boot's setMode is a pure restore, so it doesn't save.
$editTabs.addEventListener("click", e => { const t = e.target.closest(".edit-tab"); if (t && !busy) { setMode(t.dataset.mode); savePrefs(); } });
// The mobile select drives the same setMode. While a run is in flight the tab bar ignores clicks, so the select does
// too — snap it back to the active mode rather than leave an unapplied choice showing.
$editTabsSelect.addEventListener("change", () => {
  if (busy) { $editTabsSelect.value = activeMode; return; }
  setMode($editTabsSelect.value); savePrefs();
});

// --- recover an in-flight job on reload / return --------------------------------------------------
// All three /edit flows (chat edit, inpaint, outpaint) come back from /jobs as kind==="edit" on the same source
// image, so the ONLY thing that tells them apart is the workflow id. Each recoverer claims just its own bucket.
let recovering = false;
const inpaintWorkflowIds = () => new Set(inpaintModelList().map(gwModel));
const outpaintWorkflowIds = () => new Set(outpaintModelList().map(gwModel));
// Reconnect to EVERY chat edit still running for this source — a fan-out across N workflows is N separate jobs, so
// re-attaching to only the running one left the other N-1 to finish invisibly. Same shape as recoverInpaintJob.
let editRecovering = false;   // set BEFORE the /jobs await so overlapping calls (boot + tab-enter + interval) can't double-attach
async function recoverEditJob() {
  if (busy || recovering || editRecovering || activeMode === "inpaint" || activeMode === "outpaint") return;
  editRecovering = true;
  try {
    let res; try { const r = await fetch(`${GATEWAY}/jobs`); if (!r.ok) return; res = await r.json(); } catch (_) { return; }
    const inp = inpaintWorkflowIds(), out = outpaintWorkflowIds();
    // Keyed on the CURRENT source, like its inpaint/outpaint siblings — the seed is only the source the page
    // opened with, and an upload (the no-source flow) replaces it.
    const mine = (res.jobs || []).filter(j => j.kind === "edit" && (j.status === "running" || j.status === "queued")
      && j.sourceImageId === editCurrent && !inp.has(j.model) && !out.has(j.model));
    if (!mine.length) return;
    recovering = true;
    renderResultLoading();
    beginEditBatch(mine.length, 0);
    editJobIds = mine.map(j => j.jobId);   // the batch canceller cancels exactly these
    setStatus(mine.length > 1 ? `Making ${mine.length}…` : "Reconnecting to your edit…");
    try {
      // The job's workflow id resolves back to its catalog row, so a recovered video edit still renders as a clip.
      await Promise.all(mine.map(j => trackEditRun(j.jobId, EDIT_MODELS[j.model] || null, j.prompt || "", null)));
      if (cancelRequested) return;
      if (mine.length > 1) setStatus(editMade === mine.length ? `Done — made all ${mine.length}.` : `Done — made ${editMade} of ${mine.length}.`);
    } finally { endEditBatch(); recovering = false; }
  } finally { editRecovering = false; }
}
// Reconnect to an inpaint batch still running for THIS base image — so leaving the page and coming back shows the
// Generate button as Cancel, the live bar, and each result as it lands, exactly like the gen page. Every run of the
// batch is its own job; we re-attach to all of them at once and drive the same shared bar a fresh Generate uses.
let inpaintRecovering = false;   // set BEFORE the /jobs await so overlapping calls (boot + tab-enter + interval) can't double-attach
async function recoverInpaintJob() {
  if (busy || recovering || inpaintRecovering || activeMode !== "inpaint") return;
  inpaintRecovering = true;
  try {
    const inp = inpaintWorkflowIds();
    let res; try { const r = await fetch(`${GATEWAY}/jobs`); if (!r.ok) return; res = await r.json(); } catch (_) { return; }
    const mine = (res.jobs || []).filter(j => j.kind === "edit" && (j.status === "running" || j.status === "queued")
      && j.sourceImageId === inpaintBase && inp.has(j.model));
    if (!mine.length) return;
    recovering = true;
    beginInpaintBatch(mine.length);
    inpaintJobIds = mine.map(j => j.jobId);   // the batch canceller cancels exactly these
    setStatus(mine.length > 1 ? `Making ${mine.length}…` : "Reconnecting to your inpaint…");
    try {
      await Promise.all(mine.map(j => trackInpaintRun(j.jobId)));
      if (cancelRequested) return;
      if (mine.length > 1) setStatus(inpaintMade === mine.length ? `Done — made all ${mine.length}.` : `Done — made ${inpaintMade} of ${mine.length}.`);
      else if (inpaintMade === 0 && $inpaintResult.children.length === 0) renderInpaintResult(inpaintBase);
    } finally { endInpaintBatch(); recovering = false; }
  } finally { inpaintRecovering = false; }
}
// Reconnect to an outpaint still running for THIS base image, so a reload shows Cancel, the live bar, and the result
// when it lands — the last tab that had no recovery at all.
let outpaintRecovering = false;
async function recoverOutpaintJob() {
  if (busy || recovering || outpaintRecovering || activeMode !== "outpaint") return;
  outpaintRecovering = true;
  try {
    const out = outpaintWorkflowIds();
    let res; try { const r = await fetch(`${GATEWAY}/jobs`); if (!r.ok) return; res = await r.json(); } catch (_) { return; }
    const mine = (res.jobs || []).filter(j => j.kind === "edit" && (j.status === "running" || j.status === "queued")
      && j.sourceImageId === outpaintBase && out.has(j.model));
    if (!mine.length) return;
    const job = mine.find(j => j.status === "running") || mine[0];
    recovering = true;
    beginOutpaintRun();
    setStatus("Reconnecting to your outpaint…");
    try { await trackOutpaintRun(job.jobId); } finally { endOutpaintRun(); recovering = false; }
  } finally { outpaintRecovering = false; }
}

// --- boot ---------------------------------------------------------------------------------------
(async () => {
  await loadEditModels();   // also seeds savedMode/savedBrushSize/editParamPrefs/selectedEditIds from the account blob
  editCurrent = seed.id;
  srcIsVideo = await detectSrcVideo(seed.id);   // a clip seed → collapse the editor to the single V2V mode
  renderSrc(); renderEditRefs(); renderEditLastFrame();
  applySourceMediaUi();
  if (savedBrushSize != null) $brushSize.value = savedBrushSize;
  if (srcIsVideo) {
    // The source is a clip: pixel-quantize V2V is the ONLY option, regardless of the saved tab.
    setMode("video");
  } else {
    let saved = savedMode || "edit";
    // Don't land on an empty tab: fall back to one that has models.
    const has = { edit: chatHasModels("edit"), redraw: chatHasModels("redraw"), upscale: chatHasModels("upscale"), effects: chatHasModels("effects"), animate: chatHasModels("animate"), inpaint: inpaintModelList().length > 0, outpaint: outpaintModelList().length > 0 };
    if (!has[saved]) saved = ["edit", "redraw", "upscale", "effects", "animate", "inpaint", "outpaint"].find(k => has[k]) || "edit";
    setMode(saved);
  }
  setTimeout(() => { if (activeMode !== "inpaint" && activeMode !== "outpaint" && activeMode !== "video") $instruction.focus(); }, 50);
  recoverEditJob(); recoverInpaintJob(); recoverOutpaintJob();
})();
function chatHasModels(bucket) {
  const prev = chatBucket; chatBucket = bucket; const n = chatModels().length; chatBucket = prev; return n > 0;
}
document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") { recoverEditJob(); recoverInpaintJob(); recoverOutpaintJob(); } });
setInterval(() => { recoverEditJob(); recoverInpaintJob(); recoverOutpaintJob(); }, 3000);
