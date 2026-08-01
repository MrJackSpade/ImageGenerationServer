// Bookmarks page: card/chip controls. Remove a starred tag (chip) or artist (card) via its × button, and pin/unpin
// an artist card via its 📌 button. Delegated so it covers every shape; the host carries data-name/data-kind.
// After a pin toggle we reload so the server re-renders the Pinned vs Artists rows in their canonical order.
// Also the collapse state of every section, persisted to the user's account. Uses core.js.
(function () {
  document.addEventListener("click", async (e) => {
    const pin = e.target.closest(".card-pin");
    if (pin) {
      e.preventDefault(); e.stopPropagation();
      const host = pin.closest("[data-name][data-kind]");
      if (!host) return;
      const pinned = !host.classList.contains("is-pinned");
      try {
        const r = await postTokenPin(host.dataset.name, host.dataset.kind, pinned);
        if (!r.ok) throw new Error(r.status);
        location.reload();
      } catch (_) { toast("Couldn't update pin"); }
      return;
    }

    const x = e.target.closest(".tc-x, .card-x");
    if (!x) return;
    e.preventDefault(); e.stopPropagation();
    const host = x.closest("[data-name][data-kind]");
    if (!host) return;
    try { await deleteToken(host.dataset.name, host.dataset.kind); host.remove(); toast("Removed bookmark"); }
    catch (_) { toast("Couldn't remove"); }
  });

  // Collapse/expand a section by clicking its header — a whole category, or one KIND within it (artists / tags /
  // images), each folding independently. Keys are composite and come from the markup: "<category>" for the category
  // and "<category>/<kind>" for a sub-section, where a category is "__global__" or its title. Titles are all a
  // category has (there is no id behind one), so renaming a category drops its saved fold state.
  //
  // The set lives on the USER'S ACCOUNT, not in localStorage: fold state that lives in one browser is invisible to
  // every other device, and sub-headers multiply the state rather than adding one entry to it. Same pattern as the
  // gen page's composerPrefs — an opaque JSON blob on its own route, so this autosave can't clobber another's.
  const SECTION = ".bm-group[data-group-key], .bm-kind[data-group-key]";
  let collapsed = new Set();
  let prefsLoaded = false;   // gates every write: saving before the read lands would erase the stored set

  function apply() {
    document.querySelectorAll(SECTION).forEach(section => {
      section.classList.toggle("collapsed", collapsed.has(section.dataset.groupKey));
    });
  }

  document.addEventListener("click", (e) => {
    const h = e.target.closest(".bm-toggle");
    if (!h) return;
    const section = h.closest(SECTION);
    if (!section) return;
    const key = section.dataset.groupKey;
    if (section.classList.toggle("collapsed")) collapsed.add(key); else collapsed.delete(key);
    if (!prefsLoaded) return;
    saveBookmarkPrefs(JSON.stringify({ collapsed: [...collapsed] })).catch(() => toast("Couldn't save that"));
  });

  (async () => {
    // A failed read must NOT be treated as "nothing is folded" — that reads as an empty set and the first toggle
    // would write it back over the real one. Leave the page expanded and leave the stored set alone.
    let s; try { s = await fetchSettings(); } catch (_) { return; }
    if (s && s.bookmarkPrefs) {
      try { collapsed = new Set(JSON.parse(s.bookmarkPrefs).collapsed || []); }
      catch (_) { return; }   // stored blob is unreadable: don't overwrite it with a guess
    }
    prefsLoaded = true;   // nothing stored yet is a legitimate empty set — a first save creates it
    apply();
  })();

  // Press-and-hold (or right-click) a bookmark control to change which categories it's filed under. Reuses the shared
  // dialog from bookmarkCategories.js; a save reloads so the server re-renders the Global/category grouping.
  // The trigger goes on the chip — the control you click to bookmark. Nothing on the artist card is one: the preview
  // and the name are links through to the artist page, so the card gets no long-press at all (categorise an artist
  // from its chip).
  document.addEventListener("DOMContentLoaded", () => {
    if (!window.attachCategoryLongPress) return;
    document.querySelectorAll(".tagchip[data-name][data-kind]").forEach(el => {
      window.attachCategoryLongPress(el, () => ({
        scope: "token", name: el.dataset.name, kind: el.dataset.kind, onSaved: () => location.reload(),
      }));
    });
    document.querySelectorAll("[data-image-record]").forEach(el => {
      let rec; try { rec = JSON.parse(el.dataset.imageRecord); } catch (_) { return; }
      window.attachCategoryLongPress(el, () => ({
        scope: "image", id: rec.id, record: rec, onSaved: () => location.reload(),
      }));
    });
  });
})();
