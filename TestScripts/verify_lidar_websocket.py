"""
Requests LiDAR scans from Unity's /commands WebSocket endpoint and verifies the
binary LDR1 payload.

Requires:
  pip install websocket-client matplotlib

Usage:
  python TestScripts/verify_lidar_websocket.py
  python TestScripts/verify_lidar_websocket.py --count 5 --no-show
  python TestScripts/verify_lidar_websocket.py --azimuth-min-deg 0 --azimuth-max-deg 90
"""

import argparse
import csv
import json
import math
import struct
import sys
from datetime import datetime, timezone
from pathlib import Path

import websocket


HOST = "localhost"
PORT = 8080
ENDPOINT = "commands"
MAGIC = b"LDR1"
HEADER_FORMAT = "<4sHHffffId"
HEADER_SIZE = struct.calcsize(HEADER_FORMAT)
RANGE_TOLERANCE = 1e-4


class LidarVerifierError(RuntimeError):
    pass


def request_scan(host, port, endpoint, timeout):
    endpoint = endpoint.strip("/")
    url = f"ws://{host}:{port}/{endpoint}"
    print(f"[connect] {url}")

    ws = websocket.create_connection(url, timeout=timeout)
    try:
        request = {"command": "RequestLidarScan"}
        print(f"[send] {json.dumps(request)}")
        ws.send(json.dumps(request))
        response = ws.recv()
    finally:
        ws.close()

    if isinstance(response, str):
        raise LidarVerifierError(
            "Expected binary LiDAR payload, but Unity returned text: "
            f"{response}"
        )

    if not isinstance(response, (bytes, bytearray)):
        raise LidarVerifierError(
            f"Unexpected WebSocket response type: {type(response).__name__}"
        )

    print(f"[recv] {len(response)} bytes")
    return bytes(response)


def parse_scan(payload):
    if len(payload) < HEADER_SIZE:
        raise LidarVerifierError(
            f"Payload too short for LiDAR header: {len(payload)} bytes"
        )

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

    if magic != MAGIC:
        raise LidarVerifierError(f"Unexpected LiDAR magic: {magic!r}")

    if channels <= 0:
        raise LidarVerifierError(f"Invalid LiDAR channel count: {channels}")

    if azimuth_samples <= 0:
        raise LidarVerifierError(
            f"Invalid LiDAR azimuth sample count: {azimuth_samples}"
        )

    angle_count = channels
    range_count = channels * azimuth_samples
    expected_size = HEADER_SIZE + angle_count * 4 + range_count * 4
    if len(payload) != expected_size:
        raise LidarVerifierError(
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
        "payload_bytes": len(payload),
    }


