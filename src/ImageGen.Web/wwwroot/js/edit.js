// Edit page: four modes behind a tab bar, all over one source image (seeded from /edit/{id}).
//   • Edit    — instruction image-editing (Flux Kontext / Qwen), gen-style: source on the left, prompt box +
//               controls on the right, outputs underneath. Each "Apply" edits the SAME source image.
//   • Effects — deterministic image transforms (Line art / Pixelize), same gen-style layout; the dropdown is
//               grouped by effect type. These carry an effect_type in the catalog (Edit holds the rest).
//   • Animate — image→video editors (Wan / LTX / AnimateDiff), same gen-style layout.
//   • Outpaint— purpose-built: drag the frame outward, edit the FULL (tag) prompt, generate only the new margin.
// Masking is NOT a tab: a pencil on the source preview opens a paint modal (mask-editor.js). A drawn mask routes the
// selected Edit workflow to its masked sibling when it has one, else the plain edit runs and the server composites the
// painted region back. Pure-inpaint workflows live in the Edit picker and simply can't submit until a mask is drawn.
// Shares only helpers from core.js (+ tagbox.js for the prompt autocomplete). History is written
// server-side by the worker; the browser never writes it. To iterate on an OUTPUT, the user clicks it and
// chooses Edit, which re-seeds this page (/edit/{outputId}) with that output as the fixed source.

const $editTabs = $("editTabs"), $editTabsSelect = $("editTabsSelect"), $chatMode = $("chatMode"), $outpaintMode = $("outpaintMode"),
      $editModelSelect = $("editModelSelect"), $editModelToggle = $("editModelToggle"), $editModelMenu = $("editModelMenu"),
      $editSrc = $("editSrc"), $bar = $("bar"), $eta = $("eta"), $cancelEdit = $("cancelEdit"), $result = $("result"), $editComposer = $("editComposer"),
      $instruction = $("instruction"), $instructionTagPop = $("instructionTagPop"), $editSend = $("editSend"), $status = $("status"),
      $editRefs = $("editRefs"), $editRefBtn = $("editRefBtn"), $editRefFile = $("editRefFile"), $editRefHint = $("editRefHint"),
      $editLastFrame = $("editLastFrame"), $editLastFrameFile = $("editLastFrameFile"),
      $editFrameControls = $("editFrameControls"), $editLastFrameWrap = $("editLastFrameWrap"),
      $editLoopWrap = $("editLoopWrap"), $editLoop = $("editLoop"), $editSrcLabel = $("editSrcLabel"),
      $editAspectField = $("editAspectField"), $editAspect = $("editAspect"),
      $editRandomArtistBar = $("editRandomArtistBar"), $editRandomArtist = $("editRandomArtist"),
      $editSrcFile = $("editSrcFile"),
      // mask painting (a modal off the source preview; the brush slider lives inside the modal toolbar)
      $maskModal = $("maskModal"), $maskModalStage = $("maskModalStage"), $brushSize = $("brushSize"),
      // outpaint
      $outpaintModelSelect = $("outpaintModelSelect"), $outpaintModelToggle = $("outpaintModelToggle"), $outpaintModelMenu = $("outpaintModelMenu"),
      $outpaintComposer = $("outpaintComposer"), $outpaintPrompt = $("outpaintPrompt"), $outpaintTagPop = $("outpaintTagPop"), $outpaintParams = $("outpaintParams"),
      $outpaintGo = $("outpaintGo"), $outpaintResult = $("outpaintResult"),
      $outpaintBar = $("outpaintBar"), $outpaintEta = $("outpaintEta"), $cancelOutpaint = $("cancelOutpaint"),
      $outpaintStage = $("outpaintStage"), $outPads = $("outPads"), $outSize = $("outSize"), $outPresets = $("outPresets"),
      // optional negative prompt (chat + outpaint) — shown only when a selected editor's card declares support
      $editNegWrap = $("editNegWrap"), $editNeg = $("editNeg"), $editNegTagPop = $("editNegTagPop"),
      $outpaintNegWrap = $("outpaintNegWrap"), $outpaintNeg = $("outpaintNeg"), $outpaintNegTagPop = $("outpaintNegTagPop");

// The seed record names the image this page opens on — or names none, which is a legitimate starting state, not a
// failure: the rail's Edit button (GET /edit) exists precisely to open the editor with NO source and pick a file,
// and every mode already renders a picker when its base is empty (renderSrc, setupOutpaintStage).
//
// Most modes require a source to APPLY an edit; reference-only workflows can instead use an attached image and an
// empty target latent. The check therefore lives at each mode's submit control (and the button is disabled anyway).
// Asserting a source here would throw at page init, aborting the whole script and taking the file picker with it:
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
const CHAT_BUCKETS = ["edit", "redraw", "upscale", "effects", "animate", "video"];
// The one picker is rebuilt for several tabs, but each workflow belongs to exactly one of those tabs. This mapping is
// also used to migrate the former single modelIds field into the right per-tab slot.
function chatBucketOf(m) {
  if (!m || isOutpaint(m)) return null;
  if (isV2V(m)) return "video";
  if (m.kind === "animate") return "animate";
  if (isRedraw(m)) return "redraw";
  if (isUpscale(m)) return "upscale";
  if (m.kind === "effect") return "effects";
  return (m.kind === "edit" || m.kind === "inpaint") ? "edit" : null;
}
// Whether the current source (editCurrent) is a video clip. Decided from /forge/media for a seeded/edited source, and
// from the file type for an upload. When true, the editor collapses to the single V2V "Pixelize" mode.
let srcIsVideo = false;
// Ask the server whether an id is a clip or a still. "Unknown" is NOT a still: a failed/malformed lookup cannot safely
// choose editors, mask controls, or an upload route, so it throws and boot leaves the source blocked with a visible
// error instead of quietly sending a clip to an image workflow.
async function detectSrcMedia(id) {
  if (!id) return "image";   // the legitimate source-less editor; no existing media needs inspection
  const key = imageId(id);
  try {
    const r = await fetch(`${GATEWAY}/media`, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids: [key] }),
    });
    if (!r.ok) throw new Error(`the server answered ${r.status}`);
    const map = await r.json();
    const kind = map && map[key];
    if (kind === "webp" || kind === "mp4") return "video";
    if (kind === "image") return "image";
    throw new Error("the server returned no recognised media kind");
  } catch (e) {
    throw new Error(`Couldn't inspect the source media: ${e.message}`, { cause: e });
  }
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
// The editor restores its WHOLE last state from the account (never localStorage): the active tab, each tab's selected
// workflow(s), the outpaint workflow, the brush size, and a FLAT by-name param-override map. The param map is NOT
// keyed per workflow — a value you set for one editor prefills the same-named field on the next — mirroring the gen
// page, so switching workflows never wipes your knobs. Writes are debounced and go to the account via saveEditPrefs;
// the blob is restored on boot in loadEditModels. The instruction/prompt text is intentionally NOT retained: it's
// tied to the specific source image being edited, so carrying a prior image's instruction to a new source is wrong.
let editParamPrefs = {};            // flat { paramKey: value }, shared across every param panel (edit + outpaint)
let savedMode = null, savedOutpaintWorkflowId = null, savedBrushSize = null, savedLoop = null, savedReferenceAspect = null, savedRandomArtist = null;   // seeded from the account blob on boot
let referenceAspect = "reference";

