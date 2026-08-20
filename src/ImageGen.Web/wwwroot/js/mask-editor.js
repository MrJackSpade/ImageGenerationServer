// Mask editor for the Edit page — extracted from edit.js so edit.js no longer owns the paint machinery. A pencil on
// the source preview opens a paint MODAL (image + a paint canvas at native resolution); the small source thumbnail
// carries a tinted preview of whatever has been painted. The mask is bound to the source url open() is given; a new
// source means a fresh open() (or clear()).
//
//   createMaskEditor({ modalEl, stageEl, brushEl, onChange })
//     .open(srcUrl, natW, natH)  show the modal, staging a paint canvas for this source (idempotent per source)
//     .close()                   hide the modal (the painted mask is retained)
//     .hasMask()                 true when a stroke has left visible pixels
//     .clear()                   wipe the mask
//     .buildMaskPng()            → Promise<Blob>: a white-on-black PNG at native resolution (white = the painted area)
//     .drawPreview(canvasEl)     render the mask, scaled, into a preview canvas on the thumbnail (red tint via CSS)
//
// The mask is painted at FULL opacity (solid → an unambiguous binary mask); the see-through tint is purely CSS
// opacity on the canvas element, so the extracted PNG is always 100% solid, never 50%.
function createMaskEditor({ modalEl, stageEl, brushEl, onChange }) {
  let canvas = null, ctx = null, srcUrl = null, natW = 0, natH = 0, eraseMode = false, painted = false;
  const previews = new Set();   // thumbnail preview canvases to keep in step with the paint canvas

  function setupStage(url, w, h) {
    stageEl.innerHTML = ""; canvas = ctx = null; painted = false;
    natW = w || 0; natH = h || 0;
    const img = new Image(); img.className = "mask-img"; img.alt = ""; img.decoding = "async";
    const cv = document.createElement("canvas"); cv.className = "mask-canvas";
    img.onload = () => {
      cv.width = img.naturalWidth || natW || 1024; cv.height = img.naturalHeight || natH || 1024;
      natW = cv.width; natH = cv.height; ctx = cv.getContext("2d");
    };
    img.src = url;
    stageEl.appendChild(img); stageEl.appendChild(cv);
    canvas = cv; bindPaint(cv);
  }

  function bindPaint(cv) {
    let drawing = false;
    const stamp = e => {
      if (!ctx) return;
      const r = cv.getBoundingClientRect(); if (!r.width) return;
      const scale = cv.width / r.width;
      const x = (e.clientX - r.left) * scale, y = (e.clientY - r.top) * scale;
      const radius = Math.max(1, (Number(brushEl && brushEl.value) || 56) * scale / 2);
      ctx.globalCompositeOperation = eraseMode ? "destination-out" : "source-over";
      ctx.fillStyle = "rgba(255,40,60,1)";              // SOLID — display tint comes from CSS canvas opacity
      ctx.beginPath(); ctx.arc(x, y, radius, 0, Math.PI * 2); ctx.fill();
      painted = true;
    };
    // Painting itself only touches the paint canvas — the thumbnail preview and the (expensive) routing refresh run once
    // at stroke END, not per pointermove, so a drag doesn't re-read the whole canvas or re-render the panel every frame.
    const endStroke = () => { if (!drawing) return; drawing = false; drawAllPreviews(); if (onChange) onChange(); };
    cv.addEventListener("pointerdown", e => { drawing = true; try { cv.setPointerCapture(e.pointerId); } catch (err) { console.debug("pointer capture failed:", err); } stamp(e); });
    cv.addEventListener("pointermove", e => { if (drawing) stamp(e); });
    cv.addEventListener("pointerup", endStroke); cv.addEventListener("pointercancel", endStroke); cv.addEventListener("pointerleave", endStroke);
  }

  // A stroke may have been fully erased, so verify actual pixels rather than trusting the painted flag alone.
  function hasMask() {
    if (!canvas || !ctx || !painted) return false;
    const d = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
    for (let i = 3; i < d.length; i += 4) if (d[i] > 12) return true;
    return false;
  }

  function clear() {
    if (ctx && canvas) ctx.clearRect(0, 0, canvas.width, canvas.height);
    painted = false; drawAllPreviews();
    if (onChange) onChange();
  }

  async function buildMaskPng() {
    if (!canvas || !ctx) throw new Error("Paint the area to change first.");
    const W = canvas.width, H = canvas.height;
    const md = ctx.getImageData(0, 0, W, H).data;
    let any = false; for (let i = 3; i < md.length; i += 4) if (md[i] > 12) { any = true; break; }
    if (!any) throw new Error("Paint the area to change first.");
    const c = document.createElement("canvas"); c.width = W; c.height = H;
    const octx = c.getContext("2d"); const out = octx.createImageData(W, H);
    for (let i = 0; i < out.data.length; i += 4) {
      const on = md[i + 3] > 12 ? 255 : 0;            // painted (overlay alpha) → white, opaque
      out.data[i] = on; out.data[i + 1] = on; out.data[i + 2] = on; out.data[i + 3] = 255;
    }
    octx.putImageData(out, 0, 0);
    const blob = await new Promise(res => c.toBlob(res, "image/png"));
    if (!blob) throw new Error("Couldn't build the mask.");
    return blob;
  }

  // Copy the paint canvas (native resolution) onto a preview canvas; CSS scales it into the thumbnail and tints it.
  function drawOne(previewCv) {
    if (!previewCv) return;
    const w = (canvas && canvas.width) || natW, h = (canvas && canvas.height) || natH;
    if (!w || !h) return;
    previewCv.width = w; previewCv.height = h;
    const pctx = previewCv.getContext("2d");
    pctx.clearRect(0, 0, w, h);
    // Copy the paint canvas straight over (no getImageData) — an unpainted canvas copies as transparent, so `painted`
    // alone gates it; a cleared mask (painted=false) leaves the preview blank.
    if (canvas && painted) pctx.drawImage(canvas, 0, 0);
  }
  function prunePreviews() {
    for (const p of previews) if (!p.isConnected) previews.delete(p);
  }
  function drawAllPreviews() {
    prunePreviews();
    for (const p of previews) drawOne(p);
  }
  function drawPreview(previewCv) {
    if (!previewCv) return;
    prunePreviews();
    previews.add(previewCv);
    drawOne(previewCv);
  }

  function open(url, w, h) {
    if (url !== srcUrl) { srcUrl = url; setupStage(url, w, h); }
    modalEl.classList.remove("hidden");
  }
  function close() { modalEl.classList.add("hidden"); }

  // The modal's own toolbar buttons, wired by convention (data-attributes inside modalEl). Backdrop click closes.
  const eraseBtn = modalEl.querySelector("[data-mask-erase]");
  if (eraseBtn) eraseBtn.addEventListener("click", () => { eraseMode = !eraseMode; eraseBtn.classList.toggle("active", eraseMode); });
  const clearBtn = modalEl.querySelector("[data-mask-clear]");
  if (clearBtn) clearBtn.addEventListener("click", clear);
  for (const b of modalEl.querySelectorAll("[data-mask-close]")) b.addEventListener("click", close);
  modalEl.addEventListener("click", e => { if (e.target === modalEl) close(); });

  return { open, close, hasMask, clear, buildMaskPng, drawPreview };
}
