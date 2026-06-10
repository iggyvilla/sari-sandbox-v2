"""Rich terminal helpers."""

from __future__ import annotations

from typing import Any

from rich.console import Console
from rich.panel import Panel


console = Console()


def status(message: str) -> None:
    console.print(f"[bold cyan]{message}[/bold cyan]")


def log_event(label: str, payload: Any) -> None:
    console.print(Panel.fit(str(payload), title=label, border_style="cyan"))


def print_model_delta(text: str) -> None:
    console.print(text, end="")
