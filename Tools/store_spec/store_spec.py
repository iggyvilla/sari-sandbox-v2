#!/usr/bin/env python3
"""Validate agent-written Sari store specs and compile them to the sandbox's store file format."""
import argparse
import difflib
import json
import math
import sys
from collections import deque
from pathlib import Path

from jsonschema import Draft202012Validator

HERE = Path(__file__).parent
SCHEMA = json.loads((HERE / "schema.json").read_text())
CATALOG = json.loads((HERE / "catalog.json").read_text())  # Unity: Tools/Store Spec/Export Catalog

SHELF_DEPTH = CATALOG["shelf"]["depth"]
SHELF_THICKNESS = CATALOG["shelf"]["thickness"]
ITEM_PADDING = CATALOG["shelf"]["inter_item_padding"]
assert SCHEMA["$defs"]["category"]["enum"] == list(CATALOG["products"]), "schema.json categories are out of sync with catalog.json"
PRODUCTS = {p["name"]: {**p, "category": c} for c, items in CATALOG["products"].items() for p in items}

SIDES = ["front", "back", "left", "right"]  # index == ShelfBuilder subShelfId
# Per-side (buildShelves, buildBackWall, buildShelfRoof), matching the Store Builder's shelf kinds.
PRESETS = {
    "wall": dict(sides={"front": (1, 1, 0)}, width=1.5, level_spacing=0.41, base_height=0.2, roof=0.0),
    "island": dict(sides={"front": (1, 1, 0), "back": (1, 0, 0)}, width=4.0, level_spacing=0.41, base_height=0.2, roof=0.0),
    "gondola": dict(sides={"front": (1, 1, 0), "back": (1, 0, 0), "left": (1, 1, 0), "right": (1, 1, 0)},
                    width=4.0, level_spacing=0.41, base_height=0.2, roof=0.0),
    "fridge": dict(sides={"front": (1, 1, 1), "left": (0, 1, 0), "right": (0, 1, 0)},
                   width=1.25, level_spacing=0.38, base_height=0.15, roof=0.5),
}
DEFAULT_LEVELS = 4
FRIDGE_DOOR_REACH = 0.07  # door gap + glass + border past the shelf front (ShelfBuilder.Fridge)
PLANE_UNITS_PER_SCALE = 10  # floorWidth/Height are Unity Plane scales
WALL_TOLERANCE = 0.02
GRID = 0.05
EPS = 1e-3


class Report:
    def __init__(self):
        self.issues = []

    def error(self, path, message):
        self.issues.append({"level": "error", "path": path, "message": message})

    def warn(self, path, message):
        self.issues.append({"level": "warning", "path": path, "message": message})

    @property
    def ok(self):
        return all(i["level"] != "error" for i in self.issues)


# -- Geometry (x, z); rects are (xmin, zmin, xmax, zmax) ---------------------------------------

def rotate(x, z, yaw):
    r = math.radians(yaw)
    return x * math.cos(r) + z * math.sin(r), -x * math.sin(r) + z * math.cos(r)


def to_world(point, pos, yaw):
    x, z = rotate(*point, yaw)
    return x + pos[0], z + pos[1]


def world_rect(rect, pos, yaw):
    corners = [to_world(c, pos, yaw) for c in ((rect[0], rect[1]), (rect[2], rect[3]))]
    xs, zs = [c[0] for c in corners], [c[1] for c in corners]
    return min(xs), min(zs), max(xs), max(zs)


def overlaps(a, b):
    return a[0] < b[2] - EPS and b[0] < a[2] - EPS and a[1] < b[3] - EPS and b[1] < a[3] - EPS


def inside(rect, half_w, half_d, tolerance=0.0):
    return (rect[0] >= -half_w - tolerance and rect[2] <= half_w + tolerance
            and rect[1] >= -half_d - tolerance and rect[3] <= half_d + tolerance)


