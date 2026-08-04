//TODO: CHECK FOR FALLBACKS
// Bounded-concurrency thumbnail loader.
//
// A grid page (bookmarks, gallery, an artist's gens, the recents strip) can put hundreds of thumbnails on screen at
// once. Left to the browser, HTTP/2 will happily open ~100 concurrent streams for them — over the public HTTPS edge
// that burst is what made thumbs randomly fail; over a plain-HTTP local IP the browser's own 6-connection cap hid it.
// Relying on a transport's connection limit for backpressure was the mistake; this queue makes the limit ours, so the
// page behaves identically on HTTP/1.1 and HTTP/2, local or public.
//
// Contract for render sites: emit `data-src` instead of `src` on an <img> (and `data-poster` instead of `poster` on a
// <video>, which media.js does when it upgrades a clip). Anything still using a plain `src` is untouched — single
// images (the detail view, an artist hero) don't need a queue and shouldn't wait behind one.
//
// Nothing loads until it comes near the viewport, so this also replaces `loading="lazy"` on queued images: the browser
// can't lazy-load an <img> with no src, and once we DO set src we need the request to actually start (a lazy image
// parked offscreen would hold a slot indefinitely).
//
// Loaded from _Layout's <head> so the observer is installed before the body is parsed — cards are picked up as they
// stream in, not in a lump at DOMContentLoaded.
(function () {
  // Six matches what HTTP/1.1 gave us for free, and is the concurrency the app has always effectively run at.
  const MAX_INFLIGHT = 6;
  // Start fetching a screen or so ahead of the scroll, so a steady scroll never outruns the queue.
  const NEAR_VIEWPORT = "800px 0px";

  const queue = [];
  let inflight = 0;

  const sourceOf = el => el.tagName === "VIDEO" ? el.getAttribute("data-poster") : el.getAttribute("data-src");

  function pump() {
    while (inflight < MAX_INFLIGHT && queue.length) {
      const el = queue.shift();
      const url = sourceOf(el);
      // Dropped from the DOM while it waited (media.js swaps a clip's <img> for a <video>), or already started by an
      // earlier pass. Either way it isn't ours any more, and it must not consume a slot.
      if (!url || !el.isConnected) continue;
      start(el, url);
    }
  }

  function start(el, url) {
    inflight++;
    let settled = false;
    const done = ok => {
      if (settled) return;                     // load and error can both arrive for one element; count it once
      settled = true;
      inflight--;
      // One retry, and only one. A dropped or truncated response (a flaky phone connection, an edge hiccup) is worth
      // re-asking for; a second failure is a real error and is left visible rather than hidden behind a retry loop.
      if (!ok && !el.dataset.imgqRetried) {
        el.dataset.imgqRetried = "1";
        if (el.tagName === "VIDEO") el.setAttribute("data-poster", url); else el.setAttribute("data-src", url);
        queue.push(el);
      }
      pump();
    };

    if (el.tagName === "VIDEO") {
      // A <video> poster has no load event, so a throwaway Image() holds the slot for it. Assigning the poster after
      // that resolves is a cache hit (thumbnails are served immutable), not a second fetch.
      el.removeAttribute("data-poster");
      const probe = new Image();
      probe.onload = () => { el.poster = url; done(true); };
      probe.onerror = () => { done(false); };
      probe.src = url;
      return;
    }

    el.removeAttribute("data-src");
    el.loading = "eager";                      // we decide when it loads; don't let lazy defer a slot we're holding
    el.addEventListener("load", () => done(true), { once: true });
    el.addEventListener("error", () => done(false), { once: true });
    el.src = url;
  }

  const observer = "IntersectionObserver" in window
    ? new IntersectionObserver((entries, obs) => {
        for (const e of entries) {
          if (!e.isIntersecting) continue;
          obs.unobserve(e.target);
          queue.push(e.target);                // intersection order is top-down, so the queue is already prioritised
        }
        pump();
      }, { rootMargin: NEAR_VIEWPORT })
    : null;

  function claim(el) {
    if (el.dataset.imgqClaimed || !sourceOf(el)) return;
    el.dataset.imgqClaimed = "1";
    if (observer) observer.observe(el); else { queue.push(el); pump(); }
  }

  function scan(root) {
    if (!root) return;
    if (root.tagName === "IMG" || root.tagName === "VIDEO") { claim(root); return; }
    if (root.querySelectorAll) root.querySelectorAll("img[data-src], video[data-poster]").forEach(claim);
  }

  // Catches every dynamically-added card: gallery infinite scroll, the recents strip, an artist's grid, and the
  // <video> media.js swaps in for a clip.
  new MutationObserver(muts => {
    for (const m of muts) for (const n of m.addedNodes) if (n.nodeType === 1) scan(n);
  }).observe(document.documentElement, { childList: true, subtree: true });

  if (document.readyState === "loading")
    document.addEventListener("DOMContentLoaded", () => scan(document.body));
  else
    scan(document.body);
})();
