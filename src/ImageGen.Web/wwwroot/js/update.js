// The "a newer version exists" banner.
//
// Dismissal is a SESSION COOKIE, not localStorage: client state does not belong in web storage here, and this
// state is genuinely per-session — dismissing should quieten it while you work and remind you next time you
// come back, because an update you dismissed forever is one you never install. The cookie holds the version it
// dismissed, so a release that lands mid-session still shows.
//
// JSON only — /api/update returns data and this builds the DOM from it.
(function () {
  "use strict";

  const $banner = document.getElementById("updateBanner");
  if (!$banner) return;

  const COOKIE = "imagegen_update_dismissed";

  // Its own, rather than core.js's: this runs on EVERY page, and core.js is opted into per page.
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  function dismissedVersion() {
    const hit = document.cookie.split("; ").find((c) => c.startsWith(COOKIE + "="));
    return hit ? decodeURIComponent(hit.slice(COOKIE.length + 1)) : null;
  }

  // No expires/max-age: that is what makes it a session cookie, cleared when the browser closes. SameSite=Lax
  // because nothing else should be able to set it, and it never leaves this origin.
  function dismiss(version) {
    document.cookie = `${COOKIE}=${encodeURIComponent(version)}; path=/; SameSite=Lax`;
    $banner.hidden = true;
  }

  (async () => {
    let status;
    try {
      const r = await fetch("/api/update", { headers: { Accept: "application/json" } });
      if (!r.ok) return;                      // not something to tell anyone about
      status = await r.json();
    } catch (_) {
      return;                                 // offline, or the app is going down; either way, say nothing
    }

    if (!status || !status.latest) return;    // up to date, unversioned build, or the check could not run
    if (dismissedVersion() === status.latest) return;

    $banner.innerHTML =
      `<span class="update-text">Version <b>${esc(status.latest)}</b> is available — you have ${esc(status.current || "an older build")}.</span>` +
      `<a class="update-link" href="${esc(status.url)}" target="_blank" rel="noopener noreferrer">What’s new →</a>` +
      `<button type="button" class="update-close" aria-label="Dismiss">✕</button>`;
    $banner.querySelector(".update-close").addEventListener("click", () => dismiss(status.latest));
    $banner.hidden = false;
  })();
})();
