from __future__ import annotations

from sari_agent.memory.reader import load_markdown_memories


def test_load_markdown_memories_including_sari(tmp_path) -> None:
    (tmp_path / "SARI.md").write_text("system", encoding="utf-8")
    (tmp_path / "scene.md").write_text("scene memory", encoding="utf-8")
    (tmp_path / "other.md").write_text("other memory", encoding="utf-8")

    documents = load_markdown_memories(tmp_path, ["scene"])

    assert documents == {
        "SARI.md": "system",
        "scene.md": "scene memory",
    }
