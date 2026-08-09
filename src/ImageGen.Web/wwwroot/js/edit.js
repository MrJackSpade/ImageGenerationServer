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
      $editSrc = $("editSrc"), $bar = $("bar"), $eta = $("eta"), $cancelEdit = $("cancelEdit"), $result = $("result"), $editComposer = $("editComposer"),
      $instruction = $("instruction"), $instructionTagPop = $("instructionTagPop"), $editSend = $("editSend"), $status = $("status"),
      $editRefs = $("editRefs"), $editRefBtn = $("editRefBtn"), $editRefFile = $("editRefFile"), $editRefHint = $("editRefHint"),
      $editLastFrame = $("editLastFrame"), $editLastFrameBtn = $("editLastFrameBtn"), $editLastFrameFile = $("editLastFrameFile"),
      $editLoopWrap = $("editLoopWrap"), $editLoop = $("editLoop"),
      $editSrcFile = $("editSrcFile"),
      // inpaint
      $inpaintModelSelect = $("inpaintModelSelect"), $inpaintModelToggle = $("inpaintModelToggle"), $inpaintModelMenu = $("inpaintModelMenu"),
      $inpaintComposer = $("inpaintComposer"), $inpaintPrompt = $("inpaintPrompt"), $inpaintTagPop = $("inpaintTagPop"),
      $inpaintParams = $("inpaintParams"), $inpaintGo = $("inpaintGo"), $inpaintResult = $("inpaintResult"),
      $inpaintBar = $("inpaintBar"), $inpaintEta = $("inpaintEta"), $cancelInpaint = $("cancelInpaint"),
      $maskStage = $("maskStage"), $brushSize = $("brushSize"), $brushErase = $("brushErase"), $maskClear = $("maskClear"),
      // outpaint
      $outpaintModelSelect = $("outpaintModelSelect"), $outpaintModelToggle = $("outpaintModelToggle"), $outpaintModelMenu = $("outpaintModelMenu"),
      $outpaintComposer = $("outpaintComposer"), $outpaintPrompt = $("outpaintPrompt"), $outpaintTagPop = $("outpaintTagPop"),
      $outpaintGo = $("outpaintGo"), $outpaintResult = $("outpaintResult"),
      $outpaintBar = $("outpaintBar"), $outpaintEta = $("outpaintEta"), $cancelOutpaint = $("cancelOutpaint"),
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
// mode's submit control — its buildItems refuses with "Select a file to … first" (and the button is disabled anyway).
// Asserting it here instead would throw at page init, aborting the whole script and taking the file picker with it:
// the one entry point whose job is choosing a source would be the one that couldn't. Checking at the point of use
// also catches what an init assertion can't, a source that goes away or is replaced mid-session.
//
// A MISSING or unparseable #editSeed is a different failure and still stops the page here — it throws inside
// JSON.parse, so it stays distinguishable without guessing. There is deliberately no fallback that seeds
// { id: "", prompt: "(image)" }: that would leave a page looking fully functional but pointed at an image id the
// browser invented, so every Apply would be rejected server-side. An empty id must never be submitted as a render
// source — which is exactly what the submit-path checks guarantee.
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
// Every tab reads the catalog's single resolved `kind` (issue #163) — no name regexes, edit_group magic strings, or
// effect/media side-channels. Inpaint/outpaint get their own tab (their pad/mask params aren't exposed, so the frame
// editor is the only thing that can supply them); redraw/upscale are promoted out of the Edit menu; V2V (videoedit)
// consumes a clip and is offered only for a clip source. A new config lands in the right tab by declaring its kind.
const isInpaint = m => !!(m && m.kind === "inpaint");
const isOutpaint = m => !!(m && m.kind === "outpaint");
const isV2V = m => !!(m && m.kind === "videoedit");
const isRedraw = m => !!(m && m.kind === "redraw");
const isUpscale = m => !!(m && m.kind === "upscale");
// Whether the current source (editCurrent) is a video clip. Decided from /forge/media for a seeded/edited source, and
// from the file type for an upload. When true, the editor collapses to the single V2V "Pixelize" mode.
let srcIsVideo = false;
// Ask the server whether an id is a clip (content type webp/video). Used to flip into V2V mode for a clip source.
async function detectSrcVideo(id) {
  if (!id) return false;
  const key = imageId(id);
  try {
    const r = await fetch(`${GATEWAY}/media`, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids: [key] }),
    });
    if (!r.ok) return false;
    const map = await r.json();
    return map[key] === "webp" || map[key] === "mp4";   // clip kinds; a still ("image") is not V2V-eligible
  } catch (e) { console.debug("media kind check failed:", e); return false; }
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
let savedMode = null, savedInpaintWorkflowId = null, savedOutpaintWorkflowId = null, savedBrushSize = null, savedLoop = null;   // seeded from the account blob on boot

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
    loop: $editLoop ? $editLoop.checked : false,   // per-user, cross-device like the rest of the editor state
    // The pad amounts are NOT retained: like the instruction text they're tied to the specific source image, so
    // carrying a prior image's margins onto a new source would silently extend it by the wrong number of pixels.
  });
  clearTimeout(prefsTimer);
  // A silently-swallowed save would leave the editor looking exactly like one whose settings were being kept, and the
  // next page load would quietly come back with older state. Say it once, where the user is looking.
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
let editCurrent = seed.id, editRefs = [];
// Optional END frame for i2v first/last-frame editors (a single uploaded image id, or null). Tied to the current
// source like the instruction text: cleared on a source swap and on manual removal, never persisted to the account.
let lastFrameId = null;
let busy = false, activeGen = null, cancelRequested = false;
// The FIXED image inpaint paints over. Like editCurrent, it never advances on its own: a finished inpaint leaves the
// base and the painted mask in place, so the same region can be re-rolled. Only a new source (upload / click-to-edit
// re-seed) moves it.
let inpaintBase = seed.id;
let maskCanvas = null, maskCtx = null, eraseMode = false, inpaintTag = null;

