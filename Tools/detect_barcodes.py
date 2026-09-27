"""Finds a 1D barcode in each texture and writes its corners as UVs (origin bottom-left).

Usage: python3 detect_barcodes.py <texture paths json> <output json>
Needs opencv-python (cv2.barcode).
"""
import json
import sys

import cv2


def detect(detector, path):
    img = cv2.imread(path)
    if img is None:
        return None
    h, w = img.shape[:2]
    # Prefer a detection that decodes; undecodable ones are often text blocks.
    ok, decoded, _, pts = detector.detectAndDecodeWithType(img)
    best = next((i for i, text in enumerate(decoded) if text), None) if ok and pts is not None else None
    if best is None:
        ok, pts = detector.detect(img)
        if not ok or pts is None:
            return None
        best = 0
    return [[float(x / w), float(1 - y / h)] for x, y in pts[best]]


def main():
    paths = json.load(open(sys.argv[1]))
    detector = cv2.barcode.BarcodeDetector()
    json.dump({p: detect(detector, p) for p in paths}, open(sys.argv[2], "w"))


if __name__ == "__main__":
    main()
