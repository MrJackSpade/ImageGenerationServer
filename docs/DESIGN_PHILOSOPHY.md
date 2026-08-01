# Make a Picture — Design Philosophy ("Studio Dark")

This document describes the visual language of the app in plain language, so it can direct the design
of new pages and revisions. It is the source of truth: when a new screen is built, it should be
checkable against this document. No code here — principles, vocabulary, and rules.

---

## 1. North star

**Studio Dark is a calm, professional creative tool.** It should feel like a serious piece of
2026-era software for making images — closer to a darkroom or a film-grading suite than a toy or a
consumer app. The work (the images) is the star; the interface is the quiet, confident frame around
it.

Three feelings to protect, in order:
1. **Image-forward.** The pictures are the brightest, most saturated thing on screen. Everything
   else recedes so they pop.
2. **Composed, not cluttered.** The app does a lot (compose, browse, edit, bookmark, tune models),
   but it should never feel busy. Structure and restraint carry the complexity.
3. **Capable.** It should read as a tool an enthusiast trusts — precise, legible, a little technical
   — without being cold or intimidating.

**What it is not:** not the old warm sage/paper minimalism (too soft to carry this much), not playful
or loud, not glassy/ambient, not a dense terminal. If a change makes the app feel cute, busy, or
generic-SaaS, it's wrong.

---

## 2. Color

The palette is a **dark, layered greyscale with a single violet accent and a cyan support color.**
Color is used sparingly and meaningfully — never decoratively.

### Surfaces (darkest → lightest), think of them as depth

| Role | Value | Where it's used |
| --- | --- | --- |
| **Canvas** | `#0b0c10` | The page background — the deepest layer, behind everything. |
| **Panel** | `#14161c` | Cards, composers, modals — the main "things" sitting on the canvas. |
| **Elevated** | `#1c1f27` | Inputs, raised controls, popovers, image placeholders — surfaces *on top of* panels, or that you reach into (type/select). |
| **Rail** | `#0f1116` | The left navigation rail — slightly darker than canvas, to anchor the edge. |

The rule: **deeper = further back, lighter = closer / interactive.** An input is lighter than the
panel it sits in because you act on it. Don't flatten these into one grey.

### Lines & borders

| Role | Value | Use |
| --- | --- | --- |
| **Hairline** | `#262a34` | The default 1px border that defines almost every surface. Structure comes from these thin lines, not from heavy shadows. |
| **Stronger line** | `#30353f` | Slightly more present borders for popovers/floating elements that need to separate from a dark background. |

### Text (ink), three levels

| Role | Value | Use |
| --- | --- | --- |
| **Ink** | `#e7e9ee` | Primary text: prompts, headings, values that matter. |
| **Ink-soft** | `#8b909d` | Secondary text: labels, captions, helper lines, inactive nav. |
| **Ink-faint** | `#5b606c` | Tertiary: metadata, breadcrumbs, hints, the quietest details. |

Never use pure white (`#fff`) for body text — it's harsh on dark. White is reserved for text *on top
of the violet accent* (e.g. inside a primary button).

### Accent & semantics — use with restraint

| Role | Value | Use |
| --- | --- | --- |
| **Accent (violet)** | `#8b5cf6` | THE action/selection color. Primary buttons, the active nav item, focus rings, selected segments, the bookmarked state, progress. |
| **Accent-hi** | `#a78bfa` | Hover/brighter violet, and text links. |
| **Support (cyan)** | `#22d3ee` | A secondary highlight, used almost exclusively for **metadata accents** — model tags, the `#`/`@` markers in autocomplete, small technical highlights. It is the "data" color, not a second action color. |
| **Danger** | `#f0616d` | Destructive/cancel only (Cancel a generation, Delete). |
| **Good** | `#34d399` | Success/healthy status (rare). |
| **Warn** | `#f59e0b` | Cautions (e.g. "adult content: limited"). |

**The accent discipline is the most important color rule.** Violet means "this is the primary action,
the current thing, or your choice." If everything is violet, nothing is. A screen should usually have
**one** violet primary button. Cyan never competes with violet for attention — it's for quiet
technical labels.

---

## 3. Typography

Two typefaces, two jobs.

- **Sans-serif (system "Inter"-style stack)** — all human-facing UI: prompts, headings, buttons,
  body, captions. Clean, neutral, modern.
- **Monospace** — all **machine/metadata**: model names on cards, seeds, dates, file/route paths,
  breadcrumbs, the debug log, tag counts. Monospace is a signal: "this is data." It also aligns
  numbers and gives the tool its precise feel. Use it deliberately, not for prose.

Conventions:
- **Micro-labels are UPPERCASE**, small (~11px), letter-spaced, in ink-faint. They label a field or a
  section ("PROMPT", "STYLE", "RECENT", "ARTISTS"). They are quiet signposts, not headings.
