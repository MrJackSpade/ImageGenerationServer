//TODO: CHECK FOR FALLBACKS
// The "a newer version exists" banner.
//
// Checked on load and then re-checked every 60 seconds by asking the server for its stored answer. The server
// re-contacts GitHub at most once an hour (see UpdateCheck); this poll is only how the page learns that a
// release the server has since noticed is available, without the tab being reloaded.
//
// Dismissal is a SESSION COOKIE, not localStorage: client state does not belong in web storage here, and this
// state is genuinely per-session — dismissing should quieten it while you work and remind you next time you
// come back, because an update you dismissed forever is one you never install. The cookie holds the version it
// dismissed, so a NEWER release than the one dismissed still shows on its own.
//
// JSON only — /api/update returns data and this builds the DOM from it.
(function () {
  "use strict";

  const $banner = document.getElementById("updateBanner");
  if (!$banner) return;

  const COOKIE = "imagegen_update_dismissed";
  const POLL_MS = 60000; // re-read the server's stored answer once a minute

  // Its own, rather than core.js's: this runs on EVERY page, and core.js is opted into per page.
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  function dismissedVersion() {
    const hit = document.cookie.split("; ").find((c) => c.startsWith(COOKIE + "="));
    return hit ? decodeURIComponent(hit.slice(COOKIE.length + 1)) : null;
  }

  // The version currently rendered in the banner, or null when it is hidden. Lets a poll leave an already-shown
  // banner (and its click handler) untouched instead of rebuilding it every minute.
  let shown = null;

  // No expires/max-age: that is what makes it a session cookie, cleared when the browser closes. SameSite=Lax
  // because nothing else should be able to set it, and it never leaves this origin.
  function dismiss(version) {
    document.cookie = `${COOKIE}=${encodeURIComponent(version)}; path=/; SameSite=Lax`;
    $banner.hidden = true;
    shown = null;
  }

  function render(status) {
    const latest = status && status.latest;

    // Up to date, unversioned build, the check could not run, or this exact version was dismissed: stay quiet.
    if (!latest || dismissedVersion() === latest) {
      if (!$banner.hidden) {
        $banner.hidden = true;
        shown = null;
      }
      return;
    }

    if (shown === latest) return; // already showing this version — leave it and its handler in place

    $banner.innerHTML =
      `<span class="update-text">Version <b>${esc(latest)}</b> is available — you have ${esc(status.current || "an older build")}.</span>` +
      `<a class="update-link" href="${esc(status.url)}" target="_blank" rel="noopener noreferrer">What’s new →</a>` +
      `<button type="button" class="update-close" aria-label="Dismiss">✕</button>`;
    $banner.querySelector(".update-close").addEventListener("click", () => dismiss(latest));
    $banner.hidden = false;
    shown = latest;
  }

  async function check() {
    try {
      const r = await fetch("/api/update", { headers: { Accept: "application/json" } });
      if (!r.ok) return;              // not something to tell anyone about
      render(await r.json());
    } catch (_) {
      // offline, or the app is going down; either way, say nothing and try again on the next tick
    }
  }

  check();
  setInterval(check, POLL_MS);
})();
