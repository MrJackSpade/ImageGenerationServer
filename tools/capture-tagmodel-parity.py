#!/usr/bin/env python
"""Record the Python tag server's answers, so the C# port can be held to them after the server is gone.

Run this against a LIVE tagmodel/server.py (default http://127.0.0.1:8000) BEFORE deleting it. The output is committed
as tests/ImageGen.Tests/tagmodel-parity.json and read by TagModelParityTests.

    python tools/capture-tagmodel-parity.py --url http://127.0.0.1:8000 --out tests/ImageGen.Tests/tagmodel-parity.json

Only DETERMINISTIC behaviour is captured. Suggest is fully determined by the model given (tags, q, k). Generation is
captured at temp=0 only, where it is greedy and therefore reproducible; sampled generation drew from PyTorch's
process-wide RNG and never repeated even on the server that produced it, so recording it would pin noise.
"""
import argparse
import json
import urllib.parse
import urllib.request

# Cases chosen to exercise the branches that differ: empty vs non-empty context, fragment vs no fragment, a fragment
# that matches an artist (the '@' path), a rare tag, and a multi-tag context where conditioning actually bites.
SUGGEST_CASES = [
    {"tags": [], "q": "", "k": 10},
    {"tags": [], "q": "hair", "k": 10},
    {"tags": ["1girl"], "q": "", "k": 15},
    {"tags": ["1girl", "solo"], "q": "", "k": 20},
    {"tags": ["1girl", "solo", "long_hair"], "q": "blue", "k": 12},
    {"tags": ["hatsune_miku"], "q": "", "k": 10},
    {"tags": ["hatsune_miku", "vocaloid"], "q": "twin", "k": 8},
    {"tags": ["landscape", "no_humans"], "q": "", "k": 10},
    {"tags": ["1boy", "armor"], "q": "sword", "k": 10},
    {"tags": ["traditional_media"], "q": "pencil", "k": 6},
]

# temp=0 is greedy and therefore reproducible. The type lists deliberately vary, including one that names every
# droppable category (equivalent to "all") and one that is deeply restrictive.
GREEDY_CASES = [
    {"seed": ["1girl"], "types": ["general", "character", "copyright", "meta"]},
    {"seed": ["1girl", "solo"], "types": ["general", "character", "copyright", "meta"]},
    {"seed": [], "types": ["general", "character", "copyright", "meta"]},
    {"seed": ["landscape"], "types": ["general", "meta"]},
    {"seed": ["hatsune_miku"], "types": ["general", "character", "copyright", "meta"]},
    {"seed": ["1boy", "armor"], "types": ["general"]},
    {"seed": ["1girl"], "types": ["general", "artist", "character", "copyright", "meta"]},
]


def get(url, path, params):
    query = urllib.parse.urlencode(params)
    with urllib.request.urlopen(f"{url}{path}?{query}", timeout=300) as response:
        return json.loads(response.read().decode("utf-8"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:8000")
    ap.add_argument("--out", default="tests/ImageGen.Tests/tagmodel-parity.json")
    args = ap.parse_args()
    url = args.url.rstrip("/")

    health = get(url, "/api/health", {})
    print(f"server: vocab {health['vocab']:,} out_head {health['out_head']:,} dual_head={health['dual_head']}")
    if health["dual_head"]:
        raise SystemExit("this server has dual_head=True; the exported ONNX graph is not its full scoring path")

    snapshot = {"health": health, "suggest": [], "greedy": []}

    for case in SUGGEST_CASES:
        body = get(url, "/api/suggest", {
            "tags": ",".join(case["tags"]), "q": case["q"], "k": case["k"], "mode": "likely"})
        snapshot["suggest"].append({**case, "results": body["results"], "total": body["total"]})
        print(f"suggest tags={case['tags']} q='{case['q']}' -> {len(body['results'])} results")

    for case in GREEDY_CASES:
        body = get(url, "/api/random_prompt", {
            "seed": ",".join(case["seed"]), "types": ",".join(case["types"]),
            "temp": 0, "n": 1})
        snapshot["greedy"].append({
            **case,
            "tags": body["prompts"][0],
            "stop_reason": body["stop_reasons"][0],
        })
        print(f"greedy  seed={case['seed']} types={case['types']} -> {len(body['prompts'][0])} tags "
              f"({body['stop_reasons'][0]})")

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(snapshot, f, indent=2)
    print(f"\nwrote {args.out}")


if __name__ == "__main__":
    main()
