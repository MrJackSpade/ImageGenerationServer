// Shared live tracker — loaded app-wide by _Layout, after the page's core.js (which defines GATEWAY + gwWs).
//
// Every "live" page updates by RE-PULLING the server when it hears `imagegen:generated` (a new image landed) or
// `imagegen:refresh` (a job finalized). Produced only by compose.js's own tracker, those two signals fire only on
// the compose page — which would leave /gallery (History) unable to pick up a running batch until a reload. This
// makes the same signals available on every page: poll /forge/jobs (this user's ACTIVE jobs, so it is cross-device
// for the signed-in user) and listen on /forge/ws, firing `imagegen:generated` for each newly produced image and
// `imagegen:refresh` when a tracked job leaves the active set.
//
// The handlers are idempotent — they re-pull the authoritative server state — so producing these here is harmless
// even where another script also would. The compose page owns its own tracker (it also drives the compose bar and
// busy state from the same poll), so it claims the role synchronously and this stands down there.
(function () {
  if (typeof GATEWAY === "undefined") return;   // page has no core.js; nothing to talk to
  if (window.__liveTrackerOwned) return;        // compose.js runs the tracker on its own page
  window.__liveTrackerOwned = true;

  const announced = new Set();   // "jobId:imageId" already announced, so each image fires once
  const watching = new Set();    // jobIds seen active, to detect their disappearance (= finalized)
  let ws = null;

  async function sync() {
    let res;
    try { const r = await fetch(`${GATEWAY}/jobs`); if (!r.ok) return; res = await r.json(); } catch (e) { console.debug("tracker jobs poll failed:", e); return; }
    const jobs = res.jobs || [];
    const active = new Set(jobs.map(j => j.jobId));
    for (const j of jobs) {
      watching.add(j.jobId);
      for (const id of (j.imageIds || [])) {
        const k = j.jobId + ":" + id;
        if (!announced.has(k)) { announced.add(k); document.dispatchEvent(new CustomEvent("imagegen:generated", { detail: { id } })); }
      }
    }
    // A tracked job that is no longer active has finalized; tell the grids/strips to re-pull.
    for (const jobId of [...watching]) if (!active.has(jobId)) { watching.delete(jobId); document.dispatchEvent(new CustomEvent("imagegen:refresh")); }
  }

  // The websocket only makes the poll PROMPT: a finish event triggers an immediate sync instead of waiting for the
  // next tick. The poll remains the source of truth (it reads the app's own job state, not raw ComfyUI events).
  function openWs() {
    if (ws) return;
    try {
      ws = new WebSocket(gwWs("/ws"));
      ws.onmessage = (ev) => {
        if (typeof ev.data !== "string") return;
        let m; try { m = JSON.parse(ev.data); } catch (e) { console.debug("tracker ws non-JSON message:", e); return; }
        if (m.type === "executed" || m.type === "execution_success" || m.type === "execution_error") sync();
      };
      ws.onclose = () => { ws = null; };
      ws.onerror = (ev) => { console.debug("tracker ws error:", ev); try { ws && ws.close(); } catch (e) { console.debug("tracker ws close failed:", e); } ws = null; };
    } catch (e) { console.debug("tracker ws open failed:", e); ws = null; }
  }

  sync(); openWs();
  setInterval(() => { sync(); openWs(); }, 2500);   // same cadence as compose.js's live sync
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") { sync(); openWs(); } });
})();