function setStatus(t, { error = false } = {}) { $status.classList.toggle("error", error); $status.textContent = t; }
// The Apply/Generate button STAYS itself while a render runs — clicking it again queues another job (the shared submit
// control's queue-more), so there is no cancel-adjacent gesture to misfire. The only Cancel is the per-mode button,
// shown only while busy. Mode switching stays free while busy, so the visible mode is NOT the job's mode: the Cancel
// follows the JOB (runningSpecMode, captured at submit/adopt), so it stays on the mode that owns the running job even
// after the user switches tabs to set up another edit. The other two are always cleared so no stale Cancel lingers.
const specModeOf = mode => mode === "inpaint" ? "inpaint" : mode === "outpaint" ? "outpaint" : "chat";
let runningSpecMode = null;   // "chat" | "inpaint" | "outpaint" of the in-flight job; null when idle
function setBusy(b) {
  busy = b;
  if (!b) runningSpecMode = null;
  $cancelEdit.classList.toggle("show", b && runningSpecMode === "chat");
  $cancelInpaint.classList.toggle("show", b && runningSpecMode === "inpaint");
  $cancelOutpaint.classList.toggle("show", b && runningSpecMode === "outpaint");
  // The running job's own panel keeps a slim progress-bar+Cancel surface even after the user switches tabs away from it
  // (CSS: a `.running.hidden` mode div reveals only its .bar-row/.eta). Without this the Cancel would sit inside the
  // now-hidden mode div and be unreachable the moment mode switching is used mid-render.
  $chatMode.classList.toggle("running", b && runningSpecMode === "chat");
  $inpaintMode.classList.toggle("running", b && runningSpecMode === "inpaint");
  $outpaintMode.classList.toggle("running", b && runningSpecMode === "outpaint");
}
function cancelGeneration() { if (!busy || !activeGen) return; cancelRequested = true; setStatus("Cancelling…"); activeGen.cancel(); }

// Each mode's submit is enabled only when that mode has BOTH a source image AND ≥1 available workflow — so it can't
// be clicked (or hold-picked: the browser suppresses pointer events on a disabled button) into the "Select a file
// first" error. Called whenever the source or the workflow set for a mode changes, and on mode switch. A running
// batch keeps its source, so the button stays enabled while busy (a click then queues more).
function updateSubmitEnabled() {
  if ($editSend) $editSend.disabled = !editCurrent || editModels().length === 0;
  if ($inpaintGo) $inpaintGo.disabled = !inpaintBase || inpaintModelList().length === 0;
  if ($outpaintGo) $outpaintGo.disabled = !outpaintBase || outpaintModelList().length === 0;
}

// --- one queued job per submission (the ONLY edit submit path) ----------------------------------
// Every edit mode (chat/animate, inpaint, outpaint) builds a List of enqueue items and POSTs them as ONE /enqueue
// job with N slots — exactly like the gen page's Generate — instead of looping POST /edit per run. The single job is
// tracked below (poll /jobs, render each slot as it lands, drive the mode's bar + ETA) and cancelled as ONE job.
// There is no per-run fan-out and no /edit endpoint any more; the queue renders the N slots one at a time.
let editActiveJobId = null;   // the one job the live tracker owns (for cancel + recover de-dupe)

// Per-mode wiring: which bar/ETA the batch drives, how a finished slot is rendered, the source id that identifies an
// in-flight job to recover on return, and which workflow ids belong to this mode (so recover claims only its own job).
function editModeSpec(mode) {
  if (mode === "inpaint") {
    return { bar: $inpaintBar, eta: $inpaintEta, show: showInpaintBar,
      onSlot: s => renderInpaintResult(s.id), onNoneMade: () => renderInpaintResult(inpaintBase),
      sourceId: () => inpaintBase, mine: id => inpaintWorkflowIds().has(id) };
  }
  if (mode === "outpaint") {
    return { bar: $outpaintBar, eta: $outpaintEta, show: showOutpaintBar,
      onSlot: s => { outpaintBase = s.id; renderOutpaintResult(s.id); setupOutpaintStage(); outStagedBase = outpaintBase; },
      onNoneMade: () => renderOutpaintResult(outpaintBase),
      sourceId: () => outpaintBase, mine: id => outpaintWorkflowIds().has(id) };
  }
  return { bar: $bar, eta: $eta, show: showProgressBar,   // chat = edit + animate
    onSlot: s => showEditResult(s.id, "", EDIT_MODELS[s.model] || null, s.notice), onNoneMade: () => { $result.innerHTML = ""; },
    sourceId: () => editCurrent, mine: id => { const inp = inpaintWorkflowIds(), out = outpaintWorkflowIds(); return !inp.has(id) && !out.has(id); } };
}