def validate_scan(scan):
    channels = scan["channels"]
    azimuth_samples = scan["azimuth_samples"]
    min_range = scan["min_range"]
    max_range = scan["max_range"]
    azimuth_step_deg = scan["azimuth_step_deg"]
    timestamp_seconds = scan["timestamp_seconds"]
    vertical_angles = scan["vertical_angles_deg"]
    ranges = scan["ranges"]

    finite_header_values = [
        ("min_range", min_range),
        ("max_range", max_range),
        ("azimuth_start_deg", scan["azimuth_start_deg"]),
        ("azimuth_step_deg", azimuth_step_deg),
        ("timestamp_seconds", timestamp_seconds),
    ]
    for label, value in finite_header_values:
        if not math.isfinite(value):
            raise LidarVerifierError(f"Header value {label} is not finite: {value}")

    if min_range >= max_range:
        raise LidarVerifierError(
            f"Invalid range bounds: minRange={min_range}, maxRange={max_range}"
        )

    if len(vertical_angles) != channels:
        raise LidarVerifierError(
            f"Expected {channels} vertical angles, got {len(vertical_angles)}"
        )

    non_finite_angles = [
        index for index, angle in enumerate(vertical_angles)
        if not math.isfinite(angle)
    ]
    if non_finite_angles:
        raise LidarVerifierError(
            f"Non-finite vertical angle indices: {non_finite_angles[:10]}"
        )

    expected_range_count = channels * azimuth_samples
    if len(ranges) != expected_range_count:
        raise LidarVerifierError(
            f"Expected {expected_range_count} ranges, got {len(ranges)}"
        )

    expected_step = 360.0 / azimuth_samples
    step_tolerance = max(1e-5, abs(expected_step) * 1e-4)
    if abs(azimuth_step_deg - expected_step) > step_tolerance:
        raise LidarVerifierError(
            "Unexpected azimuth step: "
            f"expected {expected_step:.9f}, got {azimuth_step_deg:.9f}"
        )

    range_tolerance = max(RANGE_TOLERANCE, (max_range - min_range) * 1e-5)
    bad_ranges = []
    for index, value in enumerate(ranges):
        if (
            not math.isfinite(value)
            or value < min_range - range_tolerance
            or value > max_range + range_tolerance
        ):
            bad_ranges.append((index, value))
            if len(bad_ranges) >= 10:
                break

    if bad_ranges:
        formatted = ", ".join(
            f"{index}={value!r}" for index, value in bad_ranges
        )
        raise LidarVerifierError(f"Invalid range values: {formatted}")

    range_count = len(ranges)
    min_observed = min(ranges)
    max_observed = max(ranges)
    mean_observed = math.fsum(ranges) / range_count
    max_range_count = sum(
        1 for value in ranges
        if value >= max_range - range_tolerance
    )

    per_channel = []
    for channel in range(channels):
        start = channel * azimuth_samples
        channel_ranges = ranges[start:start + azimuth_samples]
        per_channel.append({
            "channel": channel,
            "vertical_deg": vertical_angles[channel],
            "min_range": min(channel_ranges),
            "max_range": max(channel_ranges),
            "mean_range": math.fsum(channel_ranges) / len(channel_ranges),
            "max_range_percent": (
                sum(
                    1 for value in channel_ranges
                    if value >= max_range - range_tolerance
                )
                / len(channel_ranges)
                * 100.0
            ),
        })

    return {
        "range_count": range_count,
        "range_tolerance": range_tolerance,
        "azimuth_step_expected_deg": expected_step,
        "azimuth_step_tolerance_deg": step_tolerance,
        "min_observed_range": min_observed,
        "max_observed_range": max_observed,
        "mean_observed_range": mean_observed,
        "max_range_returns": max_range_count,
        "max_range_percent": max_range_count / range_count * 100.0,
        "per_channel": per_channel,
    }


def select_azimuth_indices(scan, azimuth_min_deg, azimuth_max_deg):
    count = scan["azimuth_samples"]
    if abs(azimuth_max_deg - azimuth_min_deg) >= 360.0:
        return list(range(count))

    indices = []
    min_deg = normalize_degrees(azimuth_min_deg)
    max_deg = normalize_degrees(azimuth_max_deg)

    for index in range(count):
        angle = normalize_degrees(azimuth_degrees(scan, index))
        if angle_in_range(angle, min_deg, max_deg):
            indices.append(index)

    if not indices:
        raise LidarVerifierError(
            f"No azimuth samples selected for range "
            f"{azimuth_min_deg}..{azimuth_max_deg}"
        )

    return indices


def normalize_degrees(value):
    return value % 360.0


def angle_in_range(angle, min_deg, max_deg):
    if min_deg <= max_deg:
        return min_deg <= angle <= max_deg
    return angle >= min_deg or angle <= max_deg


def azimuth_degrees(scan, azimuth_index):
    return scan["azimuth_start_deg"] + azimuth_index * scan["azimuth_step_deg"]


