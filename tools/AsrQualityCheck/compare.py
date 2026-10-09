"""Offline regression gate: identical audio, fewer errors, more exact hotwords.

python compare.py baseline.jsonl candidate.jsonl
Synthesized fixtures measure regressions, not real-user recognition accuracy.
"""
import collections
import json
import pathlib
import sys
import unicodedata

sys.stdout.reconfigure(encoding="utf-8")


def read(path):
    rows = [json.loads(line) for line in pathlib.Path(path).read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    assert rows and len({r["name"] for r in rows}) == len(rows), "Missing or duplicate cases"
    return {r["name"]: r for r in rows}


def norm(text):
    return "".join(c for c in text if not c.isspace() and unicodedata.category(c)[0] not in "PZ")


def metrics(rows):
    result = {}
    for group in ("positive", "negative"):
        subset = [r for r in rows.values() if r["group"] == group]
        result[group + "_errors"] = sum(round(r["cer"] * len(norm(r["reference"]))) for r in subset)
        result[group + "_characters"] = sum(len(norm(r["reference"])) for r in subset)
    result["exact_hotwords"] = sum(sum(min(n, r["final"].count(w)) for w, n in collections.Counter(r["expectedWords"]).items()) for r in rows.values())
    result["malformed_final"] = sum("\ufffd" in r["final"] for r in rows.values())
    return result


baseline, candidate = map(read, sys.argv[1:3])
assert baseline.keys() == candidate.keys(), "Case sets differ"
for name, before in baseline.items():
    after = candidate[name]
    for field in ("reference", "expectedWords", "group", "samples", "profile"):
        assert before[field] == after[field], f"{name}: {field} differs"
    assert after["samples"] == after["acceptedSamples"], "Audio samples were lost"
    assert after["hotwords"] == 94 and after["skippedHotwords"] == 0, "Hotword coverage regression"
    assert after["paths"] == 4, "Production beam size changed"
    assert after.get("nativeRevision") == "a1-context-before-topk-finalize-v1", "Wrong native DLL"
old, new = metrics(baseline), metrics(candidate)
print(json.dumps({"baseline": old, "candidate": new}, ensure_ascii=False))
assert new["positive_errors"] < old["positive_errors"], "Positive sentence CER did not improve"
assert new["exact_hotwords"] > old["exact_hotwords"], "Exact hotword matches did not improve"
assert new["negative_errors"] <= old["negative_errors"], "Ordinary speech regressed"
assert new["malformed_final"] == 0, "Malformed final UTF-8 output"
print("PASS: fewer positive errors, more exact hotwords, no ordinary-speech regression")
