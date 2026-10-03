#!/usr/bin/env python3
"""Generates the Store Builder UI sprites (9-slice shapes + icons) into Assets/UI/StoreBuilder/Sprites.

Shapes are white so Unity tints them. Icons use the same 24 px paths as the design mock-up.
Needs Pillow only. Usage: python3 Tools/generate_store_builder_sprites.py
"""
import math
import re
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "Assets/UI/StoreBuilder/Sprites"
SS = 8  # supersampling

# ── shapes ────────────────────────────────────────────────────────────────────

def shape(name, size, radius, ring=0):
    s = size * SS
    mask = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=radius * SS, fill=255)
    if ring:
        inner = Image.new("L", (s, s), 0)
        ImageDraw.Draw(inner).rounded_rectangle(
            [ring * SS, ring * SS, s - 1 - ring * SS, s - 1 - ring * SS], radius=max(radius - ring, 0) * SS, fill=255)
        mask = Image.composite(Image.new("L", (s, s), 0), mask, inner)
    save(name, mask.resize((size, size), Image.LANCZOS))


def circle(name, size):
    s = size * SS
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse([0, 0, s - 1, s - 1], fill=255)
    save(name, mask.resize((size, size), Image.LANCZOS))


def save(name, mask):
    img = Image.new("RGBA", mask.size, (255, 255, 255, 0))
    img.putalpha(mask)
    OUT.mkdir(parents=True, exist_ok=True)
    img.save(OUT / f"{name}.png")


# ── svg path flattening ───────────────────────────────────────────────────────

NUM = re.compile(r"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")


def tokens(d):
    """Yields (command, [numbers]) groups; arc flags may be glued to the next number."""
    i, n = 0, len(d)
    cmd = None
    while i < n:
        c = d[i]
        if c.isalpha():
            cmd = c
            i += 1
            if cmd in "zZ":
                yield cmd, []
            continue
        if c in " ,\t\n":
            i += 1
            continue
        arity = {"m": 2, "l": 2, "h": 1, "v": 1, "c": 6, "s": 4, "q": 4, "t": 2, "a": 7}[cmd.lower()]
        args = []
        for k in range(arity):
            while i < n and d[i] in " ,\t\n":
                i += 1
            if cmd.lower() == "a" and k in (3, 4):
                args.append(float(d[i]))
                i += 1
            else:
                m = NUM.match(d, i)
                args.append(float(m.group()))
                i = m.end()
        yield cmd, args
        if cmd == "m":
            cmd = "l"
        elif cmd == "M":
            cmd = "L"


def arc_points(x1, y1, rx, ry, phi, fa, fs, x2, y2, steps=24):
    if rx == 0 or ry == 0:
        return [(x2, y2)]
    phi = math.radians(phi)
    cp, sp = math.cos(phi), math.sin(phi)
    dx, dy = (x1 - x2) / 2, (y1 - y2) / 2
    x1p, y1p = cp * dx + sp * dy, -sp * dx + cp * dy
    rx, ry = abs(rx), abs(ry)
    lam = x1p ** 2 / rx ** 2 + y1p ** 2 / ry ** 2
    if lam > 1:
        rx, ry = rx * math.sqrt(lam), ry * math.sqrt(lam)
    num = rx ** 2 * ry ** 2 - rx ** 2 * y1p ** 2 - ry ** 2 * x1p ** 2
    den = rx ** 2 * y1p ** 2 + ry ** 2 * x1p ** 2
    co = math.sqrt(max(num / den, 0)) * (-1 if fa == fs else 1)
    cxp, cyp = co * rx * y1p / ry, -co * ry * x1p / rx
    cx, cy = cp * cxp - sp * cyp + (x1 + x2) / 2, sp * cxp + cp * cyp + (y1 + y2) / 2

    def ang(ux, uy, vx, vy):
        a = math.atan2(ux * vy - uy * vx, ux * vx + uy * vy)
        return a

    t1 = ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
    dt = ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
    if not fs and dt > 0:
        dt -= 2 * math.pi
    elif fs and dt < 0:
        dt += 2 * math.pi
    pts = []
    for k in range(1, steps + 1):
        t = t1 + dt * k / steps
        pts.append((cp * rx * math.cos(t) - sp * ry * math.sin(t) + cx, sp * rx * math.cos(t) + cp * ry * math.sin(t) + cy))
    return pts