// The progress/preview wiring for one edit mode — the object the shared submit control (core.js attachEnqueueSubmit)
// and trackJobBatch consume. Identical in shape to the composer's; only the per-mode bar/eta/result rendering differs
// (editModeSpec). This is why every page's status·ETA·bar·cancel·preview behaves the same and can't drift.
function editPanel(spec) {
  return {
    eta: spec.eta,
    show: spec.show,
    onProgress: f => { const pct = Math.round(Math.min(1, f) * 100); const b = spec.bar.querySelector("i"); if (b) b.style.width = pct + "%"; document.title = `⏳ ${pct}% · Edit · Make a Picture`; },
    onSlot: s => { spec.onSlot(s); document.dispatchEvent(new CustomEvent("imagegen:generated", { detail: { id: s.id } })); },   // Recent reconciles from history
    activeStatus: (recorded, total) => total > 1 ? `Making ${Math.min(recorded + 1, total)} of ${total}…` : null,
    // A FAILED slot (real ComfyUI/render error) makes no image, exactly like a genuine no-change edit — so surface the
    // server's actual error rather than the "no visible change" nudge, which otherwise masks every edit failure.
    finalStatus: (made, total, cancelled, errors) => cancelled ? (made ? `Cancelled — made ${made} of ${total}.` : "Cancelled.")
      : (errors && errors.length) ? (total > 1 ? `Made ${made} of ${total}; ${errors.length} failed — ${errors[0]}` : errors[0])
      : total > 1 ? (made === total ? `Done — made all ${total}.` : `Done — made ${made} of ${total}.`)
      : made ? "" : "No visible change — try rephrasing, a bigger change, or a different workflow.",
    onSettle: made => { document.title = "Edit · Make a Picture"; editActiveJobId = null; if (!made && spec.onNoneMade) spec.onNoneMade(); },
  };
}
// The bits every edit mode's submit control shares: the page's busy flag, cancel handle, status, and pending-record.
// buildItems + the mode's panel are the only per-mode parts, supplied where each control is attached.
function editSubmitBase() {
  return {
    isBusy: () => busy,
    // A fresh submit can only come from the visible mode's composer, so its mode is the active one at this instant —
    // captured now so the Cancel stays with the job even after the user switches tabs mid-render.
    onBusy: b => { if (b) { cancelRequested = false; runningSpecMode = specModeOf(activeMode); } setBusy(b); },
    onActiveGen: h => { activeGen = h; },
    onJob: (jobId, items) => postPending({ jobId, prompt: items[0].instruction || "", model: items[0].workflow, modelId: items[0].workflow, aspect: "" }).catch(e => console.debug("record pending job failed:", e)),
    setStatus,
    startStatus: () => "Generating…",
  };
}

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
    rows.filter(r => r.canEdit).forEach(r => {
      EDIT_MODELS[r.id] = {
        id: r.id, friendly_name: r.friendlyName || r.id, _gw: r.id, workflow: r.workflow,
        kind: r.kind,   // the catalog's resolved kind — every tab routes off this (issue #163)
        exposedParams: r.exposedParams || [], avgSeconds: r.avgSeconds,
        hiddenParams: r.hiddenParams || [],   // shipped hidden-but-revealable params — shown only where the user's visibility prefs reveal them (#191)
        media: r.media === "video" ? "video" : "image", promptDirectsMotion: r.promptDirectsMotion !== false,
        sourceMedia: r.sourceMedia === "video" ? "video" : "image",
        supportsLastFrame: !!r.supportsLastFrame,   // i2v first/last-frame: offer an optional final frame to interpolate to
        hasAudio: !!r.hasAudio,   // clip carries a native audio track (H3) — offer an unmute control on the result

        effectType: r.effectType || null,   // sub-section header within a tab (grouping only — the TAB comes from kind)
        editGroup: r.editGroup || null,      // sub-section header within the Edit tab (grouping only, not tab routing)
        promptSemantics: r.promptSemantics || "instruction",   // instruction | whole_image | masked_region
        takesPrompt: r.takesPrompt !== false,   // false = no text encoder in the graph (upscalers): hide the box
        negativeSupported: !!(r.card && r.card.negativeSupported),   // editor uses a negative prompt (append-on-top)
        tagging: (r.card && r.card.tagging) || null,
        promptGuidance: (r.card && r.card.promptGuidance) || null,   // card's how-to-prompt line → instruction placeholder
        edit: { reference: r.reference || null, default: !!r.default }
      };
    });
    // Restore the editor's last state from the account blob (mode, workflows, flat params, inpaint workflow, brush).
    // editPrefsLoaded is set ONLY on a clean read, and savePrefs refuses to write until it is: swallowing a failure
    // here would drop the user back to defaults and then persist those defaults on the first knob they touched, so a
    // one-off bad read would permanently become their saved editor state. A missing blob is a first visit, which is
    // safe to write.
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
          if (typeof p.loop === "boolean") savedLoop = p.loop;
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
  if (chatBucket === "video") return isV2V(m);      // the V2V (clip-source) bucket — only video-source editors
  if (isV2V(m)) return false;                        // video-source editors never appear in the image buckets
  if (chatBucket === "animate") return m.kind === "animate";
  if (isInpaint(m) || isOutpaint(m)) return false;   // these have their own tabs (separate lists)
  if (chatBucket === "redraw") return isRedraw(m);
  if (isRedraw(m)) return false;                     // redraws have their own tab — never also in Edit/Effects
  if (chatBucket === "upscale") return isUpscale(m);
  if (isUpscale(m)) return false;                    // upscalers have their own tab — never also in Edit/Effects
  return chatBucket === "effects" ? m.kind === "effect" : m.kind === "edit";
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
  onChange: ids => { selectedEditIds = ids; updateEditRefBtn(); updateEditRefHint(); renderEditLastFrame(); updateEditParams(); updateInstructionPlaceholder(); updateEditNeg(); updateSubmitEnabled(); },
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
// an empty instruction is already legal (buildChatItems never blocks on a blank prompt), so hiding the box changes nothing.
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
    // The card's own prompting guidance beats the generic motion line (e.g. ref2va's <Picture 1>/<Video 1> tags).
    $instruction.placeholder = m.promptGuidance
      || (m.promptDirectsMotion
        ? "Optional: describe the motion (e.g. gentle breeze, slow zoom)"
        : "Optional: describe the scene — motion is automatic, not prompt-controlled");
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
  let media;
  if (srcIsVideo) {
    // A clip source: play it looping (the mp4 endpoint transcodes our animated-webp clips and passes real containers through).
    media = document.createElement("video");
    media.src = `${GATEWAY}/image/${encodeURIComponent(imageId(editCurrent))}/mp4`;
    media.loop = true; media.muted = true; media.autoplay = true; media.playsInline = true; media.controls = true;
    media.setAttribute("muted", ""); media.setAttribute("playsinline", "");
  } else {
    media = document.createElement("img"); media.src = viewUrl(editCurrent); media.alt = "image being edited";
    media.addEventListener("click", () => openImage(imageId(editCurrent)));
  }
  $editSrc.appendChild(media);
  $editSrc.appendChild(srcClearButton());
}
// A small "×" overlay on a source preview that clears the source. Upload sets ONE source for every mode
// (editCurrent + inpaintBase + outpaintBase together, :481), so clearing drops all of them — consistent with how
// they're set — returning every stage to its empty "Select a file" picker.
function srcClearButton() {
  const x = document.createElement("button");
  x.type = "button"; x.className = "src-clear"; x.textContent = "×"; x.title = "Clear source";
  x.addEventListener("click", e => { e.stopPropagation(); clearSource(); });
  return x;
}
// Mirror the upload reset at :481-484, but to null: drop the shared source and every source-tied piece of state
// (end frame, video-ness, staged bases), then re-render whichever stage is active so its empty picker returns.
function clearSource() {
  editCurrent = null; inpaintBase = null; outpaintBase = null;
  stagedBase = null; outStagedBase = null;
  lastFrameId = null;                                     // the end frame was tied to the old source
  srcIsVideo = false;                                     // no clip source anymore
  renderSrc(); renderEditLastFrame();
  applySourceMediaUi();
  updateSubmitEnabled();   // no source now → every mode's submit disables
  if (activeMode === "video") setMode("edit");            // leave V2V-only mode — there's no clip to quantize now
  else if (activeMode === "inpaint") setupMaskStage();
  else if (activeMode === "outpaint") setupOutpaintStage();
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
// A file is an image/video by MIME, falling back to extension for pickers/drops that hand over a blank type.
const isImageFile = f => /^image\//.test(f.type) || /\.(png|jpe?g|webp|gif|bmp|avif|heic|heif)$/i.test(f.name);
const isVideoFile = f => /^video\//.test(f.type) || /\.(mp4|webm|mov|mkv)$/i.test(f.name);
const isAudioFile = f => /^audio\//.test(f.type) || /\.(wav|mp3|flac|ogg|m4a|aac)$/i.test(f.name);
// The media kind of a picked/dropped file, or null when it's none of the three families. Matches the server's
// content-type classification so the client rejects a file the workflow won't accept before uploading it.
const fileKind = f => isImageFile(f) ? "image" : isVideoFile(f) ? "video" : isAudioFile(f) ? "audio" : null;
// Shared upload path for the source: the hidden <input>'s change AND every source drop zone (the source box, and the
// inpaint/outpaint empty-state stages, which all seed the one source). Takes the first file — the source is single.
async function handleEditSrcFiles(files) {
  const f = files && files[0];
  if (!f) return;
  const isVid = isVideoFile(f);
  if (!isImageFile(f) && !isVid) { setStatus("Please choose an image or video file.", { error: true }); return; }
  setStatus("Uploading…");
  try {
    const id = await uploadToInput(f, f.name || (isVid ? "edit_src.mp4" : "edit_src.png"));
    editCurrent = id; inpaintBase = id; stagedBase = null;   // the new upload is the source for chat, inpaint AND outpaint
    outpaintBase = id; outStagedBase = null;
    lastFrameId = null;                                      // the end frame was tied to the old source — drop it
    srcIsVideo = isVid;                                       // a clip upload flips the editor into V2V-only mode
    renderSrc(); renderEditLastFrame();
    applySourceMediaUi();
    updateSubmitEnabled();   // a source is now set → enable the submit(s) whose workflows are available
    if (srcIsVideo) setMode("video");                         // clip → the single Pixelize (V2V) mode
    else if (activeMode === "video") setMode("edit");         // switched back to an image source
    else if (activeMode === "inpaint") { setupMaskStage(); stagedBase = inpaintBase; }
    else if (activeMode === "outpaint") { setupOutpaintStage(); outStagedBase = outpaintBase; }
    setStatus("");
  } catch (err) { setStatus(friendlyError(err), { error: true }); }
}
$editSrcFile.addEventListener("change", e => { const files = Array.from(e.target.files || []); e.target.value = ""; handleEditSrcFiles(files); });
// The source box and both empty-state stages accept a dropped image/video — same path as picking one.
attachDropUpload($editSrc, handleEditSrcFiles);
attachDropUpload($maskStage, handleEditSrcFiles);
attachDropUpload($outpaintStage, handleEditSrcFiles);
preventStrayFileDrops();   // a file dropped anywhere else must not navigate the page away
// Open in the lightbox (which carries the detail fragment + its Edit button) if available, else the detail page.
function openImage(id) {
  if (!(window.openImgcard && window.openImgcard(String(id)))) location.href = "/image/" + encodeURIComponent(id);
}
function showProgressBar(show) { $bar.classList.toggle("show", show); if (!show) { $bar.querySelector("i").style.width = "0"; } }
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
    // Native-audio clips (H3): autoplay muted (browsers require it) but show controls so the user can unmute.
    if (model && model.hasAudio) media.controls = true;
  } else {
    media = document.createElement("img"); media.src = viewUrl(id); media.alt = instruction || "edited image";
  }
  media.style.cursor = "pointer";
  media.addEventListener("click", () => openImage(imageId(id)));
  const actions = document.createElement("div"); actions.className = "result-actions";
  const ed = document.createElement("a"); ed.className = "link-btn"; ed.textContent = "✎ Edit this"; ed.href = "/edit/" + encodeURIComponent(imageId(id)); ed.style.marginRight = "auto"; actions.appendChild(ed);
  const dl = document.createElement("a"); dl.className = "download"; dl.href = "#"; dl.textContent = "↓ Save";
  dl.onclick = e => { e.preventDefault(); saveMedia(id, isVid); }; actions.appendChild(dl);
  card.appendChild(media); card.appendChild(actions);
  const n = noticeEl(notice); if (n) card.appendChild(n);
  return card;
}
// Single-model output: one big card. The source on the left stays put, so the NEXT Apply edits the original again.
function showEditResult(id, instruction, model, notice) {
  $result.innerHTML = ""; $result.appendChild(buildResultCard(id, model || editModel(), instruction, notice));
}
// The result box holds ONLY the newest finished picture, exactly like the gen page's #result: a fan-out across N
// workflows lands N images there in turn (last one wins) and each also reconciles into the Recent strip below, which
// is where you compare them. Progress lives in the page-level bar (#bar), never inside a card — and the box stays
// empty until a real result lands (no "working" placeholder), so an in-flight batch never shows an empty spinner box.

