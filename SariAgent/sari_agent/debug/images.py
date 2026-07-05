"""Image helpers for debug-friendly screenshot payloads."""

from __future__ import annotations

import base64
from io import BytesIO


def png_data_url(png_bytes: bytes) -> str:
    return "data:image/png;base64," + base64.b64encode(png_bytes).decode("ascii")


def thumbnail_data_url(png_bytes: bytes, *, max_edge: int = 512) -> str:
    """Return a PNG data URL, scaled down when Pillow can decode the image.

    The frontend should use this for previews and keep the saved screenshot path
    for full-resolution inspection.  If Pillow is unavailable or the image is
    malformed, we still return a valid data URL for the original bytes so debug
    event emission never blocks tool execution.
    """

    try:
        from PIL import Image
    except ImportError:
        return png_data_url(png_bytes)

    try:
        with Image.open(BytesIO(png_bytes)) as image:
            image = image.convert("RGBA")
            image.thumbnail((max_edge, max_edge))
            output = BytesIO()
            image.save(output, format="PNG")
            return png_data_url(output.getvalue())
    except Exception:
        return png_data_url(png_bytes)