def build_points(scan, azimuth_indices):
    points = []
    channels = scan["channels"]
    azimuth_samples = scan["azimuth_samples"]
    vertical_angles = scan["vertical_angles_deg"]
    ranges = scan["ranges"]

    for channel in range(channels):
        vertical_deg = vertical_angles[channel]
        vertical_rad = math.radians(vertical_deg)
        cos_vertical = math.cos(vertical_rad)
        sin_vertical = math.sin(vertical_rad)

        for azimuth_index in azimuth_indices:
            range_m = ranges[channel * azimuth_samples + azimuth_index]
            azimuth_deg = normalize_degrees(azimuth_degrees(scan, azimuth_index))
            azimuth_rad = math.radians(azimuth_deg)

            x = math.sin(azimuth_rad) * cos_vertical * range_m
            y = sin_vertical * range_m
            z = math.cos(azimuth_rad) * cos_vertical * range_m

            points.append((
                channel,
                azimuth_index,
                azimuth_deg,
                vertical_deg,
                range_m,
                x,
                y,
                z,
            ))

    return points


def write_csv(path, points):
    with path.open("w", newline="", encoding="utf-8") as csv_file:
        writer = csv.writer(csv_file)
        writer.writerow([
            "channel",
            "azimuth_index",
            "azimuth_deg",
            "vertical_deg",
            "range_m",
            "x",
            "y",
            "z",
        ])
        writer.writerows(points)


def write_json(path, scan, validation, selection, output_paths):
    timestamp_utc = datetime.fromtimestamp(
        scan["timestamp_seconds"],
        tz=timezone.utc,
    ).isoformat()
    metadata = {
        "sequence": scan["sequence"],
        "timestamp_seconds": scan["timestamp_seconds"],
        "timestamp_utc": timestamp_utc,
        "payload_bytes": scan["payload_bytes"],
        "channels": scan["channels"],
        "azimuth_samples": scan["azimuth_samples"],
        "min_range": scan["min_range"],
        "max_range": scan["max_range"],
        "azimuth_start_deg": scan["azimuth_start_deg"],
        "azimuth_step_deg": scan["azimuth_step_deg"],
        "vertical_angles_deg": scan["vertical_angles_deg"],
        "selection": selection,
        "validation": validation,
        "outputs": {
            label: str(path.resolve())
            for label, path in output_paths.items()
        },
    }
    path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")


def write_visualizations(plt, scan, points, azimuth_indices, stride, output_paths, close_figures):
    plot_points = points[::stride]
    if not plot_points:
        raise LidarVerifierError("No points available for visualization")

    pointcloud_fig = plot_point_cloud(plt, scan, plot_points)
    pointcloud_fig.savefig(output_paths["pointcloud_png"], dpi=160)

    heatmap_fig = plot_heatmap(plt, scan, azimuth_indices)
    heatmap_fig.savefig(output_paths["heatmap_png"], dpi=160)

    if close_figures:
        plt.close(pointcloud_fig)
        plt.close(heatmap_fig)


def plot_point_cloud(plt, scan, points):
    fig = plt.figure(figsize=(9, 7))
    ax = fig.add_subplot(111, projection="3d")

    xs = [point[5] for point in points]
    ys = [point[6] for point in points]
    zs = [point[7] for point in points]
    ranges = [point[4] for point in points]

    scatter = ax.scatter(
        xs,
        ys,
        zs,
        c=ranges,
        cmap="viridis",
        s=3,
        linewidths=0,
        depthshade=False,
        vmin=scan["min_range"],
        vmax=scan["max_range"],
    )
    ax.set_title(f"LiDAR point cloud, sequence {scan['sequence']}")
    ax.set_xlabel("X right (m)")
    ax.set_ylabel("Y up (m)")
    ax.set_zlabel("Z forward (m)")
    set_axes_equal(ax, xs, ys, zs)
    fig.colorbar(scatter, ax=ax, shrink=0.7, pad=0.08, label="Range (m)")
    fig.tight_layout()
    return fig


