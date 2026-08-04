// patches.js — what this app changes in ComfyUI's own code, and whether those changes are in place.
//
// Every state on this page is DERIVED from the files on disk each time it is asked for: a patch is "applied"
// exactly when it un-applies cleanly. Nothing is stored, so nothing can claim a patch is in place after somebody
// has edited the tree underneath it.
//
// A write reloads the whole state rather than patching the row — applying one patch can change what another one
// reports, and the consequence is the point of the page.
//
// JSON only — /api/comfy-patches returns data and this builds the DOM from it.
(function () {
  "use strict";

  const $rows = $("patchRows");
  if (!$rows) return;

  const esc = escapeHtml;
  const api = "/api/comfy-patches";

  // What each state means in the words of this page, and whether it is worth colouring.
  const STATES = {
    Applied:       { label: "in place",      cls: "is-applied" },
    NotApplied:    { label: "not applied",   cls: "" },
    TargetMissing: { label: "not installed", cls: "" },
    Conflicted:    { label: "conflict",      cls: "is-conflict" },
  };

  let state = null;

  async function load() {
    let body;
    try {
      const res = await fetch(api, { headers: { Accept: "application/json" } });
      body = await res.json();
      if (!res.ok) throw new Error(body.error || `the server answered ${res.status}`);
    } catch (e) {
      // A failed read is not an empty patch set, and rendering it as one would read as "nothing to do".
      console.error("patches: could not load", e);
      $rows.innerHTML = `<p class="muted">The patch list couldn’t be read — ${esc(e.message)}.</p>`;
      return;
    }
    state = body;
    render();
  }

  function row(p) {
    const s = { ...(STATES[p.state] || { label: p.state, cls: "" }) };
    // An install-only patch verifies that the pack is THERE, not what is in it — say the weaker thing it means.
    if (p.installOnly && p.state === "Applied") s.label = "installed";
    const detail = p.detail ? `<p class="patchrow-detail">${esc(p.detail)}</p>` : "";

    // The action a row offers is the one its state calls for. A conflict offers "Overwrite" only when what it
    // would replace is nameable — a hunk that no longer fits is not something a flag can force.
    let action = "";
    if (p.state === "Applied") {
      action = `<button type="button" class="link-btn danger" data-do="remove" data-id="${esc(p.id)}">Remove</button>`;
    } else if (p.state === "NotApplied" || p.state === "TargetMissing") {
      action = `<button type="button" class="link-btn" data-do="apply" data-id="${esc(p.id)}">Apply</button>`;
    } else if (p.state === "Conflicted" && p.occupied && p.occupied.length) {
      action = `<button type="button" class="link-btn" data-do="overwrite" data-id="${esc(p.id)}">Overwrite</button>`;
    }

    // `does` is shown; `why` stays the tooltip. Applying or removing one of these changes what the renderer can
    // do, and a column of names is not something that decision can be made from.
    return `<div class="patchrow" title="${esc(p.why)}">
      <span class="patchrow-main">
        <span class="patchrow-name">${esc(p.title)}</span>
        <span class="patchrow-target">${esc(p.target)}</span>
      </span>
      <span class="patch-state ${s.cls}">${esc(s.label)}</span>
      ${action || "<span></span>"}
      <p class="patchrow-does">${esc(p.does)}</p>
      ${detail}
    </div>`;
  }

  function render() {
    if (!state.rootOk) {
      $rows.innerHTML = `<section class="settings-card"><p class="muted">${esc(state.rootError)}</p>
        <p class="settings-desc"><a class="link-btn" href="/settings/machine">Open This machine →</a></p></section>`;
      $("applyAllBtn").hidden = true;
      $("restartCard").hidden = true;
      return;
    }

    $rows.innerHTML = `<section class="settings-card">
      <h3>Patches</h3>
      ${state.patches.map(row).join("")}
      <p class="settings-desc">Applied to <code>${esc(state.root)}</code>.${
        state.ephemeral ? " ComfyUI is part of this container image, so changes here last until the container is recreated." : ""
      }</p>
    </section>`;

    $rows.querySelectorAll("button[data-do]").forEach((b) =>
      b.addEventListener("click", () => act(b, b.dataset.do, b.dataset.id)));

    $("applyAllBtn").hidden = !state.patches.some((p) => p.state === "NotApplied" || p.state === "TargetMissing");

    $("restartCard").hidden = false;
    $("restartBtn").hidden = !state.canRestart;
    $("restartNote").textContent = state.canRestart
      ? "ComfyUI loads its code once, at startup, so a patch changes nothing until it restarts."
      : "ComfyUI loads its code once, at startup — restart it yourself for these to take effect. This installation "
        + "didn’t start it, so it can’t restart it for you.";
  }

  async function act(btn, what, id) {
    const patch = state.patches.find((p) => p.id === id);

    if (what === "remove") {
      // Removing an install-only patch deletes the pack itself, which is a bigger thing than withdrawing a fix.
      const warning = patch.warn
        || (patch.installOnly ? `This deletes ${patch.target} from disk — the whole pack, not just a change to it.` : null);
      if (warning && !confirm(`${warning}\n\nRemove “${patch.title}” anyway?`)) return;
    }
    if (what === "overwrite") {
      const files = patch.occupied.join("\n  ");
      if (!confirm(`This replaces ${patch.occupied.length} file(s) that differ from what the patch installs:\n\n  ${files}\n\nWhatever is in them is lost. Continue?`)) return;
    }

    const label = btn.textContent;
    btn.disabled = true;
    btn.textContent = what === "remove" ? "Removing…" : "Applying…";
    try {
      const res = await fetch(`${api}/${what === "remove" ? "remove" : "apply"}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id, overwrite: what === "overwrite" }),
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) { toast(body.error || "That didn’t work"); return; }
      toast(body.note || (what === "remove" ? "Removed" : "Applied"));
    } catch (e) {
      console.error("patches: action failed", e);
      toast("That didn’t work");
    } finally {
      btn.disabled = false;
      btn.textContent = label;
      await load();
    }
  }

  $("applyAllBtn").addEventListener("click", async (e) => {
    const btn = e.currentTarget;
    btn.disabled = true;
    try {
      const res = await fetch(`${api}/apply-all`, { method: "POST" });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) { toast(body.error || "Couldn’t apply them all"); return; }
      toast((body.notes && body.notes.length) ? body.notes.join(" ") : "Applied");
    } catch (e) { console.error("apply-all failed:", e); toast("Couldn’t apply them all"); }
    finally { btn.disabled = false; await load(); }
  });

  $("restartBtn").addEventListener("click", async (e) => {
    const btn = e.currentTarget;

    // Restarting kills whatever is mid-render. Say how much before asking, rather than after. pageSize=1 because
    // only the outstanding summary is wanted, not the page of jobs that comes with it.
    let outstanding = 0;
    try {
      const q = await fetch(`${GATEWAY}/queue?page=1&pageSize=1`).then((r) => (r.ok ? r.json() : null));
      if (q && q.outstanding) outstanding = q.outstanding.jobs || 0;
    } catch (e) { console.debug("outstanding count unavailable:", e); /* the count is a courtesy; not having it is not a reason to refuse */ }

    const warning = outstanding
      ? `${outstanding} job(s) are still queued or rendering and will be lost.\n\nRestart the renderer anyway?`
      : "Restart the renderer?";
    if (!confirm(warning)) return;

    btn.disabled = true;
    btn.textContent = "Restarting…";
    try {
      const res = await fetch(`${api}/restart`, { method: "POST" });
      const body = await res.json().catch(() => ({}));
      toast(res.ok ? "Restarting — it will be back shortly" : (body.error || "Couldn’t restart it"));
    } catch (e) { console.error("patch restart failed:", e); toast("Couldn’t restart it"); }
    finally { btn.disabled = false; btn.textContent = "Restart the renderer"; }
  });

  load();
})();