let prefsTimer = null;
// Retain the latest complete snapshot until its PUT succeeds. pagehide flushes it with fetch keepalive, covering a
// refresh during the debounce window (the workflow picker and exposed-parameter controls both feed this path).
let pendingPrefsJson = null;
// False until the stored blob has actually been read. savePrefs writes the WHOLE editor state, so writing before we
// know what was stored replaces the user's saved editor with this page's defaults — see loadEditModels.
let editPrefsLoaded = false;
// One blob captures the full editor state (read live from the UI), like the composer's savePrefs.
function flushPrefs(keepalive = false) {
  clearTimeout(prefsTimer); prefsTimer = null;
  const json = pendingPrefsJson;
  if (!json) return Promise.resolve();
  return saveEditPrefs(json, keepalive).then(r => {
    if (!r.ok) throw new Error(`PUT editor prefs -> ${r.status}`);
    if (pendingPrefsJson === json) pendingPrefsJson = null;
  }).catch(e => {
    console.error("Editor settings could not be saved:", e);
    if (!keepalive) toast("Couldn't save your editor settings");
  });
}
function savePrefs(immediate = false) {
  if (!editPrefsLoaded) return;   // never overwrite settings we failed to read
  // The shared picker currently displays chatBucket. Snapshot it before serializing so a tab switch cannot save the
  // new tab while leaving the old tab's most recent click behind.
  selectedEditIdsByMode[chatBucket] = editSelIds();
  pendingPrefsJson = JSON.stringify({
    mode: activeMode,
    modelIdsByMode: selectedEditIdsByMode,
    modelIds: editSelIds(),   // legacy fallback for an older client reading this account blob
    outpaintWorkflowId: selectedOutpaintId,
    params: editParamPrefs,
    brushSize: $brushSize ? $brushSize.value : null,
    loop: $editLoop ? $editLoop.checked : false,   // per-user, cross-device like the rest of the editor state
    randomArtist: !!($editRandomArtist && $editRandomArtist.checked),
    referenceAspect,
    // The pad amounts are NOT retained: like the instruction text they're tied to the specific source image, so
    // carrying a prior image's margins onto a new source would silently extend it by the wrong number of pixels.
  });
  clearTimeout(prefsTimer);
  // A silently-swallowed save would leave the editor looking exactly like one whose settings were being kept, and the
  // next page load would quietly come back with older state. Say it once, where the user is looking.
  if (immediate) flushPrefs();
  else prefsTimer = setTimeout(() => flushPrefs(), 400);
}
addEventListener("pagehide", () => { if (pendingPrefsJson) flushPrefs(true); });
// Apply the shared flat param map onto the just-rendered fields in `box` (every panel reads the one map).
function restoreParams(box) { applyParamPrefs(box, editParamPrefs); }
// Merge the current field values in `box` into the shared flat map, then persist. Merge (not replace) so values for
// keys that only appear on other panels/workflows survive.
function persistParams(box) { collectParamPrefs(box, editParamPrefs); savePrefs(true); }

let activeMode = "edit", chatBucket = "edit";          // chatBucket ∈ {edit, redraw, upscale, effects (image), animate, video}
// Chat (Edit/Refine/Upscale/Effects/Animate/Video) is a MULTI-select picker (the shared createModelPicker) mirroring the gen page:
// any number of models in the bucket can be checked, and Apply fans the SAME instruction across all of them to compare.
// The picker DOM is shared, but selectedEditIdsByMode persists an independent picked set for every bucket.
let selectedEditIdsByMode = {}, selectedOutpaintId = null, editPicker = null;
const editSelIds = () => editPicker ? editPicker.getSelectedIds() : [];
const editModels = () => editPicker ? editPicker.getSelected() : [];
// "Primary" = the model when EXACTLY one is checked; it alone drives the per-model params/refs/placeholder. With
// 2+ checked there is no primary (null), so those single-model affordances hide and each model runs on its defaults.
const editModel = () => editPicker ? editPicker.getPrimary() : null;
const outpaintModel = () => EDIT_MODELS[selectedOutpaintId] || null;
// A painted mask is live. The effective descriptor (below) and the submit gate both key off this.
function maskActive() { return !!(maskEditor && maskEditor.hasMask()); }
// The descriptor that ACTUALLY runs for the chat/edit submit: when a mask is drawn and the primary editor names a
// masked sibling, the sibling (its exposed params, refs, negative, prompt semantics) drives the panel and the submit;
// otherwise the primary itself. A pure-inpaint editor selected directly is already its own effective descriptor.
function effectiveEditModel() {
  const m = editModel();
  if (m && maskActive() && m.maskWorkflow && EDIT_MODELS[m.maskWorkflow]) return EDIT_MODELS[m.maskWorkflow];
  if (m && editRefs.length > 0 && m.referenceWorkflow && EDIT_MODELS[m.referenceWorkflow]) return EDIT_MODELS[m.referenceWorkflow];
  return m;
}
// The models the shared param panel renders over. While a mask exists the picker is single-select, so this is the one
// effective descriptor; otherwise it's every checked model (the panel shows their common params).
function effectiveEditModels() { const base = editModel(), em = effectiveEditModel(); return (em && em !== base) ? [em] : editModels(); }
// A pure-inpaint editor cannot run without a mask — block its submit (the pencil shows an accent ring; see
// updateMaskControls). Checked across EVERY selected model, not just the primary: with 2+ checked there is no primary,
// and a mask can never attach to a multi-select anyway (it collapses the selection to one), so a fan-out that includes
// an inpaint editor is unsubmittable until the selection is narrowed and a mask drawn.
function editSubmitBlockedByMask() { return editModels().some(m => m && isInpaint(m)) && !maskActive(); }

// The catalog owns both names. shortName is optional compact picker copy; friendly_name is the full display fallback.
const cleanName = m => m.shortName || m.friendly_name;
let editFavs = new Set(), editHidden = new Set(), editTags = {}, editRemoved = {};

// editCurrent is the FIXED source image (the seed). It never advances on its own — every Apply edits this
// same image, so the source on the left stays put. Building on an output is an explicit click-to-edit reload.
let editCurrent = seed.id, editRefs = [];
// Optional END frame for i2v first/last-frame editors (a single uploaded image id, or null). Tied to the current
// source like the instruction text: cleared on a source swap and on manual removal, never persisted to the account.
let lastFrameId = null;
let busy = false, activeGen = null, cancelRequested = false;
// The paint-mask controller (mask-editor.js), created at boot. `maskId` is the LAZILY-uploaded white-on-black mask PNG
// id: null until built at submit, and reset to null on every stroke/clear/source-change so it is re-uploaded fresh.
let maskEditor = null, maskId = null;

function setStatus(t, { error = false } = {}) { $status.classList.toggle("error", error); $status.textContent = t; }
// The Apply/Generate button STAYS itself while a render runs — clicking it again queues another job (the shared submit
// control's queue-more), so there is no cancel-adjacent gesture to misfire. The only Cancel is the per-mode button,
// shown only while busy. Mode switching stays free while busy, so the visible mode is NOT the job's mode: the Cancel
// follows the JOB (runningSpecMode, captured at submit/adopt), so it stays on the mode that owns the running job even
// after the user switches tabs to set up another edit. The other two are always cleared so no stale Cancel lingers.
let runningSpecMode = null;   // "chat" | "outpaint" of the in-flight job; null when idle
function setBusy(b) {
  busy = b;
  if (!b) runningSpecMode = null;
  $cancelEdit.classList.toggle("show", b && runningSpecMode === "chat");
  $cancelOutpaint.classList.toggle("show", b && runningSpecMode === "outpaint");
  // The running job's own panel keeps a slim progress-bar+Cancel surface even after the user switches tabs away from it
  // (CSS: a `.running.hidden` mode div reveals only its .bar-row/.eta). Without this the Cancel would sit inside the
  // now-hidden mode div and be unreachable the moment mode switching is used mid-render.
  $chatMode.classList.toggle("running", b && runningSpecMode === "chat");
  $outpaintMode.classList.toggle("running", b && runningSpecMode === "outpaint");
}
function cancelGeneration() { if (!busy || !activeGen) return; cancelRequested = true; setStatus("Cancelling…"); activeGen.cancel(); }