// --- references (image / audio / video, per the workflow's declared types) ----------------------
// The workflow's accepted reference types: [{ kind, max }]. Most editors declare only image; ref2va takes all three.
function editRefTypes() { const m = editModel(); const r = m && m.edit && m.edit.reference; return (r && r.types) || []; }
function editRefMaxOf(kind) { const t = editRefTypes().find(x => x.kind === kind); return (t && t.max) || 0; }
function editRefTotalMax() { return editRefTypes().reduce((n, t) => n + (t.max || 0), 0); }
function editRefCountOf(kind) { return editRefs.filter(r => r.kind === kind).length; }
// The <input accept> string from the accepted kinds, so the picker only offers files the workflow takes.
function editRefAccept() { return editRefTypes().filter(t => t.max > 0).map(t => `${t.kind}/*`).join(","); }
function updateEditRefBtn() {
  const total = editRefTotalMax();
  $editRefBtn.classList.toggle("hidden", total <= 0);
  $editRefBtn.disabled = editRefs.length >= total;
  $editRefBtn.textContent = total > 0 ? `＋ ref (${editRefs.length}/${total})` : "＋ ref";
  $editRefFile.accept = editRefAccept() || "image/*";
}
function renderEditRefs() {
  $editRefs.innerHTML = "";
  editRefs.forEach((rf, i) => {
    const chip = document.createElement("div"); chip.className = "ref-chip";
    // Only an image reference has a thumbnail; an audio/video reference shows a kind glyph (its preview isn't an <img>).
    if (rf.kind === "image") {
      const im = document.createElement("img"); im.src = viewUrl(rf.id); im.alt = "reference"; chip.appendChild(im);
    } else {
      const g = document.createElement("span"); g.className = "ref-glyph"; g.textContent = rf.kind === "audio" ? "♪" : "▶"; g.title = rf.kind + " reference"; chip.appendChild(g);
    }
    const x = document.createElement("button"); x.type = "button"; x.textContent = "×"; x.title = "Remove reference"; x.addEventListener("click", () => { editRefs.splice(i, 1); renderEditRefs(); });
    chip.appendChild(x); $editRefs.appendChild(chip);
  });
  $editRefs.classList.toggle("hidden", editRefs.length === 0); updateEditRefBtn(); updateEditRefHint();
}
function editRefHint() { const m = editModel(); const r = m && m.edit && m.edit.reference; return (r && r.hint) || ""; }
function updateEditRefHint() { const txt = editRefHint(); $editRefHint.textContent = txt; $editRefHint.classList.toggle("hidden", editRefs.length === 0 || !txt); }
$editRefBtn.addEventListener("click", () => $editRefFile.click());
// References accept MULTIPLE files (picked or dropped), each routed by its media kind — the workflow declares which
// kinds it takes and how many of each; a file of an unaccepted kind, or one over its per-kind cap, is rejected here.
async function handleEditRefFiles(files) {
  for (const f of Array.from(files || [])) {
    if (editRefs.length >= editRefTotalMax()) break;
    const kind = fileKind(f);
    if (!kind || editRefMaxOf(kind) <= 0) { setStatus(`This model doesn't accept ${kind || "that"} references.`, { error: true }); continue; }
    if (editRefCountOf(kind) >= editRefMaxOf(kind)) { setStatus(`At most ${editRefMaxOf(kind)} ${kind} reference(s).`, { error: true }); continue; }
    setStatus("Uploading reference…");
    try { const id = await uploadToInput(f, f.name || `ref.${kind}`); editRefs.push({ id, kind }); renderEditRefs(); setStatus(""); }
    catch (err) { setStatus(friendlyError(err), { error: true }); }
  }
}
$editRefFile.addEventListener("change", e => { const files = Array.from(e.target.files || []); e.target.value = ""; handleEditRefFiles(files); });
// The refs strip is hidden when empty, so the ＋ ref button is the drop target that's always visible; the strip
// itself takes drops once it holds chips.
attachDropUpload($editRefBtn, handleEditRefFiles);
attachDropUpload($editRefs, handleEditRefFiles);