def fmt(rect):
    return "x {:.2f}..{:.2f}, z {:.2f}..{:.2f}".format(rect[0], rect[2], rect[1], rect[3])


# -- Normalization ------------------------------------------------------------------------------

def shelf_layout(shelf):
    """Resolved dimensions, footprint and stockable faces of a shelf, in its local frame."""
    preset = PRESETS[shelf["type"]]
    sides = preset["sides"]
    has = lambda side, i=0: sides.get(side, (0, 0, 0))[i]
    w, d, t = shelf.get("width", preset["width"]), SHELF_DEPTH, SHELF_THICKNESS
    levels = shelf.get("levels", DEFAULT_LEVELS)
    spacing = shelf.get("level_spacing", preset["level_spacing"])
    base = shelf.get("base_height", preset["base_height"])

    # Mirrors ShelfBuilder.BuildRectangularShelf / CalculateShelfWidth / MyPosWithOffset.
    one_sided = has("front") != has("back")
    end_width = (2 * d + t) / (2 if one_sided else 1)
    end_z = (end_width / 2 if has("front") else -end_width / 2) if one_sided else 0.0
    end_reach = lambda side: w / 2 + (t + d if has(side) else t if has(side, 1) else 0)
    front_reach = (d + t / 2 if has("front") else t / 2) + (FRIDGE_DOOR_REACH if shelf["type"] == "fridge" else 0)
    back_reach = d + t / 2 if has("back") else t / 2

    faces = {
        "front": ((0, d + t / 2), (0, 1), w),
        "back": ((0, -(d + t / 2)), (0, -1), w),
        "left": ((w / 2 + t + d, end_z), (1, 0), end_width),
        "right": ((-(w / 2 + t + d), end_z), (-1, 0), end_width),
    }
    return {
        "width": w, "levels": levels, "level_spacing": spacing, "base_height": base, "roof": preset["roof"],
        "height": spacing * levels + base + preset["roof"],
        "configs": {s: sides.get(s, (0, 0, 0)) for s in SIDES},
        "stocked": [s for s in SIDES if has(s)],
        "footprint": (-end_reach("right"), -back_reach, end_reach("left"), front_reach),
        "faces": {s: f for s, f in faces.items() if has(s)},
    }


def side_levels(value, levels, path, report):
    """[{category, products}] per level (bottom first), or None when the side is malformed."""
    entries = value if isinstance(value, list) else [value] * levels
    if len(entries) != levels:
        report.error(path, f"has {len(entries)} level entries but the shelf has {levels} levels")
        return None

    resolved = []
    for i, entry in enumerate(entries):
        if isinstance(entry, str):
            resolved.append({"category": entry, "products": None})
            continue

        products = entry.get("products")
        category = entry.get("category")
        for name in dict.fromkeys(products or []):
            if name not in PRODUCTS:
                hint = difflib.get_close_matches(name, PRODUCTS, n=1)
                report.error(f"{path}[{i}]", f"unknown product '{name}'" + (f" (did you mean '{hint[0]}'?)" if hint else ""))
            elif category and PRODUCTS[name]["category"] != category:
                report.warn(f"{path}[{i}]", f"'{name}' is a {PRODUCTS[name]['category']} product on a {category} level")
        if category is None:
            category = next((PRODUCTS[n]["category"] for n in products if n in PRODUCTS), None)
        resolved.append({"category": category, "products": products})
    return resolved


# -- Validation ---------------------------------------------------------------------------------

def root_cause(error):
    """For oneOf errors, the deepest reason from a branch that matches the value's JSON type."""
    if not error.context:
        return error
    causes = [root_cause(e) for e in error.context]
    # A type error, or an enum given an object/array, just means "wrong branch".
    wrong_branch = lambda e: e.validator == "type" or e.validator == "enum" and isinstance(e.instance, (dict, list))
    return max(causes, key=lambda e: (not wrong_branch(e), len(e.absolute_path)))