def set_axes_equal(ax, xs, ys, zs):
    x_limits = bounds_or_default(xs)
    y_limits = bounds_or_default(ys)
    z_limits = bounds_or_default(zs)
    spans = [
        x_limits[1] - x_limits[0],
        y_limits[1] - y_limits[0],
        z_limits[1] - z_limits[0],
    ]
    radius = max(max(spans) * 0.5, 0.5)
    centers = [
        (x_limits[0] + x_limits[1]) * 0.5,
        (y_limits[0] + y_limits[1]) * 0.5,
        (z_limits[0] + z_limits[1]) * 0.5,
    ]

    ax.set_xlim(centers[0] - radius, centers[0] + radius)
    ax.set_ylim(centers[1] - radius, centers[1] + radius)
    ax.set_zlim(centers[2] - radius, centers[2] + radius)

    try:
        ax.set_box_aspect((1, 1, 1))
    except AttributeError:
        pass


def bounds_or_default(values):
    if not values:
        return (-0.5, 0.5)
    low = min(values)
    high = max(values)
    if low == high:
        return (low - 0.5, high + 0.5)
    return (low, high)


def plot_heatmap(plt, scan, azimuth_indices):
    channels = scan["channels"]
    azimuth_samples = scan["azimuth_samples"]
    ranges = scan["ranges"]
    vertical_angles = scan["vertical_angles_deg"]

    rows = []
    for row in range(channels):
        channel = channels - 1 - row
        start = channel * azimuth_samples
        rows.append([ranges[start + index] for index in azimuth_indices])

    fig, ax = plt.subplots(figsize=(11, 5))
    image = ax.imshow(
        rows,
        aspect="auto",
        interpolation="nearest",
        cmap="viridis",
        vmin=scan["min_range"],
        vmax=scan["max_range"],
    )
    ax.set_title(f"LiDAR range heatmap, sequence {scan['sequence']}")
    ax.set_xlabel("Selected azimuth sample")
    ax.set_ylabel("Vertical angle (deg)")

    if channels <= 32:
        ax.set_yticks(range(channels))
        ax.set_yticklabels([
            f"{vertical_angles[channels - 1 - row]:.1f}"
            for row in range(channels)
        ])

    if azimuth_indices:
        tick_count = min(8, len(azimuth_indices))
        if tick_count > 1:
            tick_positions = [
                round(index * (len(azimuth_indices) - 1) / (tick_count - 1))
                for index in range(tick_count)
            ]
            ax.set_xticks(tick_positions)
            ax.set_xticklabels([
                f"{normalize_degrees(azimuth_degrees(scan, azimuth_indices[pos])):.1f}"
                for pos in tick_positions
            ])
            ax.set_xlabel("Azimuth (deg)")

    fig.colorbar(image, ax=ax, label="Range (m)")
    fig.tight_layout()
    return fig


def load_pyplot(no_show):
    try:
        if no_show:
            import matplotlib

            matplotlib.use("Agg")

        import matplotlib.pyplot as plt
    except ImportError as exc:
        raise LidarVerifierError(
            "Matplotlib is required for visualization. Install it with: "
            "pip install matplotlib"
        ) from exc

    return plt


def write_scan_outputs(plt, scan, validation, azimuth_indices, output_dir, stride, no_show, scan_number):
    output_dir.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    stem = f"lidar-websocket-{timestamp}-{scan_number:03d}"

    json_path = output_dir / f"{stem}.json"
    csv_path = output_dir / f"{stem}.csv"
    pointcloud_path = output_dir / f"{stem}-pointcloud.png"
    heatmap_path = output_dir / f"{stem}-heatmap.png"

    points = build_points(scan, azimuth_indices)
    write_csv(csv_path, points)

    output_paths = {
        "json": json_path,
        "csv": csv_path,
        "pointcloud_png": pointcloud_path,
        "heatmap_png": heatmap_path,
    }

    write_visualizations(
        plt,
        scan,
        points,
        azimuth_indices,
        stride,
        output_paths,
        close_figures=no_show,
    )

    selection = {
        "azimuth_min_deg": normalize_degrees(azimuth_degrees(scan, azimuth_indices[0])),
        "azimuth_max_deg": normalize_degrees(azimuth_degrees(scan, azimuth_indices[-1])),
        "selected_azimuth_samples": len(azimuth_indices),
        "selected_azimuth_min_index": min(azimuth_indices),
        "selected_azimuth_max_index": max(azimuth_indices),
        "csv_points": len(points),
        "plot_stride": stride,
        "plotted_points": len(points[::stride]),
    }
    write_json(json_path, scan, validation, selection, output_paths)

    return {
        "json": json_path,
        "csv": csv_path,
        "pointcloud_png": pointcloud_path,
        "heatmap_png": heatmap_path,
    }