// --- last frame (i2v first/last-frame editors) --------------------------------------------------
// A single optional END frame, offered only when the primary editor accepts one (supportsLastFrame) — a single-model
// affordance like references (there's no primary with 2+ checked). The chip mirrors a ref chip; the button hides once
// one is picked (only one end frame). buildChatItems sends it as lastFrameImageId so the graph swaps to
// WanFirstLastFrameToVideo, interpolating from the source (first frame) to this one.
const editSupportsLastFrame = () => { const m = editModel(); return !!(m && m.supportsLastFrame); };
// Loop is live only when the primary editor accepts a last frame AND the box is checked — the same gate as the button.
// While active it hides the pick-a-distinct-last-frame affordances: the source stands in as the final frame instead.
const editLoopActive = () => editSupportsLastFrame() && !!($editLoop && $editLoop.checked);
// Show the Loop checkbox on exactly the editors that show the last-frame button (supportsLastFrame).
// Loop and ＋ last frame are mutually exclusive: Loop shows only for a first/last-frame editor AND while no last frame
// is picked (picking one sends that frame, not the source, so Loop is meaningless). Checking Loop hides the ＋ last
// frame button instead, so the two only ever appear together when neither is chosen. renderEditLastFrame re-runs this
// whenever a last frame is set/cleared.
function updateEditLoop() { if ($editLoopWrap) $editLoopWrap.classList.toggle("hidden", !editSupportsLastFrame() || !!lastFrameId); }
// The pick-a-last-frame button hides once one is picked, when the model doesn't support one, or while looping.
function updateEditLastFrameBtn() { $editLastFrameBtn.classList.toggle("hidden", !editSupportsLastFrame() || !!lastFrameId || editLoopActive()); }
function renderEditLastFrame() {
  $editLastFrame.innerHTML = "";
  // While looping, hide any picked end frame (the source stands in) WITHOUT dropping lastFrameId, so unchecking restores it.
  const showPick = editSupportsLastFrame() && !editLoopActive();
  if (lastFrameId && showPick) {
    const chip = document.createElement("div"); chip.className = "ref-chip";
    const im = document.createElement("img"); im.src = viewUrl(lastFrameId); im.alt = "last frame"; chip.appendChild(im);
    const x = document.createElement("button"); x.type = "button"; x.textContent = "×"; x.title = "Remove last frame";
    x.addEventListener("click", () => { lastFrameId = null; renderEditLastFrame(); });
    chip.appendChild(x); $editLastFrame.appendChild(chip);
  }
  $editLastFrame.classList.toggle("hidden", !(lastFrameId && showPick));
  updateEditLoop();
  updateEditLastFrameBtn();
}
// Checking/unchecking Loop only reshapes the last-frame UI and persists the pref; the source itself is sent on submit.
if ($editLoop) $editLoop.addEventListener("change", () => { renderEditLastFrame(); savePrefs(); });
$editLastFrameBtn.addEventListener("click", () => $editLastFrameFile.click());
// A single end frame (picked or dropped) — takes the first file.
async function handleEditLastFrameFiles(files) {
  const f = files && files[0];
  if (!f) return;
  if (!isImageFile(f)) { setStatus("Please choose an image file.", { error: true }); return; }
  setStatus("Uploading last frame…");
  try { lastFrameId = await uploadToInput(f, f.name || "last_frame.png"); renderEditLastFrame(); setStatus(""); }
  catch (err) { setStatus(friendlyError(err), { error: true }); }
}
$editLastFrameFile.addEventListener("change", e => { const files = Array.from(e.target.files || []); e.target.value = ""; handleEditLastFrameFiles(files); });
attachDropUpload($editLastFrameBtn, handleEditLastFrameFiles);
attachDropUpload($editLastFrame, handleEditLastFrameFiles);