- **Section headings** are small and understated, not big and bold — the images provide the visual
  weight, so headings stay out of the way.
- **Prompts/values** are the largest comfortable text in a given context (the prompt textarea is
  ~18px) because that's the content.
- Weights: regular for body, 600–650 for buttons/emphasis. Avoid heavy/black weights (that's the
  brutalist direction, not this one).

---

## 4. Shape, depth & space

- **Corners:** a consistent soft radius (~12px) on panels, cards, inputs, modals; full pills (999px)
  on buttons, chips, and segmented controls. Nothing is sharp-cornered; nothing is a perfect circle
  except avatars and small status dots.
- **Borders over shadows for structure.** Almost everything is defined by a 1px hairline. This keeps
  the UI flat and calm.
- **Shadows mean genuine elevation** — only true floating things get a soft, dark shadow: cards,
  modals, popovers, the sticky composer. A shadow says "this is above the page." Don't shadow
  everything; it muddies the depth.
- **Spacing rhythm** is tight and consistent (a 4/8px feel): controls are compact and close. But give
  **breathing room** around the two things that deserve it — the prompt input and the gaps between
  major sections. Dense where it's controls, generous where it's content or focus.

---

## 5. Motion

Subtle and functional, never showy.

- **Hover lift:** interactive cards/thumbnails rise a couple of pixels and gain a violet border on
  hover. This is the main "this is clickable" cue.
- **Focus:** inputs adopt a violet border on focus (no glow halos).
- **Progress:** a thin violet→cyan gradient bar; long generations also drive the browser tab title
  and a small favicon ring so a backgrounded tab shows it's working.
- **Entrances:** cards and results fade/rise in gently (~0.25–0.35s). 
- Transitions are quick (0.12–0.18s). Nothing bounces, spins, or pulses for decoration.

---

## 6. Layout system

Every signed-in screen sits inside the same frame:

### The rail (left, ~60px, persistent)
A thin vertical strip of icon buttons: a gradient logo mark at top, then the primary destinations —
**New** (compose), **History** (gallery), **Bookmarks** — a flexible spacer, and **sign out** at the
bottom. The current section's icon is highlighted with a violet-tinted background. The rail is the
app's spine; it's always there and never scrolls away. New top-level destinations go here.

### The top bar (sticky, per content area)
Holds the **brand** ("Make a Picture", links home), a **breadcrumb** (the current section name, in
faint monospace), and the **user identity** (name + avatar) on the right. It's sticky with a subtle
blur so it stays available as you scroll. It carries identity and orientation, not actions.

### The content column
Content is centered in a column (max ~1080px) with comfortable side padding. Two widths:
- **Focused / single-flow views** (compose, image detail, the edit conversation) are constrained
  narrower (~720px) and centered — one clear column of attention.
- **Browse / grid views** (gallery, bookmarks) use the **full** column width so the image grid
  breathes and shows more per row.

### Auth pages (signed out)
No rail, no top bar. A single centered card floats on a subtly violet-tinted radial backdrop. Entry
should feel quiet and focused.

