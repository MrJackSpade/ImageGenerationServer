#!/usr/bin/env python
"""Export the s2srec2 tag model to the artifacts the C# app loads at runtime.

This is a BUILD-TIME tool. It is the only thing in this repo that needs PyTorch, and it is not needed to run the
application -- the app loads the files this produces through ONNX Runtime and never sees a .pt. Run it when the
checkpoint changes, publish the outputs, and the app consumes them from a download.

    python tools/export-tagmodel-onnx.py --checkpoint tag_s2srec2.pt --vocab vocab_s2srec2.json --out artifacts/

Outputs
-------
  tag_s2srec2.onnx        the graph. (ids, pad_mask, tmask) -> (logits over the emittable tags, completeness logit)
  tag_s2srec2.onnx.data   its external weights, ~870 MB
  out_ids.bin             int32 little-endian, one per decoder row, mapping row -> vocab id
  manifest.json           sizes + SHA256 of each file, so a download can be verified

WHY out_ids IS A SEPARATE FILE. The decoder emits only the ~232k tags worth suggesting, not the whole ~639k vocab,
so its output is indexed by decoder ROW. Every consumer indexes by VOCAB id. Python bridged the two by calling
model.logits_vocab() after each run, which is a scatter using the checkpoint's own `out_ids` array -- data that lives
inside the .pt and is therefore unreachable to anything that does not load PyTorch. Dumping it once turns the last
PyTorch dependency into a 900 KB file.

The graph itself is exported EXACTLY as tagmodel/server.py always did (same three inputs, same opset), so the model
the app runs is the model that has been serving, not a re-export that might differ.
"""
import argparse
import hashlib
import json
import os
import sys

DEFAULT_OPSET = 17


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--checkpoint", default="tag_s2srec2.pt")
    ap.add_argument("--vocab", default="vocab_s2srec2.json")
    ap.add_argument("--out", default="artifacts")
    ap.add_argument("--model-dir", default=None,
                    help="directory holding the s2srec2/ package (default: the checkpoint's directory)")
    ap.add_argument("--skip-onnx", action="store_true",
                    help="only dump out_ids.bin + manifest.json, reusing an existing .onnx next to the checkpoint")
    args = ap.parse_args()

    ckpt = os.path.abspath(args.checkpoint)
    model_dir = os.path.abspath(args.model_dir or os.path.dirname(ckpt))
    sys.path.insert(0, model_dir)

    import torch
    from s2srec2.infer import load_model
    from s2srec2.typemask import ALL_TYPES, mask_tensor
    from s2srec2._imports import Vocab, keep_tag

    os.makedirs(args.out, exist_ok=True)
    vocab_path = args.vocab if os.path.isabs(args.vocab) else os.path.join(model_dir, args.vocab)

    print(f"loading vocab   {vocab_path}", flush=True)
    vocab = Vocab.load(vocab_path)
    print(f"loading model   {ckpt}", flush=True)
    model, cfg = load_model(ckpt, vocab, "cpu")

    # The C# port assumes the single-head path, which is what the shipped checkpoint uses and what makes ONNX
    # sufficient: with a dual head, scoring runs forward_display() and the exported graph would not be the whole
    # model. Fail loudly rather than export a graph that silently answers differently from the Python server.
    if getattr(cfg, "dual_head", False):
        sys.exit("this checkpoint has dual_head=True; the exported graph is not the full scoring path for it")

    head = model.head
    if head is None:
        sys.exit("this checkpoint has no output head, so out_ids does not exist and no scatter is needed")

    onnx_path = os.path.join(args.out, "tag_s2srec2.onnx")
    if args.skip_onnx:
        src = os.path.splitext(ckpt)[0] + ".onnx"
        print(f"reusing         {src}", flush=True)
        onnx_path = src
    else:
        print(f"exporting ONNX  {onnx_path}", flush=True)
        # Identical call to the one tagmodel/server.py used, so the graph is byte-for-byte the serving graph.
        torch.onnx.export(
            model,
            (torch.tensor([[0, 1, 2]]), torch.zeros((1, 3), dtype=torch.bool), mask_tensor(ALL_TYPES, 1, "cpu")),
            onnx_path,
            input_names=["ids", "pad_mask", "tmask"],
            output_names=["logits", "p_logit"],
            dynamic_axes={"ids": {1: "n"}, "pad_mask": {1: "n"}},
            opset_version=DEFAULT_OPSET,
        )

    # int32 is enough: vocab ids max out around 639k. Little-endian, no header -- the C# side knows the element
    # width and reads the count from the file length, so there is no format to keep in sync.
    out_ids = head.out_ids.astype("<i4")
    out_ids_path = os.path.join(args.out, "out_ids.bin")
    out_ids.tofile(out_ids_path)
    print(f"wrote           {out_ids_path}  ({out_ids.size:,} rows -> vocab ids)", flush=True)

    # The junk filter (bad_id, *_request, tagme, ...) as vocab IDS rather than 200 lines of curated tag names.
    # It is DERIVED -- vocab names run through cvae/tagfilter.keep_tag -- so shipping the ids keeps the one
    # authoritative copy of the list in Python instead of a C# transcription that could rot against it.
    #
    # Expect this to be EMPTY: a vocab compiled with the current filter contains no junk by construction. It exists
    # because "usually empty" is not "always empty" -- an older vocab, or a filter updated after the vocab was built,
    # would put entries here, and the app must exclude them rather than start suggesting bad_id.
    import numpy as np
    junk = np.asarray([i for i, t in enumerate(vocab.tags) if not keep_tag(t)], dtype="<i4")
    junk_path = os.path.join(args.out, "junk_ids.bin")
    junk.tofile(junk_path)
    print(f"wrote           {junk_path}  ({junk.size:,} junk ids)", flush=True)

    manifest = {
        "vocab_size": len(vocab),
        "out_head_size": int(head.size),
        "opset": DEFAULT_OPSET,
        "dual_head": False,
        "files": {},
    }
    candidates = [onnx_path, onnx_path + ".data", out_ids_path, junk_path, vocab_path,
                  os.path.join(model_dir, "calibration.json"),
                  os.path.join(model_dir, "calibration.no_artist.json")]
    for path in candidates:
        if not os.path.exists(path):
            continue
        manifest["files"][os.path.basename(path)] = {"bytes": os.path.getsize(path), "sha256": sha256(path)}
        print(f"hashed          {os.path.basename(path)}", flush=True)

    manifest_path = os.path.join(args.out, "manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    print(f"wrote           {manifest_path}", flush=True)
    print(f"\nvocab {manifest['vocab_size']:,} | out_head {manifest['out_head_size']:,}", flush=True)


if __name__ == "__main__":
    main()