// --- chat edit: fan the instruction across every selected model --------------------------------
// n comes from the Apply button's hold-to-reveal count picker (a plain click = 1), exactly like the gen page. It
// multiplies ON TOP of the model fan-out: models × n runs, so two checked models held to 4 makes eight edits — all
// submitted as ONE /enqueue job with N slots, which the queue renders one at a time.
function buildChatItems(n) {
  const instruction = $instruction.value.trim();
  const models = editModels();
  if (!models.length || !editCurrent) return [];
  // "single" is about the number of MODELS: reference images and the end frame have no primary with 2+ checked; the
  // shared param panel (params common to every selected model) applies to all of them.
  const single = models.length === 1;
  const refIds = single ? editRefs.map(r => r.id) : [];
  const overrides = readOverrides($("editParams"));
  const items = [];
  for (const m of models)
    for (let i = 0; i < n; i++) {
      // The end frame is a single-model affordance (no primary with 2+). Loop sends the source itself as the last frame.
      const lastFrame = (single && m.supportsLastFrame) ? (editLoopActive() ? editCurrent : lastFrameId) : null;
      // Re-roll [a|b|…] per slot so the model fan-out AND the copies can differ.
      items.push({ workflow: gwModel(m), edit: true, instruction: expandRandomPrompt(instruction), negativePrompt: editNegFor(m),
        imageId: editCurrent, referenceIds: refIds, lastFrameImageId: lastFrame, overrides });
    }
  return items;
}
// Chat/animate Apply uses the ONE shared submit control (core.js attachEnqueueSubmit): a click (or held count) builds
// the items and POSTs one /enqueue job; a press while busy queues another. buildItems does the mode's own validation.
attachEnqueueSubmit({
  button: $editSend, form: $editComposer, panel: editPanel(editModeSpec("chat")), ...editSubmitBase(),
  buildItems: n => {
    const models = editModels();
    if (!models.length) { setStatus("Pick at least one workflow.", { error: true }); return []; }
    if (!editCurrent) { setStatus("Select a file to edit first.", { error: true }); return []; }
    const items = buildChatItems(Math.max(1, n || 1));   // empty instruction is allowed — never blocked on a blank prompt
    // Keep the attached references after Apply so repeated animate/edit runs reuse them (like the gen page keeps its
    // composer). They're removed only by the user (the × on each chip) or when switching source/model.
    return items;
  },
});
$cancelEdit.addEventListener("click", () => cancelGeneration());
// The chat instruction box doubles as the full TAG prompt for whole-image redraws (Anima/Photanima): same '#'/'@'
// autocomplete, gated on the primary editor's tagging — inert for instruction/animate editors, which have none.
// Enter does NOT apply the edit; Apply is the only way to start one. The popup still consumes Enter to accept a
// highlighted tag while it is open, which is the only special meaning Enter has in this box.
if ($instruction && $instructionTagPop) initTagBox({ input: $instruction, pop: $instructionTagPop, getModel: editModel });

