#!/usr/bin/env python3
"""Find the most expensive and heaviest item in each category.

Reads Categories.json (category -> item names) and PriceData.json
(item name -> price/weight info) from the same directory, then prints
the max-price and max-weight item per category.
"""

import json
import re
from pathlib import Path

DATA_DIR = Path(__file__).resolve().parent
CATEGORIES_FILE = DATA_DIR / "Categories.json"
PRICE_DATA_FILE = DATA_DIR / "PriceData.json"

# Grams-per-unit, treating ml as 1g (density ~1) since these are grocery items.
UNIT_TO_GRAMS = {
    "g": 1,
    "kg": 1000,
    "ml": 1,
    "l": 1000,
}

WEIGHT_RE = re.compile(r"^([\d.]+)\s*([a-zA-Z]+)$")


def parse_weight_grams(net_weight: str) -> float | None:
    if not net_weight:
        return None
    match = WEIGHT_RE.match(net_weight.strip())
    if not match:
        return None
    value, unit = match.groups()
    unit = unit.lower()
    if unit not in UNIT_TO_GRAMS:
        return None
    return float(value) * UNIT_TO_GRAMS[unit]


def main():
    categories = json.loads(CATEGORIES_FILE.read_text())["Categories"]
    price_data = json.loads(PRICE_DATA_FILE.read_text())

    for category in categories:
        name = category["Category"]
        items = category["Items"]

        priced_items = []
        weighed_items = []
        for item in items:
            info = price_data.get(item)
            if info is None:
                continue
            price = info.get("pricePHP")
            if price is not None:
                priced_items.append((item, price))
            grams = parse_weight_grams(info.get("netWeight"))
            if grams is not None:
                weighed_items.append((item, grams, info.get("netWeight")))

        print(f"== {name} ==")
        if priced_items:
            item, price = max(priced_items, key=lambda x: x[1])
            print(f"  Most expensive: {item} (PHP {price})")
            item, price = min(priced_items, key=lambda x: x[1])
            print(f"  Cheapest:       {item} (PHP {price})")
        else:
            print("  Most expensive: (no price data)")

        if weighed_items:
            item, grams, raw = max(weighed_items, key=lambda x: x[1])
            print(f"  Heaviest:       {item} ({raw})")
        else:
            print("  Heaviest:       (no weight data)")
        print()


if __name__ == "__main__":
    main()