def validate(spec, agent_radius):
    """Returns (report, resolved shelves). Resolved shelves are only complete when report.ok."""
    report = Report()
    for e in sorted(Draft202012Validator(SCHEMA).iter_errors(spec), key=lambda e: list(e.path)):
        e = root_cause(e)
        path = "".join(f"[{p}]" if isinstance(p, int) else f".{p}" for p in e.absolute_path).lstrip(".") or "(root)"
        report.error(path, e.message)
    if not report.ok:
        return report, []

    half_w, half_d = spec["floor"]["width"] / 2, spec["floor"]["depth"] / 2
    wall_height = spec.get("wall_height", 4)
    obstacles = []  # (path, world rect)
    shelves = []

    for i, shelf in enumerate(spec["shelves"]):
        path = f"shelves[{i}]"
        layout = shelf_layout(shelf)
        pos, yaw = shelf["position"], shelf.get("yaw", 0)
        rect = world_rect(layout["footprint"], pos, yaw)

        if shelf["type"] != "fridge" and "door_style" in shelf:
            report.warn(path, "door_style only applies to fridges")
        if layout["height"] >= wall_height:
            report.error(path, f"is {layout['height']:.2f} m tall but walls are {wall_height} m")
        if not inside(rect, half_w, half_d, WALL_TOLERANCE):
            report.error(path, f"footprint ({fmt(rect)}) extends past the floor")

        stock = {}
        for side in SIDES:
            if side in layout["stocked"] and side not in shelf:
                report.error(path, f"a {shelf['type']} shelf needs stock for its '{side}' side")
            elif side not in layout["stocked"] and side in shelf:
                report.error(f"{path}.{side}", f"a {shelf['type']} shelf has no '{side}' side (stocked sides: {', '.join(layout['stocked'])})")
            elif side in shelf:
                stock[side] = side_levels(shelf[side], layout["levels"], f"{path}.{side}", report)

        has_products = False
        for side, levels in stock.items():
            budget = layout["faces"][side][2]
            for j, level in enumerate(levels or []):
                if not level["products"]:
                    continue
                has_products = True
                known = [PRODUCTS[n] for n in level["products"] if n in PRODUCTS]
                total = sum(p["width"] for p in known) + ITEM_PADDING * max(0, len(known) - 1)
                if total > budget:
                    report.error(f"{path}.{side}[{j}]", f"products need {total:.2f} m but the level is {budget:.2f} m wide")
                too_tall = [p["name"] for p in known if p["height"] > layout["level_spacing"] - SHELF_THICKNESS]
                if too_tall and (j < layout["levels"] - 1 or layout["roof"]):  # the open top level has no limit
                    report.warn(f"{path}.{side}[{j}]", f"taller than the level gap: {', '.join(too_tall)}")
        if has_products and shelf.get("randomize_each_reset"):
            report.error(path, "randomize_each_reset can't be combined with explicit products")

        for other_path, other in obstacles:
            if overlaps(rect, other):
                report.error(path, f"overlaps {other_path}")
        obstacles.append((path, rect))
        shelves.append({**shelf, "layout": layout, "stock": stock, "has_products": has_products, "rect": rect})

    checkout = CATALOG["checkout"]
    sx, sz = checkout["size"]
    ox, oz = checkout["offset"]
    checkout_local = (ox - sx / 2, oz - sz / 2, ox + sx / 2, oz + sz / 2)
    checkout_rects = []
    for i, c in enumerate(spec.get("checkouts", [])):
        path = f"checkouts[{i}]"
        rect = world_rect(checkout_local, c["position"], c.get("yaw", 0))
        if not inside(rect, half_w, half_d, WALL_TOLERANCE):
            report.error(path, f"footprint ({fmt(rect)}) extends past the floor")
        for other_path, other in obstacles:
            if overlaps(rect, other):
                report.error(path, f"overlaps {other_path}")
        obstacles.append((path, rect))
        checkout_rects.append((path, rect))
    if not checkout_rects:
        report.warn("checkouts", "store has no self-checkout, so shoppers can't pay")

    for i, m in enumerate(spec.get("aisle_markers", [])):
        path = f"aisle_markers[{i}]"
        x, z = m["position"]
        if abs(x) > half_w or abs(z) > half_d:
            report.error(path, "is outside the floor")
        if m.get("cable_length", 1.5) >= wall_height:
            report.error(path, "cable_length reaches the floor")

    check_reachability(spec, shelves, checkout_rects, obstacles, agent_radius, report)
    return report, shelves


