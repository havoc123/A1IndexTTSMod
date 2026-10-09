"""Summarize offline JSONL with exact lexicon matches and ordinary-speech controls.

Usage: python summarize.py path/to/results.jsonl [more-results.jsonl ...]
No third-party packages; syntheses are development fixtures, not user accuracy.
"""
import collections
import json
import pathlib
import sys
import unicodedata

sys.stdout.reconfigure(encoding="utf-8")


def normalize(text):
    return "".join(c for c in text if not c.isspace() and unicodedata.category(c)[0] not in "PZ")


lexicon = [line.strip() for line in (pathlib.Path(__file__).resolve().parents[2] / "config/asr-hotwords.zh-CN.txt").read_text(encoding="utf-8-sig").splitlines() if line.strip() and not line.startswith("#")]
for filename in sys.argv[1:]:
    rows = [json.loads(line) for line in pathlib.Path(filename).read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    result = {"file": pathlib.Path(filename).name, "cases": len(rows)}
    for group in ("positive", "negative"):
        subset = [row for row in rows if row.get("group") == group]
        total_chars = sum(len(normalize(row["reference"])) for row in subset)
        errors = sum(round(row["cer"] * len(normalize(row["reference"]))) for row in subset)
        result[group + "_cer"] = errors / total_chars if total_chars else None
    expected = matched = false_matches = 0
    for row in rows:
        words = collections.Counter(row.get("expectedWords") or [])
        expected += sum(words.values())
        matched += sum(min(count, row["final"].count(word)) for word, count in words.items())
        if row.get("group") == "negative":
            false_matches += sum(row["final"].count(word) for word in lexicon if word not in row["reference"])
    result.update(expected_words=expected, matched_words=matched, negative_false_hotwords=false_matches,
                  malformed_cases=sum("\ufffd" in row["final"] for row in rows),
                  rtf=sum(row["decodeMs"] for row in rows) / 1000 / (sum(row["samples"] for row in rows) / 16000),
                  max_stop_ms=max((row["stopMs"] for row in rows), default=0))
    print(json.dumps(result, ensure_ascii=False))
    for row in rows:
        if row.get("group") == "positive":
            print(json.dumps({"case": row["name"], "cer": row["cer"], "final": row["final"]}, ensure_ascii=False))
