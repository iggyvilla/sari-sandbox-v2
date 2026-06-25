"""
Requests one LiDAR scan from /commands and recreates a depth-texture-style PNG.

Requires: pip install websocket-client
Usage:
  python TestScripts/test_lidar_depth_client.py
  python TestScripts/test_lidar_depth_client.py --azimuth-min-deg 0 --azimuth-max-deg 90
"""

import argparse
import json
import math
import struct
import time
import zlib
from datetime import datetime
from pathlib import Path

import websocket


HOST = "localhost"
PORT = 8080
ENDPOINT = "commands"
HEADER_FORMAT = "<4sHHffffId"
HEADER_SIZE = struct.calcsize(HEADER_FORMAT)


class LidarClientError(RuntimeError):
    pass


def request_scan(host, port, endpoint, timeout):
    url = f"ws://{host}:{port}/{endpoint}"
    print(f"Connecting to {url} ...")

    ws = websocket.create_connection(url, timeout=timeout)
    try:
        request = {"command": "RequestLidarScan"}
        print(f"[send] {json.dumps(request)}")
        ws.send(json.dumps(request))
        response = ws.recv()
    finally:
        ws.close()

    if isinstance(response, str):
        raise LidarClientError(response)

    if not isinstance(response, (bytes, bytearray)):
        raise LidarClientError(f"Unexpected response type: {type(response).__name__}")

    print(f"[recv] {len(response)} bytes")
    return bytes(response)


def parse_scan(payload):
    if len(payload) < HEADER_SIZE:
        raise LidarClientError(f"Payload too short for LiDAR header: {len(payload)} bytes")

    (
        magic,
        channels,
        azimuth_samples,
        min_range,
        max_range,
        azimuth_start_deg,
        azimuth_step_deg,
        sequence,
        timestamp_seconds,
    ) = struct.unpack_from(HEADER_FORMAT, payload, 0)

    if magic != b"LDR1":
        raise LidarClientError(f"Unexpected LiDAR magic: {magic!r}")

    angle_count = channels
    range_count = channels * azimuth_samples
    expected_size = HEADER_SIZE + angle_count * 4 + range_count * 4
    if len(payload) != expected_size:
        raise LidarClientError(
            f"Unexpected payload size: expected {expected_size}, got {len(payload)}"
        )

    offset = HEADER_SIZE
    vertical_angles = list(struct.unpack_from(f"<{angle_count}f", payload, offset))
    offset += angle_count * 4
    ranges = list(struct.unpack_from(f"<{range_count}f", payload, offset))

    return {
        "channels": channels,
        "azimuth_samples": azimuth_samples,
        "min_range": min_range,
        "max_range": max_range,
        "azimuth_start_deg": azimuth_start_deg,
        "azimuth_step_deg": azimuth_step_deg,
        "sequence": sequence,
        "timestamp_seconds": timestamp_seconds,
        "vertical_angles_deg": vertical_angles,
        "ranges": ranges,
    }


def select_azimuth_indices(scan, azimuth_min_deg, azimuth_max_deg):
    indices = []
    start = scan["azimuth_start_deg"]
    step = scan["azimuth_step_deg"]
    count = scan["azimuth_samples"]
    if abs(azimuth_max_deg - azimuth_min_deg) >= 360.0:
        return list(range(count))

    min_deg = normalize_degrees(azimuth_min_deg)
    max_deg = normalize_degrees(azimuth_max_deg)

    for index in range(count):
        angle = normalize_degrees(start + index * step)
        if angle_in_range(angle, min_deg, max_deg):
            indices.append(index)

    if not indices:
        raise LidarClientError(
            f"No azimuth samples selected for range {azimuth_min_deg}..{azimuth_max_deg}"
        )

    return indices


def normalize_degrees(value):
    return value % 360.0


def angle_in_range(angle, min_deg, max_deg):
    if min_deg <= max_deg:
        return min_deg <= angle <= max_deg
    return angle >= min_deg or angle <= max_deg


def build_depth_image(scan, azimuth_indices):
    channels = scan["channels"]
    azimuth_samples = scan["azimuth_samples"]
    min_range = scan["min_range"]
    max_range = scan["max_range"]
    span = max(max_range - min_range, 0.0001)
    width = len(azimuth_indices)
    height = channels
    pixels = bytearray(width * height)

    for row in range(height):
        # Put the highest vertical channel at the top of the image.
        channel = channels - 1 - row
        for col, azimuth_index in enumerate(azimuth_indices):
            range_value = scan["ranges"][channel * azimuth_samples + azimuth_index]
            normalized = 1.0 - clamp((range_value - min_range) / span, 0.0, 1.0)
            pixels[row * width + col] = int(round(normalized * 255.0))

    return width, height, bytes(pixels)


def clamp(value, low, high):
    return max(low, min(high, value))


def write_png_gray(path, width, height, pixels):
    def chunk(chunk_type, data):
        body = chunk_type + data
        return (
            struct.pack(">I", len(data))
            + body
            + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
        )

    rows = bytearray()
    for y in range(height):
        rows.append(0)
        start = y * width
        rows.extend(pixels[start:start + width])

    png = bytearray()
    png.extend(b"\x89PNG\r\n\x1a\n")
    png.extend(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0)))
    png.extend(chunk(b"IDAT", zlib.compress(bytes(rows), level=9)))
    png.extend(chunk(b"IEND", b""))
    path.write_bytes(png)


def write_outputs(scan, azimuth_indices, output_dir):
    output_dir.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    image_path = output_dir / f"lidar-depth-{timestamp}.png"
    metadata_path = output_dir / f"lidar-depth-{timestamp}.json"

    width, height, pixels = build_depth_image(scan, azimuth_indices)
    write_png_gray(image_path, width, height, pixels)

    metadata = {
        "sequence": scan["sequence"],
        "timestamp_seconds": scan["timestamp_seconds"],
        "channels": scan["channels"],
        "azimuth_samples": scan["azimuth_samples"],
        "selected_azimuth_samples": len(azimuth_indices),
        "selected_azimuth_min_index": min(azimuth_indices),
        "selected_azimuth_max_index": max(azimuth_indices),
        "min_range": scan["min_range"],
        "max_range": scan["max_range"],
        "azimuth_start_deg": scan["azimuth_start_deg"],
        "azimuth_step_deg": scan["azimuth_step_deg"],
        "vertical_angles_deg": scan["vertical_angles_deg"],
        "image": str(image_path.resolve()),
    }
    metadata_path.write_text(json.dumps(metadata, indent=2))

    return image_path, metadata_path


def run(args):
    payload = request_scan(args.host, args.port, args.endpoint, args.timeout)
    scan = parse_scan(payload)
    azimuth_indices = select_azimuth_indices(
        scan,
        args.azimuth_min_deg,
        args.azimuth_max_deg,
    )
    image_path, metadata_path = write_outputs(scan, azimuth_indices, Path(args.output_dir))
    print(f"[info] LiDAR depth image saved to {image_path.resolve()}")
    print(f"[info] Metadata saved to {metadata_path.resolve()}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Request and visualize one LiDAR scan.")
    parser.add_argument("--host", default=HOST)
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--endpoint", default=ENDPOINT)
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("--azimuth-min-deg", type=float, default=0.0)
    parser.add_argument("--azimuth-max-deg", type=float, default=360.0)
    parser.add_argument(
        "--output-dir",
        default=Path(__file__).resolve().parent / "lidar_outputs",
    )
    run(parser.parse_args())