def check_reachability(spec, shelves, checkout_rects, obstacles, radius, report):
    """Flood-fills walkable floor from the spawn and checks every stocked face and checkout is reachable."""
    width, depth = spec["floor"]["width"], spec["floor"]["depth"]
    nx, nz = int(width / GRID), int(depth / GRID)
    blocked = bytearray(nx * nz)

    def cell(x, z):
        return int((x + width / 2) / GRID), int((z + depth / 2) / GRID)

    for ix in range(nx):
        for iz in range(nz):
            x, z = (ix + 0.5) * GRID - width / 2, (iz + 0.5) * GRID - depth / 2
            if abs(x) > width / 2 - radius or abs(z) > depth / 2 - radius:
                blocked[ix * nz + iz] = 1
    for _, r in obstacles:
        (x0, z0), (x1, z1) = cell(r[0] - radius, r[1] - radius), cell(r[2] + radius, r[3] + radius)
        for ix in range(max(0, x0), min(nx, x1 + 1)):
            for iz in range(max(0, z0), min(nz, z1 + 1)):
                blocked[ix * nz + iz] = 1

    def free(point):
        ix, iz = cell(*point)
        return 0 <= ix < nx and 0 <= iz < nz and not blocked[ix * nz + iz]

    spawn = spec["agent_spawn"]["position"]
    if not free(spawn):
        report.error("agent_spawn", f"is inside or within {radius} m of a wall or obstacle")
        return

    seen = bytearray(nx * nz)
    start = cell(*spawn)
    seen[start[0] * nz + start[1]] = 1
    queue = deque([start])
    while queue:
        ix, iz = queue.popleft()
        for jx, jz in ((ix + 1, iz), (ix - 1, iz), (ix, iz + 1), (ix, iz - 1)):
            k = jx * nz + jz
            if 0 <= jx < nx and 0 <= jz < nz and not blocked[k] and not seen[k]:
                seen[k] = 1
                queue.append((jx, jz))

    def reachable(point):
        ix, iz = cell(*point)
        return free(point) and seen[ix * nz + iz]

    clearance = radius + 2 * GRID
    for i, s in enumerate(shelves):
        pos, yaw = s["position"], s.get("yaw", 0)
        door = FRIDGE_DOOR_REACH if s["type"] == "fridge" else 0
        for side, (center, normal, _) in s["layout"]["faces"].items():
            offset = clearance + (door if side == "front" else 0)
            target = to_world((center[0] + normal[0] * offset, center[1] + normal[1] * offset), pos, yaw)
            if not reachable(target):
                report.error(f"shelves[{i}].{side}", "can't be reached from agent_spawn "
                             f"(needs a {2 * radius:.1f} m wide path to it)")

    for path, r in checkout_rects:
        cx, cz = (r[0] + r[2]) / 2, (r[1] + r[3]) / 2
        around = [(r[0] - clearance, cz), (r[2] + clearance, cz), (cx, r[1] - clearance), (cx, r[3] + clearance)]
        if not any(reachable(p) for p in around):
            report.error(path, "can't be reached from agent_spawn")


# -- Compilation --------------------------------------------------------------------------------