// Each mode's submit is enabled only when it has ≥1 available workflow and either a source or a valid reference-only
// input. Called whenever the source/reference/workflow set changes and on mode switch. A running batch keeps its
// inputs, so the button stays enabled while busy (a click then queues more).
function updateSubmitEnabled() {
  const models = editModels();
  // References are a single-workflow affordance (multi-select has no unambiguous capacity/slot contract).
  const referenceOnlyReady = !editCurrent && models.length === 1
    && models.every(m => m && m.supportsReferenceOnly)
    && editRefs.some(r => r.kind === "image");
  if ($editSend) $editSend.disabled = (!editCurrent && !referenceOnlyReady) || models.length === 0 || editSubmitBlockedByMask();
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
  if (mode === "outpaint") {
    return { bar: $outpaintBar, eta: $outpaintEta, result: $outpaintResult, show: showOutpaintBar,
      onSlot: s => { outpaintBase = s.id; renderOutpaintResult(s.id); setupOutpaintStage(); outStagedBase = outpaintBase; },
      onNoneMade: () => renderOutpaintResult(outpaintBase),
      sourceId: () => outpaintBase, mine: id => outpaintWorkflowIds().has(id) };
  }
  return { bar: $bar, eta: $eta, result: $result, show: showProgressBar,   // chat = edit + animate
    // The slot's own media/hasAudio (server-stated) back up the EDIT_MODELS lookup: an adopted job's model can be
    // absent from this page's map, and a miss must not render a clip as a still <img>.
    onSlot: s => showEditResult(s.id, "", EDIT_MODELS[s.model] || { media: s.media, hasAudio: s.hasAudio }, s.notice), onNoneMade: () => { $result.innerHTML = ""; },
    // chat now owns inpaint workflows too (masking is a per-source action, not a tab) — only outpaint is a separate job.
    sourceId: () => editCurrent, mine: id => !outpaintWorkflowIds().has(id) };
}