// --- inpaint mode -------------------------------------------------------------------------------
function populateInpaintMenu() {
  const models = inpaintModelList();
  if (!models.length) { $inpaintModelToggle.textContent = "No inpaint workflows installed"; $inpaintGo.disabled = true; return; }
  updateSubmitEnabled();   // enabled only when a source is also present
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
  $maskStage.appendChild(srcClearButton());
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
  canvas.addEventListener("pointerdown", e => { drawing = true; try { canvas.setPointerCapture(e.pointerId); } catch (err) { console.debug("pointer capture failed:", err); } stamp(e); });
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
// inside it to partially-denoise from. (Baking the mask into the source's alpha would black out the masked region,
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
// Build the inpaint items: n copies of the SAME base + mask + prompt, re-rolling [a|b|…] per slot so the takes differ
// (the server also fills a fresh seed per slot). The mask PNG is built once here and shared by every slot in the job.
async function buildInpaintItems(n) {
  const model = inpaintModel();
  if (!model || !inpaintBase) return [];
  const maskId = await buildMaskPng();   // throws if nothing is painted — the caller surfaces it
  const prompt = $inpaintPrompt.value.trim();
  const overrides = readOverrides($inpaintParams);
  const items = [];
  for (let i = 0; i < n; i++)
    items.push({ workflow: gwModel(model), edit: true, instruction: expandRandomPrompt(prompt), negativePrompt: inpaintNegFor(model),
      imageId: inpaintBase, maskImageId: maskId, referenceIds: [], overrides });
  return items;
}
function showInpaintBar(show) { $inpaintBar.classList.toggle("show", show); if (!show) $inpaintBar.querySelector("i").style.width = "0"; }
// Inpaint uses the ONE shared submit control: n images from the same base + mask + prompt as one /enqueue job. The
// mask is built once (in buildInpaintItems) and shared by every slot; a press while busy queues another take.
attachEnqueueSubmit({
  button: $inpaintGo, form: $inpaintComposer, panel: editPanel(editModeSpec("inpaint")), ...editSubmitBase(),
  buildItems: async n => {
    if (!inpaintModel()) { setStatus("Pick a workflow.", { error: true }); return []; }
    if (!inpaintBase) { setStatus("Select a file to inpaint first.", { error: true }); return []; }
    setStatus("Preparing mask…");
    return await buildInpaintItems(Math.max(1, n || 1));   // throws if unpainted → the control surfaces it
  },
});
inpaintTag = initTagBox({ input: $inpaintPrompt, pop: $inpaintTagPop, getModel: inpaintModel });
// The same booru '#'/'@' autocomplete on the negative boxes (chat + inpaint), gated on the active editor's tagging
// (so it's inert for non-tag editors — which don't show a negative box anyway). Uses the primary model per mode.
if ($editNeg && $editNegTagPop) initTagBox({ input: $editNeg, pop: $editNegTagPop, getModel: editModel });
if ($inpaintNeg && $inpaintNegTagPop) initTagBox({ input: $inpaintNeg, pop: $inpaintNegTagPop, getModel: inpaintModel });
$cancelInpaint.addEventListener("click", () => cancelGeneration());
let stagedBase = null;
function enterInpaint() {
  if (!$inpaintPrompt.value.trim() && seedPrompt()) $inpaintPrompt.value = seedPrompt();
  if ($inpaintNeg && !$inpaintNeg.value.trim() && seedNegative()) $inpaintNeg.value = seedNegative();
  populateInpaintMenu();
  if (stagedBase !== inpaintBase) { setupMaskStage(); stagedBase = inpaintBase; }   // re-stage only when the base changed
  if (liveRecover) liveRecover.tick();   // entering the tab with a job already running (came back) → adopt it now
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
  updateSubmitEnabled();   // enabled only when a source is also present
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
    try { h.setPointerCapture(e.pointerId); } catch (err) { console.debug("pointer capture failed:", err); }
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
  $outpaintStage.appendChild(srcClearButton());
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
// result — same as inpaint. (Navigating away would throw the whole stage out.)
function renderOutpaintResult(id) {
  $outpaintResult.innerHTML = "";
  const c = document.createElement("div"); c.className = "result-card";
  const im = document.createElement("img"); im.src = viewUrl(id); im.alt = "result"; im.style.cursor = "pointer";
  im.addEventListener("click", () => openImage(imageId(id))); c.appendChild(im);
  $outpaintResult.appendChild(c);
}
function showOutpaintBar(show) { $outpaintBar.classList.toggle("show", show); if (!show) $outpaintBar.querySelector("i").style.width = "0"; }
// Build the outpaint items: n takes of the SAME base + pads + prompt, re-rolling [a|b|…] per slot (the server also
// fills a fresh seed per slot). The pads are the ONLY override — everything else (fill strength, feather, mask grow,
// LLLite) stays at the configuration's defaults, exactly as a bare API call gets them.
//
// Do NOT reintroduce readOverrides() here. The editor's param map (editParamPrefs) is flat and keyed by param NAME
// across every panel, and `denoise` is "Change amount" (default 0.6, min 0.2) to anima-inpaint but "Fill strength"
// (default 1.0, min 0.5) to anima-outpaint. Feeding inpaint's denoise in would half-denoise the grey padding that
// ImagePadForOutpaint lays down, so the border would come back grey instead of painted.
function buildOutpaintItems(n) {
  const model = outpaintModel();
  if (!model || !outpaintBase || !padsTotal()) return [];
  const prompt = $outpaintPrompt.value.trim();
  const overrides = { pad_left: pads.left, pad_top: pads.top, pad_right: pads.right, pad_bottom: pads.bottom };
  const items = [];
  for (let i = 0; i < n; i++)
    items.push({ workflow: gwModel(model), edit: true, instruction: expandRandomPrompt(prompt), negativePrompt: outpaintNegFor(model),
      imageId: outpaintBase, referenceIds: [], overrides });
  return items;
}
// Outpaint uses the ONE shared submit control: n takes of the same base + pads + prompt as one /enqueue job. A finished
// slot becomes the new base (editModeSpec.onSlot) so you can keep pushing the frame out; a press while busy queues more.
attachEnqueueSubmit({
  button: $outpaintGo, form: $outpaintComposer, panel: editPanel(editModeSpec("outpaint")), ...editSubmitBase(),
  buildItems: n => {
    if (!outpaintModel()) { setStatus("Pick a workflow.", { error: true }); return []; }
    if (!outpaintBase) { setStatus("Select a file to outpaint first.", { error: true }); return []; }
    // Zero pads would pad by nothing and hand back the source — the outpaint equivalent of an unpainted mask.
    if (!padsTotal()) { setStatus("Drag an edge outward to extend the canvas first.", { error: true }); return []; }
    return buildOutpaintItems(Math.max(1, n || 1));
  },
});
initTagBox({ input: $outpaintPrompt, pop: $outpaintTagPop, getModel: outpaintModel });
if ($outpaintNeg && $outpaintNegTagPop) initTagBox({ input: $outpaintNeg, pop: $outpaintNegTagPop, getModel: outpaintModel });
$cancelOutpaint.addEventListener("click", () => cancelGeneration());
function enterOutpaint() {
  if (!$outpaintPrompt.value.trim() && seedPrompt()) $outpaintPrompt.value = seedPrompt();
  if ($outpaintNeg && !$outpaintNeg.value.trim() && seedNegative()) $outpaintNeg.value = seedNegative();
  populateOutpaintMenu();
  if (outStagedBase !== outpaintBase) { setupOutpaintStage(); outStagedBase = outpaintBase; }   // re-stage only when the base changed
  else outLayout();   // the stage had no size while hidden, so re-fit on every entry
  if (liveRecover) liveRecover.tick();   // switching INTO the tab re-adopts a job still running for this base
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
  // Expose the active chat bucket on the composer so CSS can scope per-mode tweaks (e.g. animate right-aligns its
  // side-pane controls). Inpaint/outpaint hide the chat composer entirely, so their value is irrelevant.
  if (chat) $chatMode.dataset.mode = mode;
  if (chat) { chatBucket = mode; populateChatMenu(); }   // chat modes: edit | redraw | upscale | effects | animate | video
  else if (mode === "inpaint") enterInpaint();
  else enterOutpaint();
  updateSubmitEnabled();   // reflect the newly-active mode's source/workflow presence on its submit
  refreshTabSelect();   // keep the mobile mirror's selected option on the active mode
}
// Whether a tab's group has ≥1 available (non-hidden) workflow. Chat buckets reuse chatHasModels; inpaint/outpaint
// have their own workflow sets. Drives both the source-media split and the empty-tab hiding below.
function tabHasModels(mode) {
  if (mode === "inpaint") return inpaintModelList().length > 0;
  if (mode === "outpaint") return outpaintModelList().length > 0;
  return chatHasModels(mode);   // edit | redraw | upscale | effects | animate | video buckets
}
// Reflect the source's media type in the tab bar: a clip source shows ONLY the "Pixelize" (V2V) tab; an image source
// shows the four image-editing tabs and hides the video one. On top of that, hide any tab whose group has no available
// workflows (e.g. no upscalers installed) so an empty tab never shows. Called on boot and whenever the source changes.
function applySourceMediaUi() {
  for (const t of $editTabs.querySelectorAll(".edit-tab")) {
    const mode = t.dataset.mode;
    const isVideoTab = mode === "video";
    const mediaHidden = srcIsVideo ? !isVideoTab : isVideoTab;
    t.hidden = mediaHidden || !tabHasModels(mode);
  }
  // If the active tab just became hidden (empty group / source split), move to a visible tab that has workflows.
  const active = $editTabs.querySelector(`.edit-tab[data-mode="${activeMode}"]`);
  if (active && active.hidden && !busy) {
    const next = Array.from($editTabs.querySelectorAll(".edit-tab")).find(t => !t.hidden);
    if (next && next.dataset.mode !== activeMode) setMode(next.dataset.mode);
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
// Switching is free while a job runs — the running job keeps its own bar/ETA/Cancel (runningSpecMode), so the user can
// set up another mode (e.g. an inpaint while a chat edit renders) without waiting for the job to finish.
$editTabs.addEventListener("click", e => { const t = e.target.closest(".edit-tab"); if (t) { setMode(t.dataset.mode); savePrefs(); } });
// The mobile select drives the same setMode — also free while busy.
$editTabsSelect.addEventListener("change", () => { setMode($editTabsSelect.value); savePrefs(); });

// --- recover an in-flight job on reload / return --------------------------------------------------
// Recovery is the shared attachLiveRecover (core.js) — the SAME path the composer uses. It adopts ANY of this user's
// active jobs and drives the VISIBLE mode's bar/ETA/Cancel through the same tracker a fresh Apply uses. The ONLY thing
// the editor does differently from the composer is the preview: the finished image is painted here only when the job
// is this mode's OWN source + workflow. A plain compose gen, or an edit on another image/mode, still lights the bar
// (so work in flight is always visible) but isn't previewed. When the tracked job finishes, the next tick picks up any
// job queued behind it (queue-more), draining the queue continuously.
const inpaintWorkflowIds = () => new Set(inpaintModelList().map(gwModel));
const outpaintWorkflowIds = () => new Set(outpaintModelList().map(gwModel));
// The adopted job's spec mode, derived from its OWN workflow (not the visible tab): inpaint/outpaint workflows route to
// their tab, everything else — including a plain compose gen with no editor workflow — is chat. Mode switching is free
// while a job runs, so the spec must be captured from the job at adopt time (runningSpecMode) and NOT re-read off the
// currently-visible mode, which the user may have since switched away from.
function specModeForJob(job) {
  if (inpaintWorkflowIds().has(job.model)) return "inpaint";
  if (outpaintWorkflowIds().has(job.model)) return "outpaint";
  return "chat";
}
const recoverSpec = () => editModeSpec(runningSpecMode || "chat");
let liveRecover = null;
function startEditRecover() {
  liveRecover = attachLiveRecover({
    isBusy: () => busy,
    onAdopt: job => { cancelRequested = false; runningSpecMode = specModeForJob(job); setBusy(true); recoverSpec().show(true); editActiveJobId = job.jobId; setStatus(job.total > 1 ? `Making ${job.total}…` : "Reconnecting…"); },
    options: job => {
      const spec = recoverSpec();
      const p = editPanel(spec);
      // Relevance is per-JOB (its source + workflow), decided once: this mode's own job renders its slots; anything
      // else is bar-only. A generate job has no sourceImageId, so it never matches an editor source — exactly right.
      const mine = job.sourceImageId === spec.sourceId() && spec.mine(job.model);
      return {
        eta: p.eta, onProgress: p.onProgress,
        onSlot: mine ? p.onSlot : undefined,   // the one divergence: paint only THIS surface's own finished image
        // The panel's activeStatus stays null for a lone image (a fresh submit's opening "Generating…" carries it); on
        // ADOPTION the opening line is "Reconnecting…", so a single-job poll MUST emit its own live status or the
        // "Reconnecting…" never clears until the job settles (#217). Multi-job text is identical to the panel's.
        activeStatus: (recorded, total) => total > 1 ? `Making ${Math.min(recorded + 1, total)} of ${total}…` : "Generating…",
        finalStatus: p.finalStatus, setStatus,
        onCancelHandle: h => { activeGen = h; },
        onSettle: made => { spec.show(false); setBusy(false); document.title = "Edit · Make a Picture"; editActiveJobId = null; if (mine && !made && spec.onNoneMade) spec.onNoneMade(); },
      };
    },
  });
}

// --- boot ---------------------------------------------------------------------------------------
(async () => {
  const settings = await loadEditModels();   // also seeds savedMode/savedBrushSize/editParamPrefs/selectedEditIds from the account blob
  setTagBoxPinBookmarks(settings.pinBookmarks);   // one account toggle governs every tag box on this page
  // Favorited/banned marks in the '#'/'@' popup: one-time snapshots, applied when they resolve. Detached so they
  // never gate boot, un-caught so a real endpoint failure surfaces rather than being swallowed.
  fetchBookmarks().then(setTagBoxFavorites);
  fetchAllBans().then(setTagBoxBans);
  editCurrent = seed.id;
  srcIsVideo = await detectSrcVideo(seed.id);   // a clip seed → collapse the editor to the single V2V mode
  if (savedLoop != null && $editLoop) $editLoop.checked = savedLoop;   // restore the Loop pref before the last-frame UI renders
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
  startEditRecover();   // attachLiveRecover owns its own poll interval + visibility re-check + initial adoption tick
})();
function chatHasModels(bucket) {
  const prev = chatBucket; chatBucket = bucket; const n = chatModels().length; chatBucket = prev; return n > 0;
}
