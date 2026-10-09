"""Expand offline AI-authored performance decisions into a standalone preset library.

This script assembles decisions; it does not classify text with keywords or call a model.
The source tables are local game-asset extracts and are not included in the output.
"""
import argparse
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tables", type=Path, default=ROOT / ".state/prompt-analysis-20261008")
    args = parser.parse_args()
    decisions = read(ROOT / "config/preset-voice-styles.authoring.json")
    for name, expected in decisions["source_sha256"].items():
        if hashlib.sha256((args.tables / name).read_bytes()).hexdigest() != expected:
            raise ValueError(f"Source snapshot changed; re-author decisions before generating: {name}")
    base = read(args.tables / "tbnpcbasecfg.json")
    topics = read(args.tables / "tbnpctopicchat.json")
    persuade_topics = read(args.tables / "tbnpcpersuadetopic.json")
    personas = read(args.tables / "tbnpcaipersona.json")
    by_npc = {r["id"]: r for r in base}
    by_persona = {r["npcId"]: r for r in personas}
    backgrounds = {}
    for topic in topics:
        backgrounds.setdefault(topic["npcId"], []).append(topic.get("backgroundPrompt", {}).get("zh-Hans", ""))
    for topic in persuade_topics:
        backgrounds.setdefault(topic["npcId"], []).append(topic.get("backgroundPrompt", {}).get("zh-Hans", ""))
    cards = list(dict.fromkeys(r.get("speakStyle", {}).get("zh-Hans", "").strip() for r in personas))
    greeting_texts = list(dict.fromkeys(g.get("zh-Hans", "").strip() for r in base for g in r.get("greetings", []) if g.get("zh-Hans", "").strip()))
    records = []
    character_styles = {}

    def get_character(npc_id):
        npc = by_npc.get(npc_id, {})
        card = by_persona.get(npc_id, {}).get("speakStyle", {}).get("zh-Hans", "").strip()
        if card:
            code = f"C{cards.index(card):03}"
            actor_key = decisions["card_assignments"][code]
        else:
            code = None
            desc = npc.get("PersonalityDesc", "")
            actor_key = decisions["personality_assignments"].get(desc, "natural")
        actor = decisions["actors"][actor_key]
        overridden = str(npc_id) in decisions["character_overrides"]
        if overridden:
            actor_key = decisions["character_overrides"][str(npc_id)]
            actor = decisions["actors"][actor_key]
        character_styles[str(npc_id)] = {
            "npc_name": npc.get("npcName", {}).get("zh-Hans", str(npc_id)),
            "actor_profile": actor_key,
            "delivery": actor["delivery"],
            "card_sha256": sha(card) if card else None,
            "personality": npc.get("PersonalityDesc", ""),
            "topic_context_sha256": sha("\n".join(backgrounds[npc_id])) if npc_id in backgrounds else None,
            "basis": "topic_character_context" if overridden else "speakStyle" if card else "PersonalityDesc" if npc.get("PersonalityDesc") else "neutral_missing_card",
        }
        return npc, actor_key, actor

    def add(npc_id, source, source_id, text, performance, **metadata):
        npc, actor_key, actor = get_character(npc_id)
        tone = decisions["performances"][performance]
        tags = actor["emotion_tags"] if performance in ("natural", "intro") else tone["emotion_tags"]
        delivery = actor["delivery"] + "；" + tone["delivery"]
        record = {
            "key": f"{source}:{npc_id}:{source_id}",
            "npc_id": npc_id,
            "npc_name": character_styles[str(npc_id)]["npc_name"],
            "source": source,
            "source_id": source_id,
            "language": "zh-Hans",
            "text": text,
            "text_sha256": sha(text),
            "actor_profile": actor_key,
            "performance_profile": performance,
            "voice_style": {
                "emotion_tags": tags,
                "delivery": delivery,
                "intensity": tone["intensity"],
            },
            "style_source": "offline_ai_authored",
            **metadata,
        }
        # Pet cries and pure stage directions remain documented, never invent human dialogue.
        record["speech_policy"] = "nonhuman_or_narration" if npc.get("isPet") or performance in ("pet", "narration", "silence") else "speak"
        override = decisions.get("entry_overrides", {}).get(record["key"])
        if override:
            record["voice_style"] = override
        style = record["voice_style"]
        if not (1 <= len(style["emotion_tags"]) <= 3 and 0 <= style["intensity"] <= 1 and 0 < len(style["delivery"]) <= 80):
            raise ValueError(f"Performance exceeds the runtime style contract: {record['key']}")
        records.append(record)

    for npc in base:
        for index, greeting in enumerate(npc.get("greetings", [])):
            text = greeting.get("zh-Hans", "").strip()
            if not text:
                continue
            gid = f"G{greeting_texts.index(text):03}"
            add(npc["id"], "base_greeting", str(index), text, decisions["greeting_assignments"][gid])
    for index, topic in enumerate(topics):
        text = topic.get("firstNpcMessage", {}).get("zh-Hans", "").strip()
        if not text:
            continue
        tid = f"T{index:03}"
        performance = decisions["topic_assignments"][tid]
        background = topic.get("backgroundPrompt", {}).get("zh-Hans", "")
        add(topic["npcId"], "topic_opening", str(topic["id"]), text, performance,
            topic_name=topic["topicName"].get("zh-Hans", ""), context_sha256=sha(background))
        # Structural branch headers only; no semantic keyword inference.
        if "\n" in text and "【" in text:
            matches = list(re.finditer(r"【([^】]+)】", text))
            if matches:
                records[-1]["speech_policy"] = "template_only"
            for branch, match in enumerate(matches):
                end = matches[branch + 1].start() if branch + 1 < len(matches) else len(text)
                fragment = text[match.end():end].strip()
                if not fragment:
                    continue
                choice = decisions.get("branch_assignments", {}).get(f"{tid}:{branch}", performance)
                add(topic["npcId"], "topic_opening_branch", f"{topic['id']}:{branch}", fragment, choice,
                    topic_name=topic["topicName"].get("zh-Hans", ""), branch_condition=match.group(1), context_sha256=sha(background))

    for topic in persuade_topics:
        text = topic.get("firstNpcMessage", {}).get("zh-Hans", "").strip()
        if not text:
            continue
        performance = decisions["persuade_assignments"][str(topic["id"])]
        add(topic["npcId"], "persuade_opening", str(topic["id"]), text, performance,
            topic_name=topic["topic"].get("zh-Hans", ""),
            context_sha256=sha(topic.get("backgroundPrompt", {}).get("zh-Hans", "")))
    counts = {source: sum(r["source"] == source for r in records) for source in ("base_greeting", "topic_opening", "topic_opening_branch", "persuade_opening")}
    library = {
        "schema_version": 1,
        "library_version": "2026-10-09.1",
        "language": "zh-Hans",
        "generation_method": "offline_ai_authored_per_utterance_decisions_with_character_card_profiles",
        "runtime_status": "embedded_default_enabled",
        "matching": "npc_id + exact trimmed text; active topic ID disambiguates identical text; conflicting performances remain unmatched; never match across characters or reuse previous-turn style",
        "source_sha256": {name: hashlib.sha256((args.tables / name).read_bytes()).hexdigest() for name in decisions["source_sha256"]},
        "counts": {**counts, "characters": len(character_styles), "total_records": len(records)},
        "characters": character_styles,
        "entries": records,
    }
    destination = ROOT / "config/preset-voice-styles.zh-CN.json"
    destination.write_text(json.dumps(library, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(destination), **library["counts"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