// The progress/preview wiring for one edit mode — the object the shared submit control (core.js attachEnqueueSubmit)
// and trackJobBatch consume. Identical in shape to the composer's; only the per-mode bar/eta/result rendering differs
// (editModeSpec). This is why every page's status·ETA·bar·cancel·preview behaves the same and can't drift.
function editPanel(spec) {
  return {
    eta: spec.eta,
    previewTarget: spec.result,
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
// The bits every edit mode's submit control shares: the page's busy flag, cancel handle, and status.
// buildItems + the mode's panel are the only per-mode parts, supplied where each control is attached.
function editSubmitBase(specMode) {
  return {
    isBusy: () => busy,
    // Each submit control belongs to exactly ONE mode, fixed at attach time — NOT read off activeMode when busy flips
    // on, because buildItems can be async (the inpaint mask upload) and the user may switch tabs during that await.
    onBusy: b => { if (b) { cancelRequested = false; runningSpecMode = specMode; } setBusy(b); },
    onActiveGen: h => { activeGen = h; },
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
    editFavs = prefs.favs; editHidden = prefs.hidden; editTags = prefs.tags; editRemoved = prefs.removed;
    const s = prefs.settings;
    rows.filter(r => r.canEdit).forEach(r => {
      EDIT_MODELS[r.id] = {
        id: r.id, friendly_name: r.friendlyName || r.id, shortName: r.shortName || null, _gw: r.id, workflow: r.workflow,
        kind: r.kind,   // the catalog's resolved kind — every tab routes off this (issue #163)
        exposedParams: r.exposedParams || [], avgSeconds: r.avgSeconds,
        hiddenParams: r.hiddenParams || [],   // shipped hidden-but-revealable params — shown only where the user's visibility prefs reveal them (#191)
        media: r.media === "video" ? "video" : "image", promptDirectsMotion: r.promptDirectsMotion !== false,
        sourceMedia: r.sourceMedia === "video" ? "video" : "image",
        supportsLastFrame: !!r.supportsLastFrame,   // i2v first/last-frame: offer an optional final frame to interpolate to
        supportsReferenceOnly: !!r.supportsReferenceOnly,
        supportsReferenceAspectWithSource: !!r.supportsReferenceAspectWithSource,
        hasAudio: !!r.hasAudio,   // clip carries a native audio track (H3) — offer an unmute control on the result

        baseTags: (r.card && r.card.tags) || [],   // definition tags (incl. the derived "Ref"); merged with the user delta for the picker chips
        effectType: r.effectType || null,   // sub-section header within a tab (grouping only — the TAB comes from kind)
        editGroup: r.editGroup || null,      // sub-section header within the Edit tab (grouping only, not tab routing)
        promptSemantics: r.promptSemantics || "instruction",   // instruction | whole_image | masked_region
        takesPrompt: r.takesPrompt !== false,   // false = no text encoder in the graph (upscalers): hide the box
        negativeSupported: !!(r.card && r.card.negativeSupported),   // editor uses a negative prompt (append-on-top)
        tagging: (r.card && r.card.tagging) || null,
        promptGuidance: (r.card && r.card.promptGuidance) || null,   // card's how-to-prompt line → instruction placeholder
        maskWorkflow: r.maskWorkflow || "",   // the masked sibling this Edit config routes to when a mask is drawn ("" = none)
        referenceWorkflow: r.referenceWorkflow || "",   // first/last-frame sibling selected while refs are attached
        hiddenFromPicker: !!r.hiddenFromPicker,   // a link TARGET: kept in the map for the panel swap + routing, never shown in the picker
        edit: { reference: r.reference || null, default: !!r.default }
      };
    });
    // Restore the editor's last state from the account blob (mode, per-tab workflows, flat params, outpaint, brush).
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
          if (typeof p.outpaintWorkflowId === "string") savedOutpaintWorkflowId = p.outpaintWorkflowId;
          if (p.brushSize != null) savedBrushSize = p.brushSize;
          if (typeof p.loop === "boolean") savedLoop = p.loop;
          if (typeof p.randomArtist === "boolean") savedRandomArtist = p.randomArtist;
          if (["reference", "square", "landscape", "portrait"].includes(p.referenceAspect)) savedReferenceAspect = p.referenceAspect;
          // Current format: an independent multi-selection for every picker tab. Reject ids that are installed but
          // belong to a different tab, so catalog reclassification cannot cross-contaminate the saved buckets.
          const mappedBuckets = new Set();
          if (p.modelIdsByMode && typeof p.modelIdsByMode === "object") {
            for (const bucket of CHAT_BUCKETS) {
              if (!Array.isArray(p.modelIdsByMode[bucket])) continue;
              mappedBuckets.add(bucket);
              const ids = p.modelIdsByMode[bucket].filter(id => EDIT_MODELS[id] && chatBucketOf(EDIT_MODELS[id]) === bucket);
              if (ids.length) selectedEditIdsByMode[bucket] = ids;
            }
          }
          // Legacy single selection: place every id into the tab its current catalog descriptor owns. This preserves
          // the last-used model from pre-map blobs without pretending it was the selection for every other tab.
          if (Array.isArray(p.modelIds)) {
            for (const id of p.modelIds) {
              const bucket = chatBucketOf(EDIT_MODELS[id]);
              if (bucket && !mappedBuckets.has(bucket)) (selectedEditIdsByMode[bucket] ||= []).push(id);
            }
          }
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
// edit   = instruction editors AND pure-inpaint editors (masking is a per-source action now, not a tab); effects =
// image with an effect_type (Line art / Pixelize, grouped by type); animate = video editors. Outpaint keeps its tab.
const chatModels = () => visibleOf(Object.values(EDIT_MODELS).filter(m =>
  !m.hiddenFromPicker && chatBucketOf(m) === chatBucket));
const outpaintModelList = () => visibleOf(Object.values(EDIT_MODELS).filter(isOutpaint));
const sortModels = ms => ms.slice().sort((a, b) => {
  const af = editFavs.has(a.id) ? 0 : 1, bf = editFavs.has(b.id) ? 0 : 1;
  // sensitivity:'base' — order by name case-insensitively, so casing never decides the position.
  return af !== bf ? af - bf : cleanName(a).localeCompare(cleanName(b), undefined, { sensitivity: "base" });
});

// Styled single-select popover (mirrors the gen page): ★ favorites first, render time, tag chips. `groupBy(m)->label`
// is optional: when given AND it yields more than one distinct label, the options render under `model-group` section
// headers (like the shared multi-select picker); otherwise the list is flat.
// The chips shown for a picker row: the workflow's base tags (including the derived Ref/Inpaint) MERGED with those of
// its masked inpaint sibling — the sibling is hidden from the picker, so a mask-capable Edit config (e.g. Qwen Image
// Edit) reads as inpaint-capable here — then overlaid with the user's per-workflow tag delta. computeWorkflowTags
// dedupes, so an overlap between the two definitions collapses to one chip.
function pickerTags(m) {
  const siblings = [m.maskWorkflow, m.referenceWorkflow].map(id => id && EDIT_MODELS[id]).filter(Boolean);
  const base = siblings.reduce((tags, sibling) => tags.concat(sibling.baseTags), m.baseTags.slice());
  return computeWorkflowTags(base, editTags[m.id], editRemoved[m.id]);
}

function buildMenu(menuEl, models, selectedId, groupBy) {
  menuEl.innerHTML = "";
  const sorted = sortModels(models);
  const optEl = m => {
    const opt = document.createElement("div"); opt.className = "model-opt" + (m.id === selectedId ? " selected" : ""); opt.dataset.id = m.id; opt.setAttribute("role", "option");
    const text = document.createElement("div"); text.className = "model-opt-text";
    const nameRow = document.createElement("div"); nameRow.className = "model-opt-namerow";
    const nm = document.createElement("span"); nm.className = "model-opt-nm"; nm.textContent = (editFavs.has(m.id) ? "★ " : "") + cleanName(m); nameRow.appendChild(nm);
    if (m.avgSeconds) { const tm = document.createElement("span"); tm.className = "model-opt-time"; tm.textContent = fmtDuration(m.avgSeconds); nameRow.appendChild(tm); }
    text.appendChild(nameRow);
    const tg = pickerTags(m);
    if (tg.length) { const sub = document.createElement("div"); sub.className = "model-opt-tags"; for (const t of tg) { const chip = document.createElement("span"); chip.className = "model-opt-tag"; chip.textContent = t; sub.appendChild(chip); } text.appendChild(sub); }
    opt.appendChild(text); return opt;
  };
  // Group by label when asked and there's more than one section — a lone header is just noise. Labels sort
  // alphabetically (the Reference/Non-Reference split is gone — that distinction is the "Ref" tag now).
  const labels = groupBy ? [...new Set(sorted.map(m => groupBy(m) || ""))] : [];
  if (groupBy && labels.length > 1) {
    for (const label of labels.sort((a, b) => a.localeCompare(b))) {
      const head = document.createElement("div"); head.className = "model-group"; head.textContent = label; menuEl.appendChild(head);
      for (const m of sorted.filter(m => (groupBy(m) || "") === label)) menuEl.appendChild(optEl(m));
    }
  } else {
    for (const m of sorted) menuEl.appendChild(optEl(m));
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
// onChange runs after any selection change (keeps this bucket's selected ids current + refreshes the
// single-model affordances); onCommit persists the primary id on user changes. editModel()/editModels() above
// read the picker's live state: refs/placeholder are primary-only (hide when 2+ checked), but the param panel
// shows the params common to ALL checked models (their intersection) and applies them to every one.
editPicker = createModelPicker({
  select: $editModelSelect, toggle: $editModelToggle, menu: $editModelMenu,
  nameOf: cleanName,
  favOf: m => editFavs.has(m.id),
  timeOf: m => m.avgSeconds,
  tagsOf: m => pickerTags(m),
  // Effects bucket → grouped by effect type. Redraw and Upscale are each ONE edit_group promoted to its own top-level
  // tab, so inside those tabs the group renders flat — a lone "Redraw"/"Upscale" header would just repeat the tab name.
  // Edit → ONE flat list: the Reference / Non-Reference split is gone, replaced by the "Ref" tag chip on the workflows
  // that take a reference. Animate keeps its own edit_group section (e.g. Pixel Art). The buckets never collide: a
  // config with an effectType only ever appears in the Effects bucket.
  groupBy: m => {
    if (m.effectType) return m.effectType;                         // Effects → group by effect type
    if (chatBucket === "animate") return m.editGroup || null;      // Animate → its edit_group sections
    if (chatBucket === "edit" || chatBucket === "redraw" || chatBucket === "upscale") return null;   // flat
    return m.editGroup || null;                                    // video (v2v) etc. keep their edit_group sections
  },
  hint: "Long-press a workflow to pick several and compare",
  onChange: ids => { selectedEditIdsByMode[chatBucket] = ids; refreshEditRouting(); },
  onCommit: () => savePrefs(true),   // committed workflow pick persists immediately
});
function populateChatMenu() {
  const models = chatModels();
  if (!models.length) { $editModelToggle.textContent = chatBucket === "video" ? "No video pixelizer installed" : chatBucket === "animate" ? "No video editors installed" : chatBucket === "effects" ? "No effects installed" : chatBucket === "redraw" ? "No redraw models installed" : chatBucket === "upscale" ? "No upscalers installed" : "No image editors installed"; return; }
  editPicker.rebuild(models);
  // Restore THIS bucket's prior pick; a different tab's selection must never enter this decision.
  let ids = (selectedEditIdsByMode[chatBucket] || []).filter(id => models.some(m => m.id === id));
  if (!ids.length) ids = [(models.find(m => m.edit && m.edit.default) || models[0]).id];
  editPicker.setSelectedIds(ids);
  // Re-sync the affordances that depend on the selection, in case setSelectedIds didn't fire onChange: the negative
  // box's visibility and the Change/Prompt wording would otherwise stay on the previous bucket's model.
  updateEditNeg();
  updateInstructionPlaceholder();
  renderEditLastFrame();
  updateReferenceAspectPicker();
}

function updateEditParams() {
  renderParamFields($("editParams"), effectiveEditModels());   // the sibling's params (mask_grow/blur) when masked
  restoreParams($("editParams"));   // prefill from the shared flat param map (carries across workflow switches)
}
// Random artist is a single-workflow refine affordance. The catalog's tagging capability is authoritative, so Anima
// gets the control without coupling the UI to its workflow id; multi-select hides it because each slot must opt in.
function supportsEditRandomArtist(m) { return !!(m && m.tagging && m.tagging.artists); }
function wantsEditRandomArtist(m) {
  return chatBucket === "redraw" && supportsEditRandomArtist(m) && !!($editRandomArtist && $editRandomArtist.checked);
}
function updateEditRandomArtist() {
  if ($editRandomArtistBar) $editRandomArtistBar.hidden = !(chatBucket === "redraw" && supportsEditRandomArtist(effectiveEditModel()));
}
const takesReferences = m => !!(m && m.edit && m.edit.reference);
const supportsReferenceOnly = m => !!(takesReferences(m) && m.supportsReferenceOnly);
const supportsChosenReferenceAspect = m => !!(takesReferences(m)
  && (!editCurrent ? m.supportsReferenceOnly : m.supportsReferenceAspectWithSource));
function setReferenceAspect(value, persist = true) {
  if (!["reference", "square", "landscape", "portrait"].includes(value)) value = "reference";
  referenceAspect = value;
  if ($editAspect) for (const b of $editAspect.querySelectorAll("button[data-aspect]")) b.classList.toggle("active", b.dataset.aspect === value);
  if (persist) savePrefs();
}
function updateReferenceAspectPicker() {
  if (!$editAspectField) return;
  const models = effectiveEditModels();
  // Most real image1 inputs own the edit canvas, while a source-free reference workflow synthesizes its target. A
  // reference-to-video workflow may explicitly declare that its real image1 is conditioning-only, not a first frame;
  // it keeps the same shape choice. Masks always own real source coordinates and therefore force Reference.
  $editAspectField.hidden = maskActive() || !models.length || !models.every(supportsChosenReferenceAspect);
}
if ($editAspect) $editAspect.addEventListener("click", e => {
  const b = e.target.closest("button[data-aspect]");
  if (b) setReferenceAspect(b.dataset.aspect);
});
// Re-run everything that depends on the EFFECTIVE descriptor and the mask state: the param/refs/negative/prompt panel,
// the pencil/clear overlay, the submit gate. Also collapses the multi-select to a single pick while a mask exists, so
// the panel reflects exactly one effective descriptor (its params/refs/negative can't be an intersection of several).
function refreshEditRouting() {
  if (maskActive() && editSelIds().length > 1) {
    editPicker.setSelectedIds([editSelIds()[0]]);   // keep the first, drop the rest — fires onChange → re-enters here single
    return;
  }
  updateEditRefBtn(); updateEditRefHint(); renderEditRefs();
  renderEditLastFrame();
  updateEditParams();
  updateEditRandomArtist();
  updateInstructionPlaceholder();
  updateEditNeg();
  updateReferenceAspectPicker();
  updateMaskControls();
  if (!editCurrent) renderSrc();   // workflow selection changes whether image 1 is labelled optional
  updateSubmitEnabled();
}
// The pencil (open the paint modal) and, when a mask exists, the clear-mask button — overlaid on the source preview.
// Rebuilt in place so a stroke/clear (maskChanged) or a selection change reshapes them without re-rendering the media.
function updateMaskControls() {
  const overlay = $("srcOverlay"); if (!overlay) return;
  overlay.innerHTML = "";
  if (srcIsVideo || !editCurrent) return;
  // Masking only makes sense where you confine an edit to a region. Upscale and Animate consume the whole frame, so
  // they get no pencil (Outpaint has its own pane and never reaches here).
  if (!["edit", "redraw", "effects"].includes(activeMode)) return;
  const pencil = document.createElement("button");
  pencil.type = "button"; pencil.className = "src-overlay-btn"; pencil.title = "Edit mask"; pencil.textContent = "✎";
  pencil.classList.toggle("needs-mask", editSubmitBlockedByMask());   // accent ring: a pure-inpaint editor with no mask
  pencil.addEventListener("click", openMaskModal);
  overlay.appendChild(pencil);
  if (maskActive()) {
    const er = document.createElement("button");
    er.type = "button"; er.className = "src-overlay-btn"; er.title = "Clear mask"; er.textContent = "⌫";
    er.addEventListener("click", clearMaskAll);
    overlay.appendChild(er);
  }
}
function openMaskModal() { if (editCurrent && !srcIsVideo && maskEditor) maskEditor.open(viewUrl(editCurrent), 0, 0); }
function clearMaskAll() { if (maskEditor) maskEditor.clear(); }   // maskEditor.clear fires onChange (maskChanged) → refresh
// A stroke or a clear inside the modal: the built mask id is now stale, so drop it (rebuilt at submit), and re-route.
function maskChanged() { maskId = null; refreshEditRouting(); }
// Persist tuned values the moment they change.
$("editParams").addEventListener("change", () => persistParams($("editParams")));
if ($editRandomArtist) $editRandomArtist.addEventListener("change", () => savePrefs(true));
// Honest wording for whatever the primary model actually consumes. An instruction editor is told to name a CHANGE; a
// redraw re-renders the whole frame from the prompt, so it is asked for the picture itself (saying "describe a change"
// there is simply wrong); a video editor is asked about motion per promptDirectsMotion. Outpaint is not reachable from
// here — it has its own tab, because its pad_* amounts need a frame editor no dropdown can provide.
// An editor with no text encoder (the upscalers) gets no instruction box at all: the field is hidden whenever NOT ONE
// selected editor consumes a prompt. Mixed selections keep it — the models that read it still would. Submitting with
// an empty instruction is already legal (buildChatItems never blocks on a blank prompt), so hiding the box changes nothing.
function updateInstructionVisibility() {
  const field = $("instructionField");
  if (field) field.hidden = !effectiveEditModels().some(m => m && m.takesPrompt);
}

function updateInstructionPlaceholder() {
  updateInstructionVisibility();
  const m = effectiveEditModel();
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
function updateEditNeg() { if ($editNegWrap) $editNegWrap.hidden = !effectiveEditModels().some(m => m && m.negativeSupported); }
function editNegFor(model) { const t = $editNeg ? $editNeg.value.trim() : ""; return (model && model.negativeSupported && t) ? t : null; }
function updateOutpaintNeg() { const m = outpaintModel(); if ($outpaintNegWrap) $outpaintNegWrap.hidden = !(m && m.negativeSupported); }
function outpaintNegFor(model) { const t = $outpaintNeg ? $outpaintNeg.value.trim() : ""; return (model && model.negativeSupported && t) ? t : null; }

// --- source pane + result (gen-style) -----------------------------------------------------------
// Left pane shows the FIXED source image being edited. Clicking it opens the lightbox/detail.
function renderSrc() {
  $editSrc.innerHTML = "";
  const firstLast = editSupportsLastFrame();
  if ($editSrcLabel) $editSrcLabel.textContent = firstLast ? "First frame" : "Editing";
  if (!editCurrent) {
    const optional = editModels().length === 1 && editModels().every(supportsReferenceOnly);
    $editSrc.appendChild(selectFileButton(firstLast ? "Select a first frame" : optional ? "Add image 1 (optional)" : "Select a file to edit"));
    return;
  }
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
  // A still source can be masked: a tinted preview canvas over the thumbnail + the pencil/clear overlay controls.
  if (!srcIsVideo) {
    const preview = document.createElement("canvas"); preview.className = "mask-preview";
    $editSrc.appendChild(preview);
    if (maskEditor) maskEditor.drawPreview(preview);
    const overlay = document.createElement("div"); overlay.className = "src-overlay"; overlay.id = "srcOverlay";
    $editSrc.appendChild(overlay);
  }
  $editSrc.appendChild(srcClearButton());
  updateMaskControls();
}
// A small "×" overlay on a source preview that clears the source. Upload sets ONE source for chat + outpaint together,
// so clearing drops both — consistent with how they're set — returning every stage to its empty "Select a file" picker.
function srcClearButton() {
  const x = document.createElement("button");
  x.type = "button"; x.className = "src-clear"; x.textContent = "×"; x.title = "Clear source";
  x.addEventListener("click", e => { e.stopPropagation(); clearSource(); });
  return x;
}
// Drop the shared source and every source-tied piece of state (end frame, video-ness, the mask, the outpaint stage),
// then re-render whichever stage is active so its empty picker returns.
function clearSource() {
  editCurrent = null; outpaintBase = null;
  outStagedBase = null;
  lastFrameId = null;                                     // the end frame was tied to the old source
  srcIsVideo = false;                                     // no clip source anymore
  maskId = null; if (maskEditor) maskEditor.clear();      // the painted mask was bound to the old source
  renderSrc(); renderEditLastFrame();
  applySourceMediaUi();
  updateSubmitEnabled();
  if (activeMode === "video") setMode("edit");            // leave V2V-only mode — there's no clip to quantize now
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
  if (editSupportsLastFrame() && isVid) { setStatus("Please choose an image for the first frame.", { error: true }); return; }
  if (!isImageFile(f) && !isVid) { setStatus("Please choose an image or video file.", { error: true }); return; }
  setStatus("Uploading…");
  try {
    const id = await uploadToInput(f, f.name || (isVid ? "edit_src.mp4" : "edit_src.png"));
    editCurrent = id;                                        // the new upload is the source for chat AND outpaint
    outpaintBase = id; outStagedBase = null;
    lastFrameId = null;                                      // the end frame was tied to the old source — drop it
    maskId = null; if (maskEditor) maskEditor.clear();       // the painted mask was bound to the old source
    srcIsVideo = isVid;                                       // a clip upload flips the editor into V2V-only mode
    renderSrc(); renderEditLastFrame();
    applySourceMediaUi();
    updateSubmitEnabled();   // a source is now set → enable the submit(s) whose workflows are available
    if (srcIsVideo) setMode("video");                         // clip → the single Pixelize (V2V) mode
    else if (activeMode === "video") setMode("edit");         // switched back to an image source
    else if (activeMode === "outpaint") { setupOutpaintStage(); outStagedBase = outpaintBase; }
    setStatus("");
  } catch (err) { setStatus(friendlyError(err), { error: true }); }
}
$editSrcFile.addEventListener("change", e => { const files = Array.from(e.target.files || []); e.target.value = ""; handleEditSrcFiles(files); });
// The source box and both empty-state stages accept a dropped image/video — same path as picking one.
attachDropUpload($editSrc, handleEditSrcFiles);
attachDropUpload($outpaintStage, handleEditSrcFiles);
// Pasting an image from the clipboard (Ctrl+V) seeds the source, the same as picking or dropping one.
document.addEventListener("paste", e => {
  const items = e.clipboardData && e.clipboardData.items;
  if (!items) return;
  for (const it of items) {
    if (it.kind === "file" && /^image\//.test(it.type)) {
      const f = it.getAsFile();
      if (f) { e.preventDefault(); handleEditSrcFiles([f]); return; }
    }
  }
});
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
// The reference machinery is shared by two tabs — chat Edit/Animate and the reference-capable Inpaint (Qwen-Image-Edit
// masked). The pure accessors below read a MODEL's declared reference types [{ kind, max }]; makeRefUi() binds a live
// model-getter + a refs array + its DOM strip into one controller, so each tab owns its own refs without duplicating
// the upload/chip/cap logic.
function refTypesOf(m) { const r = m && m.edit && m.edit.reference; return (r && r.types) || []; }
function refMaxOf(m, kind) { const t = refTypesOf(m).find(x => x.kind === kind); return (t && t.max) || 0; }
function refTotalMax(m) { return refTypesOf(m).reduce((n, t) => n + (t.max || 0), 0); }
function refCountOf(refs, kind) { return refs.filter(r => r.kind === kind).length; }
// The <input accept> string from a model's accepted kinds, so the picker only offers files the workflow takes.
function refAcceptOf(m) { return refTypesOf(m).filter(t => t.max > 0).map(t => `${t.kind}/*`).join(","); }
function refHintOf(m) { const r = m && m.edit && m.edit.reference; return (r && r.hint) || ""; }

// One reference controller bound to a tab. `modelOf()` returns the tab's current workflow, `refs` is the tab's array
// (mutated in place so callers keep their reference), and `els` are its strip/button/file/hint. References accept
// MULTIPLE files (picked or dropped), each routed by its media kind; a file of an unaccepted kind, or one over its
// per-kind cap, is rejected here.
function makeRefUi({ modelOf, refs, els, onChange }) {
  function updateBtn() {
    const total = refTotalMax(modelOf());
    els.btn.classList.toggle("hidden", total <= 0);
    els.btn.disabled = refs.length >= total;
    els.btn.textContent = total > 0 ? `＋ ref (${refs.length}/${total})` : "＋ ref";
    els.file.accept = refAcceptOf(modelOf()) || "image/*";
  }
  function updateHint() { const txt = refHintOf(modelOf()); els.hint.textContent = txt; els.hint.classList.toggle("hidden", refs.length === 0 || !txt); }
  function render() {
    els.list.innerHTML = "";
    refs.forEach((rf, i) => {
      const chip = document.createElement("div"); chip.className = "ref-chip";
      // Only an image reference has a thumbnail; an audio/video reference shows a kind glyph (its preview isn't an <img>).
      if (rf.kind === "image") {
        const im = document.createElement("img"); im.src = viewUrl(rf.id); im.alt = "reference"; chip.appendChild(im);
      } else {
        const g = document.createElement("span"); g.className = "ref-glyph"; g.textContent = rf.kind === "audio" ? "♪" : "▶"; g.title = rf.kind + " reference"; chip.appendChild(g);
      }
      const x = document.createElement("button"); x.type = "button"; x.textContent = "×"; x.title = "Remove reference"; x.addEventListener("click", () => { refs.splice(i, 1); if (onChange) onChange(); else render(); });
      chip.appendChild(x); els.list.appendChild(chip);
    });
    els.list.classList.toggle("hidden", refs.length === 0); updateBtn(); updateHint(); updateSubmitEnabled();
  }
  async function handleFiles(files) {
    for (const f of Array.from(files || [])) {
      const m = modelOf();
      if (refs.length >= refTotalMax(m)) break;
      const kind = fileKind(f);
      if (!kind || refMaxOf(m, kind) <= 0) { setStatus(`This model doesn't accept ${kind || "that"} references.`, { error: true }); continue; }
      if (refCountOf(refs, kind) >= refMaxOf(m, kind)) { setStatus(`At most ${refMaxOf(m, kind)} ${kind} reference(s).`, { error: true }); continue; }
      setStatus("Uploading reference…");
      try { const id = await uploadToInput(f, f.name || `ref.${kind}`); refs.push({ id, kind }); if (onChange) onChange(); else render(); setStatus(""); }
      catch (err) { setStatus(friendlyError(err), { error: true }); }
    }
  }
  els.btn.addEventListener("click", () => els.file.click());
  els.file.addEventListener("change", e => { const files = Array.from(e.target.files || []); e.target.value = ""; handleFiles(files); });
  // The refs strip is hidden when empty, so the ＋ ref button is the drop target that's always visible; the strip
  // itself takes drops once it holds chips.
  attachDropUpload(els.btn, handleFiles);
  attachDropUpload(els.list, handleFiles);
  return { render, updateBtn, updateHint, handleFiles };
}

// Chat reference controller. Reads the EFFECTIVE descriptor so a masked route offers the sibling's reference capacity
// (Qwen-Image-Edit masked takes references too). The thin same-named wrappers keep every existing call site working.
function referenceCapabilityModel() {
  const effective = effectiveEditModel();
  if (takesReferences(effective)) return effective;
  const base = editModel();
  return (base && base.referenceWorkflow && EDIT_MODELS[base.referenceWorkflow]) || effective;
}
const editRefUi = makeRefUi({ modelOf: referenceCapabilityModel, refs: editRefs,
  els: { list: $editRefs, btn: $editRefBtn, file: $editRefFile, hint: $editRefHint }, onChange: refreshEditRouting });
function renderEditRefs() { editRefUi.render(); }
function updateEditRefBtn() { editRefUi.updateBtn(); }
function updateEditRefHint() { editRefUi.updateHint(); }

// --- last frame (i2v first/last-frame editors) --------------------------------------------------
// A single optional END frame, offered only when the primary editor accepts one (supportsLastFrame) — a single-model
// affordance like references (there's no primary with 2+ checked). It is stacked under the first-frame preview with
// Loop between them; buildChatItems sends it as lastFrameImageId for the workflow's endpoint-conditioning graph.
const editSupportsLastFrame = () => { const m = editModel(); return !!(m && m.supportsLastFrame); };
// Loop is live only when the primary editor accepts a last frame AND the box is checked — the same gate as the button.
// While active it hides the pick-a-distinct-last-frame affordances: the source stands in as the final frame instead.
const editLoopActive = () => editSupportsLastFrame() && !!($editLoop && $editLoop.checked);
function renderEditLastFrame() {
  $editLastFrame.innerHTML = "";
  const supported = editSupportsLastFrame(), showPick = supported && !editLoopActive();
  if ($editSrcLabel) $editSrcLabel.textContent = supported ? "First frame" : "Editing";
  $editFrameControls.classList.toggle("hidden", !supported);
  $editLastFrameWrap.classList.toggle("hidden", !showPick);
  if (lastFrameId && showPick) {
    const im = document.createElement("img"); im.src = viewUrl(lastFrameId); im.alt = "last frame"; $editLastFrame.appendChild(im);
    const x = document.createElement("button"); x.type = "button"; x.textContent = "×"; x.title = "Remove last frame";
    x.addEventListener("click", () => { lastFrameId = null; renderEditLastFrame(); });
    x.className = "src-clear"; $editLastFrame.appendChild(x);
  } else if (showPick) {
    const pick = selectLastFrameButton(); $editLastFrame.appendChild(pick);
  }
}
// Checking/unchecking Loop only reshapes the last-frame UI and persists the pref; the source itself is sent on submit.
if ($editLoop) $editLoop.addEventListener("change", () => { renderEditLastFrame(); savePrefs(); });
function selectLastFrameButton() {
  const b = document.createElement("button"); b.type = "button"; b.className = "edit-pick-src";
  const ic = document.createElement("span"); ic.className = "eps-icon"; ic.textContent = "⇪";
  const tx = document.createElement("span"); tx.textContent = "Select a last frame";
  b.appendChild(ic); b.appendChild(tx); b.addEventListener("click", () => $editLastFrameFile.click());
  return b;
}
function imageDimensions(url) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve({ width: image.naturalWidth, height: image.naturalHeight });
    image.onerror = () => reject(new Error("The image dimensions could not be read."));
    image.src = url;
  });
}
// A single end frame (picked or dropped) — takes the first file.
async function handleEditLastFrameFiles(files) {
  const f = files && files[0];
  if (!f) return;
  if (!isImageFile(f)) { setStatus("Please choose an image file.", { error: true }); return; }
  if (!editCurrent) { setStatus("Select a first frame before choosing a last frame.", { error: true }); return; }
  setStatus("Uploading last frame…");
  let objectUrl;
  try {
    objectUrl = URL.createObjectURL(f);
    const [first, last] = await Promise.all([imageDimensions(viewUrl(editCurrent)), imageDimensions(objectUrl)]);
    if (first.width * last.height !== last.width * first.height) {
      setStatus(`The first and last frames must have the same aspect ratio (${first.width}×${first.height} vs ${last.width}×${last.height}).`, { error: true });
      return;
    }
    lastFrameId = await uploadToInput(f, f.name || "last_frame.png"); renderEditLastFrame(); setStatus("");
  }
  catch (err) { setStatus(friendlyError(err), { error: true }); }
  finally { if (objectUrl) URL.revokeObjectURL(objectUrl); }
}
$editLastFrameFile.addEventListener("change", e => { const files = Array.from(e.target.files || []); e.target.value = ""; handleEditLastFrameFiles(files); });
attachDropUpload($editLastFrame, handleEditLastFrameFiles);

// --- chat edit: fan the instruction across every selected model --------------------------------
// n comes from the Apply button's hold-to-reveal count picker (a plain click = 1), exactly like the gen page. It
// multiplies ON TOP of the model fan-out: models × n runs, so two checked models held to 4 makes eight edits — all
// submitted as ONE /enqueue job with N slots, which the queue renders one at a time.
async function buildChatItems(n) {
  const instruction = $instruction.value.trim();
  const models = editModels();
  if (!models.length) return [];
  const referenceOnly = !editCurrent && models.length === 1
    && models.every(supportsReferenceOnly) && editRefs.some(r => r.kind === "image");
  if (!editCurrent && !referenceOnly) return [];
  // "single" is about the number of MODELS: reference images, the end frame and the mask have no primary with 2+
  // checked (and a mask forces single-select anyway); the shared param panel applies to every selected model.
  const single = models.length === 1;
  const refIds = single ? editRefs.map(r => r.id) : [];
  const overrides = readOverrides($("editParams"));
  // A painted mask (single-select only): upload it once, lazily. It routes to the masked sibling when the editor has
  // one; otherwise it rides the plain workflow and the server composites the painted region back over the source.
  let maskAttach = null;
  if (single && maskActive()) {
    if (maskId == null) maskId = await uploadToInput(await maskEditor.buildMaskPng(), "inpaint_mask.png");
    maskAttach = maskId;
  }
  const items = [];
  for (const m of models)
    for (let i = 0; i < n; i++) {
      // The end frame is a single-model affordance (no primary with 2+). Loop sends the source itself as the last frame.
      const lastFrame = (single && m.supportsLastFrame) ? (editLoopActive() ? editCurrent : lastFrameId) : null;
      // The effective descriptor when masked: the sibling workflow (if any), whose negative capability also applies.
      const eff = single ? (effectiveEditModel() || m) : m;
      const wf = eff !== m ? gwModel(eff) : gwModel(m);
      const itemOverrides = { ...overrides };
      if (takesReferences(eff)) itemOverrides.reference_aspect = (maskAttach || (editCurrent && !eff.supportsReferenceAspectWithSource))
        ? "reference" : referenceAspect;
      // Send raw text; the server resolves Comfy {a|b} choices independently for every submitted edit slot.
      items.push({ workflow: wf, edit: true, instruction, negativePrompt: editNegFor(eff), randomArtist: wantsEditRandomArtist(eff),
        imageId: editCurrent, referenceIds: takesReferences(eff) ? refIds : [], lastFrameImageId: lastFrame, maskImageId: maskAttach, overrides: itemOverrides });
    }
  return items;
}
// Chat/animate Apply uses the ONE shared submit control (core.js attachEnqueueSubmit): a click (or held count) builds
// the items and POSTs one /enqueue job; a press while busy queues another. buildItems does the mode's own validation.
attachEnqueueSubmit({
  button: $editSend, form: $editComposer, panel: editPanel(editModeSpec("chat")), ...editSubmitBase("chat"),
  buildItems: async n => {
    const models = editModels();
    if (!models.length) { setStatus("Pick at least one workflow.", { error: true }); return []; }
    if (!editCurrent && !(models.length === 1 && models.every(supportsReferenceOnly) && editRefs.some(r => r.kind === "image"))) {
      setStatus("Add image 1, or attach an image reference for a reference-only workflow.", { error: true }); return [];
    }
    // A pure-inpaint editor needs a mask (the button is disabled anyway; this is the belt-and-braces message).
    if (editSubmitBlockedByMask()) { setStatus("Draw a mask first — click the pencil on the source.", { error: true }); return []; }
    // Keep the attached references after Apply so repeated animate/edit runs reuse them (like the gen page keeps its
    // composer). They're removed only by the user (the × on each chip) or when switching source/model.
    return await buildChatItems(Math.max(1, n || 1));   // empty instruction is allowed — never blocked on a blank prompt
  },
});
$cancelEdit.addEventListener("click", () => cancelGeneration());
// The chat instruction box doubles as the full TAG prompt for whole-image redraws (Anima/Photanima): same '#'/'@'
// autocomplete, gated on the primary editor's tagging — inert for instruction/animate editors, which have none.
// Enter does NOT apply the edit; Apply is the only way to start one. The popup still consumes Enter to accept a
// highlighted tag while it is open, which is the only special meaning Enter has in this box.
if ($instruction && $instructionTagPop) initTagBox({ input: $instruction, pop: $instructionTagPop, getModel: editModel });

// --- mask painting (modal off the source preview) -----------------------------------------------
// The paint machinery lives in mask-editor.js; the controller is created at boot. Here we only keep the brush-size
// persistence — the slider now sits in the modal toolbar, and its value rides the editor-state blob like before.
$brushSize.addEventListener("change", savePrefs);

// The booru '#'/'@' autocomplete on the chat negative box, gated on the primary editor's tagging (inert for non-tag
// editors, which don't show a negative box anyway).
if ($editNeg && $editNegTagPop) initTagBox({ input: $editNeg, pop: $editNegTagPop, getModel: editModel });

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
  syncOutpaintLabel(); updateOutpaintNeg(); updateOutpaintParams();
}
function syncOutpaintLabel() { const m = outpaintModel(); $outpaintModelToggle.innerHTML = ""; const s = document.createElement("span"); s.textContent = m ? cleanName(m) : "Pick a workflow…"; $outpaintModelToggle.appendChild(s); }
function updateOutpaintParams() {
  const m = outpaintModel();
  const qualityOnly = m && { ...m,
    exposedParams: (m.exposedParams || []).filter(p => p.key === "edit_quality"),
    hiddenParams: (m.hiddenParams || []).filter(p => p.key === "edit_quality") };
  renderParamFields($outpaintParams, qualityOnly);
  restoreParams($outpaintParams);
}
function selectOutpaint(id) { selectedOutpaintId = id; savePrefs(true); $outpaintModelMenu.querySelectorAll(".model-opt").forEach(o => o.classList.toggle("selected", o.dataset.id === id)); syncOutpaintLabel(); updateOutpaintNeg(); updateOutpaintParams(); }
$outpaintParams.addEventListener("change", () => persistParams($outpaintParams));
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
// Build the outpaint items: n takes of the same raw prompt; the server re-rolls {a|b} per slot and fills a fresh seed.
// Pads plus an optionally revealed Quality selector are the only overrides; fill
// strength, feather, mask grow, and model-specific controls stay at configuration defaults.
//
// Do NOT read the normal editParams panel here. The editor's param map (editParamPrefs) is flat and keyed by param NAME
// across every panel, and `denoise` is "Change amount" (default 0.6, min 0.2) to anima-inpaint but "Fill strength"
// (default 1.0, min 0.5) to anima-outpaint. Feeding inpaint's denoise in would half-denoise the grey padding that
// ImagePadForOutpaint lays down, so the border would come back grey instead of painted.
function buildOutpaintItems(n) {
  const model = outpaintModel();
  if (!model || !outpaintBase || !padsTotal()) return [];
  const prompt = $outpaintPrompt.value.trim();
  const overrides = { ...readOverrides($outpaintParams), pad_left: pads.left, pad_top: pads.top, pad_right: pads.right, pad_bottom: pads.bottom };
  const items = [];
  for (let i = 0; i < n; i++)
    items.push({ workflow: gwModel(model), edit: true, instruction: prompt, negativePrompt: outpaintNegFor(model),
      imageId: outpaintBase, referenceIds: [], overrides });
  return items;
}
// Outpaint uses the ONE shared submit control: n takes of the same base + pads + prompt as one /enqueue job. A finished
// slot becomes the new base (editModeSpec.onSlot) so you can keep pushing the frame out; a press while busy queues more.
attachEnqueueSubmit({
  button: $outpaintGo, form: $outpaintComposer, panel: editPanel(editModeSpec("outpaint")), ...editSubmitBase("outpaint"),
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
  if (!["edit", "redraw", "upscale", "effects", "animate", "outpaint", "video"].includes(mode)) mode = "edit";
  activeMode = mode;
  for (const t of $editTabs.querySelectorAll(".edit-tab")) t.classList.toggle("active", t.dataset.mode === mode);
  const chat = mode !== "outpaint";
  $chatMode.classList.toggle("hidden", !chat);
  $outpaintMode.classList.toggle("hidden", mode !== "outpaint");
  // V2V (video) has no prompt — the quantize is deterministic — so hide the instruction box; only its params matter.
  const instrField = $instruction.closest(".field");
  if (instrField) instrField.hidden = (mode === "video");
  // Expose the active chat bucket on the composer so CSS can scope per-mode tweaks (e.g. animate right-aligns its
  // side-pane controls). Outpaint hides the chat composer entirely, so its value is irrelevant.
  if (chat) $chatMode.dataset.mode = mode;
  if (chat) { chatBucket = mode; populateChatMenu(); }   // chat modes: edit | redraw | upscale | effects | animate | video
  else enterOutpaint();
  updateSubmitEnabled();   // reflect the newly-active mode's source/workflow presence on its submit
  refreshTabSelect();   // keep the mobile mirror's selected option on the active mode
}
// Whether a tab's group has ≥1 available (non-hidden) workflow. Chat buckets reuse chatHasModels; inpaint/outpaint
// have their own workflow sets. Drives both the source-media split and the empty-tab hiding below.
function tabHasModels(mode) {
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
  updateReferenceAspectPicker();   // real image1 hides shape; clearing it restores the source-free choice
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
const outpaintWorkflowIds = () => new Set(outpaintModelList().map(gwModel));
// The adopted job's spec mode, derived from its OWN workflow (not the visible tab): inpaint/outpaint workflows route to
// their tab, everything else — including a plain compose gen with no editor workflow — is chat. Mode switching is free
// while a job runs, so the spec must be captured from the job at adopt time (runningSpecMode) and NOT re-read off the
// currently-visible mode, which the user may have since switched away from.
function specModeForJob(job) {
  if (outpaintWorkflowIds().has(job.model)) return "outpaint";
  return "chat";   // chat owns the edit + inpaint workflows now
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
        eta: p.eta, onProgress: p.onProgress, previewTarget: mine ? p.previewTarget : undefined,
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
  const settings = await loadEditModels();   // also seeds mode/brush/params and every tab's selected ids from the account blob
  setTagBoxPinBookmarks(settings.pinBookmarks);   // one account toggle governs every tag box on this page
  // Favorited/banned marks in the '#'/'@' popup: one-time snapshots, applied when they resolve. Detached so they
  // never gate boot, un-caught so a real endpoint failure surfaces rather than being swallowed.
  fetchBookmarks().then(setTagBoxFavorites);
  fetchAllBans().then(setTagBoxBans);
  editCurrent = seed.id;
  // The paint-mask controller (mask-editor.js) — created before renderSrc so the source preview can register its
  // tinted mask-preview canvas. A stroke or clear routes back through maskChanged.
  maskEditor = createMaskEditor({ modalEl: $maskModal, stageEl: $maskModalStage, brushEl: $brushSize, onChange: maskChanged });
  try {
    srcIsVideo = (await detectSrcMedia(seed.id)) === "video";   // a clip seed → collapse into V2V mode
  } catch (e) {
    // Do not render the source or enable any submit path under a guessed media type. The rejected boot promise keeps
    // the rest of initialization stopped; this status gives the user the actionable reason instead of a dead page.
    setStatus(friendlyError(e), { error: true });
    throw e;
  }
  if (savedLoop != null && $editLoop) $editLoop.checked = savedLoop;   // restore the Loop pref before the last-frame UI renders
  if (savedRandomArtist != null && $editRandomArtist) $editRandomArtist.checked = savedRandomArtist;
  setReferenceAspect(savedReferenceAspect || "reference", false);
  renderSrc(); renderEditRefs(); renderEditLastFrame();
  applySourceMediaUi();
  if (savedBrushSize != null) $brushSize.value = savedBrushSize;
  if (srcIsVideo) {
    // The source is a clip: pixel-quantize V2V is the ONLY option, regardless of the saved tab.
    setMode("video");
  } else {
    let saved = savedMode || "edit";
    // Don't land on an empty tab: fall back to one that has models.
    const has = { edit: chatHasModels("edit"), redraw: chatHasModels("redraw"), upscale: chatHasModels("upscale"), effects: chatHasModels("effects"), animate: chatHasModels("animate"), outpaint: outpaintModelList().length > 0 };
    if (!has[saved]) saved = ["edit", "redraw", "upscale", "effects", "animate", "outpaint"].find(k => has[k]) || "edit";
    setMode(saved);
  }
  setTimeout(() => { if (activeMode !== "outpaint" && activeMode !== "video") $instruction.focus(); }, 50);
  startEditRecover();   // attachLiveRecover owns its own poll interval + visibility re-check + initial adoption tick
})();
function chatHasModels(bucket) {
  const prev = chatBucket; chatBucket = bucket; const n = chatModels().length; chatBucket = prev; return n > 0;
}