### Responsive
The rail stays (it's already thin). On narrow screens the two-column composer collapses to one
column, and grids reflow (they're fluid auto-fill). Mobile is a first-class case — it's used on
phones — so nothing should depend on hover alone, and tap targets stay generous.

---

## 7. The component vocabulary

These are the reusable parts. New pages should compose from these, not invent new ones.

### Surfaces
- **Panel** — the default container for a group of controls or content (a composer, a meta block).
  Panel background, hairline border, soft radius, optional shadow if it floats.
- **Popover / floating** — autocomplete, the count picker. Elevated background, stronger border,
  shadow. Appears on demand, dismisses on outside tap.

### The image-card — the atomic unit of the app
Anywhere images are listed (gallery, the Recent strip, bookmarks, filtered results), they appear as
**cards**, not bare cropped squares:
- a **square image** on top,
- a **meta footer** below it: the **prompt** (clamped to two lines, primary ink), and a **row** with
  a **model tag** (cyan monospace pill) and an optional faint monospace detail (date/seed).
- **Hover:** lift + violet border. **Just-generated** cards get a violet glow ring so new work stands
  out from history.

Cards live in a **card grid** that auto-fills columns at a comfortable thumbnail size (~190px; the
Recent strip is a touch denser). The card is what makes the app feel like a gallery of *labeled
work* rather than a contact sheet.

### Forms & inputs
- The unit is a **field**: an uppercase micro-label above its control.
- Inputs/selects use the elevated surface with a hairline border and a violet focus border.
- The **composer** is the signature form: a **two-column panel** — the prompt (and its inline help)
  on the left, a stacked **controls column** on the right (style, shape, options, the primary action
  at the bottom). Group secondary controls in the side column; keep the left for the primary input.

### Buttons & actions (a clear hierarchy)
- **Primary:** a violet gradient **pill** with a soft glow — exactly one per view, the main action
  (Generate, Sign in). White text.
- **Segmented control:** a row of pills where the active one is solid violet (Simple/Advanced,
  the shape selector).
- **Link-button:** text-only, accent-colored, with a faint hover background — for everything
  secondary (Back, More →, Edit, bookmark toggles, footer tools). Most actions are link-buttons.
- **Destructive / cancel:** solid danger red, and only when it's truly destructive or stops work.

**Enter never starts work.** In a prompt box Enter inserts a newline and nothing else — the button is
the only way to generate, apply or send. A prompt is prose that wants paragraphs, and a key that
submits it is a key that submits it half-written. Nothing should need a "press Enter to…" hint,
because there is nothing to learn.

### Chips
Prompt tokens render as **chips**: plain text for ordinary words, an outlined **tag** chip, a
violet-outlined **artist** chip, and a solid-violet **bookmarked** chip (with a gold star). Chips are
how the app exposes "this token is meaningful and you can act on it."

### Status & progress
A single status line (centered, soft ink; red when it's an error) plus the thin gradient progress
bar. Inside the edit/refine conversation, progress shows inside the working bubble instead.

### Conversation bubbles (edit & simple-refine)
A chat layout: the **user's message** is a violet bubble aligned right; the **AI's image** is a
panel-card bubble aligned left. A sticky composer sits at the bottom. This pattern is reserved for
the iterative edit/refine flows.

### Modals & slide-up panels
- **Modal:** a panel-card centered on a dimmed overlay (the batch-count picker). Backdrop click
  dismisses.
- **Utility panels** (debug log, prewarm): slide up from the bottom, near-black, monospace, with a
  violet top edge. These are "under the hood" tools and look the part.

### Empty states
A centered, soft-ink message with a clear next step (e.g. "No images yet. Make one →"). Never a blank
screen.

### Toast
A small pill at bottom-center for transient confirmations (bookmark saved, imported N). Elevated,
auto-dismissing.

---

## 8. Voice & microcopy

The words are part of the design. Keep them **plain, calm, and lowercase-leaning.**
- Prefer everyday language over jargon ("Make a Picture", "Describe what you want", "Edit a photo").
- Helper text is quiet and useful, not salesy ("Nothing here yet — make one →").
- Labels are short nouns ("Style", "Shape", "Prompt").
- Errors name the real culprit and are not alarming ("Can't reach the gateway on :5079 — is
  ForgeGateway running?").
- No exclamation marks, hype, or emoji-as-decoration. A few functional glyphs (★ ✎ ◻ ▭ ▯) are fine.

---

## 9. Recipe for a new page

When adding a screen, follow this so it belongs:

1. **Frame:** it renders inside the rail + top bar automatically. Set its **breadcrumb** (the page
   title) so the top bar reads correctly. If it's a new top-level destination, add it to the rail.
2. **Width:** focused single flow → the narrow (~720px) centered column; a browse/grid → the full
   column.
3. **Build from the vocabulary:** panels for grouped controls, the **field** pattern for inputs,
   **image-cards in a card grid** for any image collection, link-buttons for secondary actions, and
   **one** violet primary button for the main action.
4. **Section headers:** small uppercase micro-label, left; an optional link-button action on the
   right (the "Recent / More →" pattern).
5. **Metadata in monospace + faint ink; prompts/values in primary ink; supporting text in soft ink.**
6. **Color check:** is there exactly one violet primary action? Is violet only on action/selection/
   focus/active-nav? Is cyan only on metadata? Are surfaces layered (canvas < panel < elevated)?
7. **Empty & loading states** designed, not afterthoughts.
8. **Works on a phone** (no hover-only affordances, generous targets, reflowing grid).

---

## 10. Quick do / don't

**Do**
- Let images be the brightest thing; keep chrome quiet.
- Define structure with hairline borders; reserve shadows for things that truly float.
- Keep one clear primary action per view.
- Use monospace to mark data, uppercase micro-labels to mark fields/sections.
- Layer your surfaces by depth.

**Don't**
- Don't spread the violet accent around for decoration, or introduce new accent colors.
- Don't use pure white text, heavy drop shadows, or big bold headings.
- Don't put bare cropped thumbnails in a grid — use image-cards with a meta footer.
- Don't crowd the prompt or the spaces between sections.
- Don't add motion/decoration that doesn't communicate state.

---

*Reference implementation: `src/ImageGen.Web/wwwroot/css/app.css` (the variables at the top are the
canonical palette) and the Razor views under `src/ImageGen.Web/Views`. The original direction was
chosen from the five mockups in `design/` — `design/01-studio-dark.html` is the seed of this system.*