def print_summary(scan, validation, azimuth_indices, output_paths):
    timestamp_utc = datetime.fromtimestamp(
        scan["timestamp_seconds"],
        tz=timezone.utc,
    ).isoformat()
    print("[pass] LDR1 payload validation passed")
    print(
        "[scan] "
        f"sequence={scan['sequence']} "
        f"timestamp_utc={timestamp_utc} "
        f"channels={scan['channels']} "
        f"azimuth_samples={scan['azimuth_samples']} "
        f"selected_azimuth_samples={len(azimuth_indices)}"
    )
    print(
        "[ranges] "
        f"min={validation['min_observed_range']:.3f}m "
        f"max={validation['max_observed_range']:.3f}m "
        f"mean={validation['mean_observed_range']:.3f}m "
        f"max_range_returns={validation['max_range_percent']:.1f}%"
    )
    print("[per-channel]")
    for channel in validation["per_channel"]:
        print(
            "  "
            f"ch={channel['channel']:02d} "
            f"vertical={channel['vertical_deg']:.2f}deg "
            f"min={channel['min_range']:.3f}m "
            f"max={channel['max_range']:.3f}m "
            f"mean={channel['mean_range']:.3f}m "
            f"max_returns={channel['max_range_percent']:.1f}%"
        )

    print(f"[output] json: {output_paths['json'].resolve()}")
    print(f"[output] csv: {output_paths['csv'].resolve()}")
    print(f"[output] point cloud: {output_paths['pointcloud_png'].resolve()}")
    print(f"[output] heatmap: {output_paths['heatmap_png'].resolve()}")


def run(args):
    if args.count < 1:
        raise LidarVerifierError("--count must be at least 1")
    if args.stride < 1:
        raise LidarVerifierError("--stride must be at least 1")

    plt = load_pyplot(args.no_show)
    output_dir = Path(args.output_dir).expanduser()

    for scan_number in range(1, args.count + 1):
        print(f"\n=== LiDAR scan {scan_number}/{args.count} ===")
        payload = request_scan(args.host, args.port, args.endpoint, args.timeout)
        scan = parse_scan(payload)
        validation = validate_scan(scan)
        azimuth_indices = select_azimuth_indices(
            scan,
            args.azimuth_min_deg,
            args.azimuth_max_deg,
        )
        output_paths = write_scan_outputs(
            plt,
            scan,
            validation,
            azimuth_indices,
            output_dir,
            args.stride,
            args.no_show,
            scan_number,
        )
        print_summary(scan, validation, azimuth_indices, output_paths)

    if not args.no_show:
        print("[show] Close Matplotlib windows to exit.")
        plt.show()


def build_parser():
    parser = argparse.ArgumentParser(
        description="Verify and visualize Unity LiDAR WebSocket payloads."
    )
    parser.add_argument("--host", default=HOST)
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--endpoint", default=ENDPOINT)
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("--count", type=int, default=1)
    parser.add_argument(
        "--output-dir",
        default=Path(__file__).resolve().parent / "lidar_outputs",
    )
    parser.add_argument("--azimuth-min-deg", type=float, default=0.0)
    parser.add_argument("--azimuth-max-deg", type=float, default=360.0)
    parser.add_argument(
        "--stride",
        type=int,
        default=1,
        help="Plot every Nth selected point; CSV still receives all selected points.",
    )
    parser.add_argument(
        "--no-show",
        action="store_true",
        help="Save PNGs without opening Matplotlib windows.",
    )
    return parser


def main():
    parser = build_parser()
    try:
        run(parser.parse_args())
    except (LidarVerifierError, OSError, websocket.WebSocketException) as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
