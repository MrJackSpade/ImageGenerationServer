"""Regenerate docs/MODELS.md from configurations/workflows/.

The catalogue is the only place that knows what ships, so the model list is generated from it rather than
maintained by hand -- a hand-written list is stale the first time a configuration is added.

    python tools/gen-models-doc.py

Categories are inferred, because a configuration does not declare one: a video workflow is the one with
frame parameters, an editing workflow takes a source image, an effect declares effect_type, and whatever is
left is text-to-image.
"""
import glob
import io
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
CATALOG = os.path.join(ROOT, "configurations", "workflows")
OUT = os.path.join(ROOT, "docs", "MODELS.md")

VIDEO_PARAMS = {"fps", "length"}
# Matched against the WORKFLOW id, not the card. A card's edit_use_cases describes what the model can do in
# general and is shared by every configuration that binds it, so it says nothing about which one this is.
EDIT_WORDS = ("edit", "inpaint", "outpaint", "redraw", "upscale", "matte", "refine", "kontext")


def category(cfg):
    """Which section a configuration belongs in. Order matters -- effects win over their base model."""
    if cfg.get("effect_type"):
        return "effects"
    params = cfg.get("params") or {}
    workflow = cfg.get("workflow", "")
    if VIDEO_PARAMS & set(params) or re.search(r"(^|-)(i2v|t2v|video)", workflow):
        return "video"
    if cfg.get("edit_group") or any(w in workflow for w in EDIT_WORDS):
        return "editing"
    return "text-to-image"


def speaks_tags(cfg):
    """True when the model is booru-trained, which is what turns on tag autocomplete and the markers."""
    return bool((cfg.get("card") or {}).get("tagging"))


def first_sentence(text, limit=200):
    if not text:
        return ""
    text = " ".join(text.split())
    # Split on sentence ends only. Splitting on ';' too used to cut summaries mid-clause.
    cut = re.split(r"(?<=[.!?])\s", text)[0]
    if len(cut) > limit:
        cut = cut[:limit].rsplit(" ", 1)[0] + "…"
    return cut


def main():
    # One file per configuration; the two monolithic catalogue files were retired with the split.
    configs = [json.load(io.open(f, encoding="utf-8"))
               for f in sorted(glob.glob(os.path.join(CATALOG, "*.json")))]

    # Collapse configurations to models. Several configurations are settings variants of one model (a Turbo
    # preset, a 720p preset, a bf16 test), and a reader wants the model, not the preset.
    models = {}
    for cfg in configs:
        if cfg.get("ui_visible") is False and cfg.get("api_visible") is False:
            continue
        card = cfg.get("card") or {}
        name = cfg.get("friendly_name") or cfg.get("id")
        key = (category(cfg), name)
        entry = models.setdefault(key, {"variants": 0, "arch": "", "summary": "", "tags": False})
        entry["variants"] += 1
        entry["tags"] = entry["tags"] or speaks_tags(cfg)
        if not entry["arch"]:
            entry["arch"] = first_sentence(card.get("architecture"), 150)
        if not entry["summary"]:
            entry["summary"] = first_sentence(card.get("summary"), 200)

    sections = [
        ("text-to-image", "Text to image", "Type a prompt, get a picture."),
        ("editing", "Editing, inpaint and outpaint",
         "Take an existing image and change it. Multi-turn: each edit builds on the last."),
        ("video", "Video", "Animate a still, or generate a clip from a prompt."),
        ("effects", "Effects and post-processing",
         "Applied to an image or a clip you already have. Several need no diffusion model at all."),
    ]

    total = sum(m["variants"] for m in models.values())
    distinct = len({name for _, name in models})
    lines = [
        "# Supported models",
        "",
        f"**{distinct} models, {total} presets.** Generated from `configurations/workflows/` by "
        "`tools/gen-models-doc.py` — edit the catalogue, not this file.",
        "",
        "A model can appear in more than one section: the same weights that generate a picture often also "
        "redraw one or drive an effect.",
        "",
        "A model appears in the app once you have pointed its slots at files on your disk — most are "
        "recognised automatically, the rest are bound on the models page.",
        "",
        "🏷 marks a **booru-tagged** model — the ones with tag autocomplete, `#tag` / `@artist` markers and "
        "tag bans. Every other model takes an ordinary sentence.",
        "",
    ]

    for key, title, blurb in sections:
        rows = sorted(((n, m) for (c, n), m in models.items() if c == key), key=lambda r: r[0].lower())
        if not rows:
            continue
        lines += [f"## {title}", "", blurb, "", "| Model | What it is |", "| --- | --- |"]
        for name, m in rows:
            label = f"**{name}**" + (" 🏷" if m["tags"] else "")
            if m["variants"] > 1:
                label += f" ×{m['variants']}"
            detail = m["summary"] or m["arch"] or ""
            lines.append(f"| {label} | {detail.replace('|', '\\|')} |")
        lines.append("")

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with io.open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines))
    print(f"wrote {OUT}: {len(models)} models, {total} presets")


if __name__ == "__main__":
    main()