def compile_store(spec, shelves):
    """StoreData JSON (DataHandler.cs) for a validated spec."""
    wall_height = spec.get("wall_height", 4)
    store = {
        "version": 2,
        "floorWidth": spec["floor"]["width"] / PLANE_UNITS_PER_SCALE,
        "floorHeight": spec["floor"]["depth"] / PLANE_UNITS_PER_SCALE,
        "wallHeight": wall_height,
        "shelves": [],
        "shelfItems": {},
        "selfCheckoutLocations": [placement(c) for c in spec.get("checkouts", [])],
        "agentSpawnLocation": placement(spec["agent_spawn"]),
        "aisleMarkerLocations": [],
    }

    for shelf_id, s in enumerate(shelves, start=1):
        layout = s["layout"]
        config = lambda side: dict(zip(("buildShelves", "buildBackWall", "buildShelfRoof"), map(bool, layout["configs"][side])))
        categories = {}
        for side, levels in s["stock"].items():
            sub = SIDES.index(side)
            for level, entry in enumerate(levels):
                categories[f"{sub}_{level}"] = entry["category"]
                if entry["products"]:
                    store["shelfItems"][f"ID{shelf_id}_{sub}_{level}"] = {"items": [{"name": n} for n in entry["products"]]}

        store["shelves"].append({
            "shelfId": shelf_id,
            **placement(s),
            "shelfWidth": layout["width"],
            "shelfBootHeight": layout["base_height"],
            "shelfLevels": layout["levels"],
            "distanceBetweenLevels": layout["level_spacing"],
            "shelfRoofHeight": layout["roof"],
            "frontShelfConfig": config("front"),
            "backShelfConfig": config("back"),
            "leftShelfConfig": config("left"),
            "rightShelfConfig": config("right"),
            "fridgeDoorStyle": s.get("door_style", "double" if s["type"] == "fridge" else "single").capitalize(),
            "spawnItems": True,
            "spawnPriceTags": s.get("price_tags", True),
            "spawnHingeDoors": s["type"] == "fridge",
            # ReadFromSave falls back to a random fill that is then saved, i.e. "random once".
            "itemSpawnOption": "GenerateRandom" if s.get("randomize_each_reset") else "ReadFromSave",
            "subShelfCategories": categories,
        })

    for m in spec.get("aisle_markers", []):
        labels = (m.get("categories", []) + ["", "", ""])[:3]
        cable = m.get("cable_length", 1.5)
        store["aisleMarkerLocations"].append({
            **placement(m, y=wall_height - cable),
            "category1": labels[0], "category2": labels[1], "category3": labels[2],
            "aisleNumber": m["number"],
            "cableLength": cable,
        })
    return store


def placement(item, y=0.0):
    x, z = item["position"]
    return {"posX": x, "posY": y, "posZ": z, "rotationY": item.get("yaw", 0)}


# -- CLI ----------------------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["validate", "compile"])
    parser.add_argument("spec", type=Path)
    parser.add_argument("-o", "--output", type=Path, help="compile: store file to write (<persistentDataPath>/<store name>.json)")
    parser.add_argument("--agent-radius", type=float, default=0.3, help="clearance the shopper needs from obstacles (m)")
    parser.add_argument("--json", action="store_true", help="print issues as JSON")
    args = parser.parse_args()

    try:
        spec = json.loads(args.spec.read_text())
    except (OSError, json.JSONDecodeError) as e:
        print(f"error: can't read {args.spec}: {e}", file=sys.stderr)
        return 2

    report, shelves = validate(spec, args.agent_radius)
    if args.json:
        print(json.dumps({"ok": report.ok, "issues": report.issues}, indent=2))
    else:
        for i in report.issues:
            print(f"{i['level']}: {i['path']}: {i['message']}")
        print("ok" if report.ok else f"{sum(i['level'] == 'error' for i in report.issues)} error(s)")

    if not report.ok:
        return 1
    if args.command == "compile":
        if not args.output:
            parser.error("compile needs -o/--output")
        args.output.write_text(json.dumps(compile_store(spec, shelves), indent=2))
        print(f"wrote {args.output}", file=sys.stderr if args.json else sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