def flatten(d):
    """Returns a list of (points, closed) subpaths."""
    subs, cur = [], []
    x = y = sx = sy = 0.0
    last_c = None

    def bez(p0, p1, p2, p3, steps=20):
        out = []
        for k in range(1, steps + 1):
            t = k / steps
            u = 1 - t
            out.append((u ** 3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t ** 3 * p3[0],
                        u ** 3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t ** 3 * p3[1]))
        return out

    for cmd, a in tokens(d):
        rel = cmd.islower()
        c = cmd.lower()
        ox, oy = (x, y) if rel else (0, 0)
        if c == "m":
            if cur:
                subs.append((cur, False))
            x, y = a[0] + ox, a[1] + oy
            sx, sy = x, y
            cur = [(x, y)]
        elif c == "l":
            x, y = a[0] + ox, a[1] + oy
            cur.append((x, y))
        elif c == "h":
            x = a[0] + (x if rel else 0)
            cur.append((x, y))
        elif c == "v":
            y = a[0] + (y if rel else 0)
            cur.append((x, y))
        elif c == "c":
            p1, p2, p3 = (a[0] + ox, a[1] + oy), (a[2] + ox, a[3] + oy), (a[4] + ox, a[5] + oy)
            cur += bez((x, y), p1, p2, p3)
            x, y = p3
            last_c = p2
        elif c == "s":
            p1 = (2 * x - last_c[0], 2 * y - last_c[1]) if last_c else (x, y)
            p2, p3 = (a[0] + ox, a[1] + oy), (a[2] + ox, a[3] + oy)
            cur += bez((x, y), p1, p2, p3)
            x, y = p3
            last_c = p2
        elif c == "q":
            q, p = (a[0] + ox, a[1] + oy), (a[2] + ox, a[3] + oy)
            p1 = (x + 2 / 3 * (q[0] - x), y + 2 / 3 * (q[1] - y))
            p2 = (p[0] + 2 / 3 * (q[0] - p[0]), p[1] + 2 / 3 * (q[1] - p[1]))
            cur += bez((x, y), p1, p2, p)
            x, y = p
        elif c == "a":
            ex, ey = a[5] + ox, a[6] + oy
            cur += arc_points(x, y, a[0], a[1], a[2], int(a[3]), int(a[4]), ex, ey)
            x, y = ex, ey
        elif c == "z":
            if cur:
                cur.append((sx, sy))
                subs.append((cur, True))
                cur = []
            x, y = sx, sy
        if c not in "cs":
            last_c = None
    if cur:
        subs.append((cur, False))
    return subs


def icon(name, paths, fill=False, stroke=1.8, size=64):
    scale = size * SS / 24
    mask = Image.new("L", (size * SS, size * SS), 0)
    d = ImageDraw.Draw(mask)
    w = max(int(round(stroke * scale)), 1)
    r = w / 2
    for path in paths:
        for pts, closed in flatten(path):
            px = [(p[0] * scale, p[1] * scale) for p in pts]
            if fill:
                d.polygon(px, fill=255)
            if len(px) > 1:
                d.line(px, fill=255, width=w, joint="curve")
            for qx, qy in (px[0], px[-1]):
                d.ellipse([qx - r, qy - r, qx + r, qy + r], fill=255)
    save(f"icon_{name}", mask.resize((size, size), Image.LANCZOS))


ICONS = {
    "select": ["M5 3.5l14 7-6 2-2.2 6.5z"],
    "shelf": ["M4 4v16M20 4v16M4 9h16M4 14h16M4 19h16"],
    "fridge": ["M8 3h8a2 2 0 012 2v14a2 2 0 01-2 2H8a2 2 0 01-2-2V5a2 2 0 012-2z", "M6 11h12M9 6.5v1.5M9 14v3"],
    "checkout": ["M6 4h12a2 2 0 012 2v7a2 2 0 01-2 2H6a2 2 0 01-2-2V6a2 2 0 012-2z", "M9 20h6M12 15v5"],
    "aisle": ["M12 3v4M5 7h14v8H5zM8.5 11h7"],
    "agent": ["M8.8 7.5a3.2 3.2 0 106.4 0a3.2 3.2 0 10-6.4 0", "M5.5 21c.6-4 3-6 6.5-6s5.9 2 6.5 6"],
    "exit": ["M14 4h5v16h-5M4 12h10M10 8l4 4-4 4"],
    "folder": ["M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v8a2 2 0 01-2 2H5a2 2 0 01-2-2z"],
    "save": ["M5 4h11l3 3v13H5zM8 4v5h7V4M8 20v-6h8v6"],
    "sliders": ["M4 7h9M19 7h1M4 17h1M11 17h9", "M13.8 7a2.2 2.2 0 104.4 0a2.2 2.2 0 10-4.4 0", "M5.8 17a2.2 2.2 0 104.4 0a2.2 2.2 0 10-4.4 0"],
    "move": ["M12 3v18M3 12h18M12 3l-3 3M12 3l3 3M12 21l-3-3M12 21l3-3M3 12l3-3M3 12l3 3M21 12l-3-3M21 12l-3 3"],
    "rotate": ["M20 12a8 8 0 11-2.6-5.9M20 4v5h-5"],
    "copy": ["M10 8h8a2 2 0 012 2v8a2 2 0 01-2 2h-8a2 2 0 01-2-2v-8a2 2 0 012-2z", "M16 8V6a2 2 0 00-2-2H6a2 2 0 00-2 2v8a2 2 0 002 2h2"],
    "trash": ["M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"],
    "close": ["M6 6l12 12M18 6L6 18"],
    "warn": ["M12 4l9 16H3zM12 10v4M12 17.5v.01"],
    "check": ["M5 12.5l4.5 4.5L19 7.5"],
    "search": ["M5 11a6 6 0 1012 0a6 6 0 10-12 0", "M20 20l-4.5-4.5"],
    "fill": ["M12 4l9 5-9 5-9-5zM3 14l9 5 9-5"],
    "edit": ["M4 20l4-1 11-11-3-3L5 16z"],
    "chevron": ["M6 9l6 6 6-6"],
}

if __name__ == "__main__":
    shape("fill_r8", 64, 16)
    shape("ring_r8", 64, 16, ring=2)
    shape("fill_r12", 96, 24)
    shape("ring_r12", 96, 24, ring=2)
    shape("fill_pill", 44, 22)
    shape("fill_pill_sm", 12, 6)
    shape("ring_pill", 44, 22, ring=2)
    circle("circle", 64)
    for key, paths in ICONS.items():
        icon(key, paths)
    icon("play", ["M7 4.5v15l12-7.5z"], fill=True, stroke=1.2)
    print(f"wrote {len(list(OUT.glob('*.png')))} sprites to {OUT}")
