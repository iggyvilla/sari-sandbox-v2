"""Markdown memory loading."""

from __future__ import annotations

from pathlib import Path


class MemoryLoadError(RuntimeError):
    pass


def load_markdown_memories(memory_dir: Path, selected_names: list[str] | None = None) -> dict[str, str]:
    """Load SARI.md plus selected named markdown memories from a directory."""

    memory_dir = Path(memory_dir)
    sari_path = memory_dir / "SARI.md"
    if not sari_path.exists():
        raise MemoryLoadError(f"Required system memory not found: {sari_path}")

    requested = set(selected_names or [])
    documents: dict[str, str] = {"SARI.md": sari_path.read_text(encoding="utf-8")}

    for path in sorted(memory_dir.glob("*.md")):
        if path.name == "SARI.md":
            continue
        if requested and path.stem not in requested and path.name not in requested:
            continue
        documents[path.name] = path.read_text(encoding="utf-8")

    return documents
