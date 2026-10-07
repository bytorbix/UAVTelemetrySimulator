"""
Prepares telemetry CSVs offline so every source fills the ICD with the same meanings:

    altitude                              metres above mean sea level
    yaw                                   aircraft heading, clockwise from true north, in [-180, 180)
    gimbal_yaw / gimbal_pitch / gimbal_roll
                                          ABSOLUTE camera orientation (yaw clockwise from true north,
                                          pitch up from the horizon, roll), degrees in [-180, 180)
    fov_h / fov_v                         camera field of view, degrees

The simulator stays a plain replayer: this tool only appends prep_* columns to a copy of the CSV,
and the *PreparedMapping.json files map those columns onto the ICD.

Usage:
    python prep_dataset.py misb <in.csv> <out.csv>
    python prep_dataset.py dji  <in.csv> <out.csv> --fov-h 81.7 --fov-v 51.9 --dem <copernicus_tile.tif>
    python prep_dataset.py check-misb <in.csv>

check-misb projects the camera boresight onto the ground and compares it with the
FrameCenterLatitude/Longitude the aircraft computed, which validates the rotation math.

Needs numpy. The dji mode also needs rasterio to read the DEM GeoTIFF.
"""

import argparse
import csv
import math
import statistics
import sys

import numpy as np

# WGS84 ellipsoid
WGS84_A = 6378137.0
WGS84_E2 = 6.69437999014e-3


def to_float(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def wrap180(degrees):
    return (degrees + 180.0) % 360.0 - 180.0


# --- rotations -------------------------------------------------------------------------------------
# Frames are NED (x north, y east, z down) for the world, and x forward / y right / z down for a body.
# yaw/pitch/roll are applied Z-Y-X: R = Rz(yaw) @ Ry(pitch) @ Rx(roll). Positive pitch tilts x up.

def rotation(yaw_deg, pitch_deg, roll_deg):
    y, p, r = (math.radians(a) for a in (yaw_deg, pitch_deg, roll_deg))
    rz = np.array([[math.cos(y), -math.sin(y), 0], [math.sin(y), math.cos(y), 0], [0, 0, 1]])
    ry = np.array([[math.cos(p), 0, math.sin(p)], [0, 1, 0], [-math.sin(p), 0, math.cos(p)]])
    rx = np.array([[1, 0, 0], [0, math.cos(r), -math.sin(r)], [0, math.sin(r), math.cos(r)]])
    return rz @ ry @ rx


def euler_from_rotation(r):
    yaw = math.degrees(math.atan2(r[1, 0], r[0, 0]))
    pitch = math.degrees(math.asin(max(-1.0, min(1.0, -r[2, 0]))))
    roll = math.degrees(math.atan2(r[2, 1], r[2, 2]))
    return wrap180(yaw), pitch, wrap180(roll)


def misb_camera_rotation(row):
    """MISB ST 0601: platform heading/pitch/roll, then sensor azimuth/elevation/roll relative to the platform."""
    values = [to_float(row.get(c)) for c in (
        "PlatformHeadingAngle", "PlatformPitchAngle", "PlatformRollAngle",
        "SensorRelativeAzimuth", "SensorRelativeElevation", "SensorRelativeRoll")]
    if any(v is None for v in values):
        return None
    heading, pitch, roll, azimuth, elevation, sensor_roll = values
    return rotation(heading, pitch, roll) @ rotation(azimuth, elevation, sensor_roll)


# --- geodesy ---------------------------------------------------------------------------------------

def offset_latlon(lat_deg, lon_deg, north_m, east_m):
    """Moves a point by a local north/east offset (fine for the few km a camera looks out)."""
    lat = math.radians(lat_deg)
    s = 1 - WGS84_E2 * math.sin(lat) ** 2
    meridian_radius = WGS84_A * (1 - WGS84_E2) / s ** 1.5
    normal_radius = WGS84_A / math.sqrt(s)
    return (lat_deg + math.degrees(north_m / meridian_radius),
            lon_deg + math.degrees(east_m / (normal_radius * math.cos(lat))))


def ground_distance_m(lat1, lon1, lat2, lon2):
    lat = math.radians((lat1 + lat2) / 2)
    s = 1 - WGS84_E2 * math.sin(lat) ** 2
    north = math.radians(lat2 - lat1) * WGS84_A * (1 - WGS84_E2) / s ** 1.5
    east = math.radians(lon2 - lon1) * WGS84_A / math.sqrt(s) * math.cos(lat)
    return math.hypot(north, east)


def project_boresight(lat, lon, camera_alt_m, ground_alt_m, r_camera):
    """Where the camera's forward axis hits flat ground at ground_alt_m, or None if it points above the horizon."""
    direction = r_camera[:, 0]
    if direction[2] <= 1e-9:
        return None
    t = (camera_alt_m - ground_alt_m) / direction[2]
    return offset_latlon(lat, lon, t * direction[0], t * direction[1])


# --- DEM -------------------------------------------------------------------------------------------

class Dem:
    """Bilinear ground-height lookup in a GeoTIFF DEM (e.g. a Copernicus GLO-30 tile, heights above mean sea level)."""

    def __init__(self, path):
        try:
            import rasterio
        except ImportError:
            sys.exit("dji mode needs rasterio to read the DEM: pip install rasterio")
        with rasterio.open(path) as dataset:
            self.heights = dataset.read(1).astype(float)
            self.inverse = ~dataset.transform
            self.nodata = dataset.nodata

    def height(self, lat, lon):
        col, row = self.inverse * (lon, lat)
        col -= 0.5  # pixel centres
        row -= 0.5
        c0, r0 = int(math.floor(col)), int(math.floor(row))
        if not (0 <= r0 < self.heights.shape[0] - 1 and 0 <= c0 < self.heights.shape[1] - 1):
            raise ValueError(f"({lat}, {lon}) is outside the DEM tile")
        fc, fr = col - c0, row - r0
        cells = self.heights[r0:r0 + 2, c0:c0 + 2]
        if self.nodata is not None and (cells == self.nodata).any():
            raise ValueError(f"DEM has no data at ({lat}, {lon})")
        top = cells[0, 0] * (1 - fc) + cells[0, 1] * fc
        bottom = cells[1, 0] * (1 - fc) + cells[1, 1] * fc
        return top * (1 - fr) + bottom * fr


# --- modes -----------------------------------------------------------------------------------------

# surrogateescape passes bytes that aren't valid UTF-8 (e.g. Latin-1 place names in DJI logs) through unchanged
def read_csv(path):
    with open(path, newline="", encoding="utf-8-sig", errors="surrogateescape") as f:
        reader = csv.DictReader(f)
        return reader.fieldnames, list(reader)


def write_csv(path, fieldnames, rows):
    with open(path, "w", newline="", encoding="utf-8", errors="surrogateescape") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def fmt(value):
    return "" if value is None else repr(round(float(value), 9))


def prep_misb(args):
    fieldnames, rows = read_csv(args.input)
    added = ["prep_yaw", "prep_gimbal_yaw", "prep_gimbal_pitch", "prep_gimbal_roll"]
    skipped = 0
    for row in rows:
        heading = to_float(row.get("PlatformHeadingAngle"))
        row["prep_yaw"] = fmt(None if heading is None else wrap180(heading))

        r_camera = misb_camera_rotation(row)
        if r_camera is None:
            skipped += 1
            yaw = pitch = roll = None
        else:
            yaw, pitch, roll = euler_from_rotation(r_camera)
        row["prep_gimbal_yaw"], row["prep_gimbal_pitch"], row["prep_gimbal_roll"] = fmt(yaw), fmt(pitch), fmt(roll)

    write_csv(args.output, fieldnames + [c for c in added if c not in fieldnames], rows)
    print(f"{len(rows)} rows written to {args.output} ({skipped} without full angles left blank)")


def prep_dji(args):
    fieldnames, rows = read_csv(args.input)
    dem = Dem(args.dem)

    # the takeoff point is fixed for a flight, so its ground height is looked up once
    home = next(((to_float(r.get("HOME.latitude")), to_float(r.get("HOME.longitude"))) for r in rows
                 if to_float(r.get("HOME.latitude")) and to_float(r.get("HOME.longitude"))), None)
    if home is None:
        sys.exit("no row has a HOME.latitude/HOME.longitude to anchor the altitude")
    home_ground_m = dem.height(*home)
    print(f"takeoff point {home} ground height {home_ground_m:.1f} m above mean sea level")

    added = ["prep_altitude_msl", "prep_fov_h", "prep_fov_v"]
    for row in rows:
        height = to_float(row.get("OSD.height [m]"))
        row["prep_altitude_msl"] = fmt(None if height is None else home_ground_m + height)
        row["prep_fov_h"], row["prep_fov_v"] = fmt(args.fov_h), fmt(args.fov_v)

    write_csv(args.output, fieldnames + [c for c in added if c not in fieldnames], rows)
    print(f"{len(rows)} rows written to {args.output}")


def check_misb(args):
    _, rows = read_csv(args.input)
    composed, naive = [], []
    for row in rows:
        values = {c: to_float(row.get(c)) for c in (
            "SensorLatitude", "SensorLongitude", "SensorTrueAltitude",
            "FrameCenterLatitude", "FrameCenterLongitude", "FrameCenterElevation",
            "PlatformHeadingAngle", "SensorRelativeAzimuth", "SensorRelativeElevation")}
        r_camera = misb_camera_rotation(row)
        if r_camera is None or any(v is None for v in values.values()):
            continue
        lat, lon, alt = values["SensorLatitude"], values["SensorLongitude"], values["SensorTrueAltitude"]
        ground = values["FrameCenterElevation"]
        target = (values["FrameCenterLatitude"], values["FrameCenterLongitude"])

        hit = project_boresight(lat, lon, alt, ground, r_camera)
        # the shortcut this tool avoids: heading + azimuth, elevation as pitch, ignoring platform pitch/roll
        shortcut = rotation(values["PlatformHeadingAngle"] + values["SensorRelativeAzimuth"],
                            values["SensorRelativeElevation"], 0)
        hit_naive = project_boresight(lat, lon, alt, ground, shortcut)
        if hit and hit_naive:
            composed.append(ground_distance_m(*hit, *target))
            naive.append(ground_distance_m(*hit_naive, *target))

    if not composed:
        sys.exit("no rows with the fields needed for the check")
    print(f"{len(composed)} rows checked against FrameCenterLatitude/Longitude (metres off):")
    for name, errors in (("rotation composition", composed), ("heading + azimuth shortcut", naive)):
        print(f"  {name:28} median {statistics.median(errors):8.1f}   max {max(errors):8.1f}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="mode", required=True)

    misb = sub.add_parser("misb", help="append absolute camera angles and a wrapped heading")
    misb.add_argument("input")
    misb.add_argument("output")
    misb.set_defaults(run=prep_misb)

    dji = sub.add_parser("dji", help="append altitude above mean sea level and the camera's field of view")
    dji.add_argument("input")
    dji.add_argument("output")
    dji.add_argument("--fov-h", type=float, required=True, help="horizontal field of view of the VIDEO frame, degrees")
    dji.add_argument("--fov-v", type=float, required=True, help="vertical field of view of the VIDEO frame, degrees")
    dji.add_argument("--dem", required=True, help="GeoTIFF DEM covering the takeoff point (heights above mean sea level)")
    dji.set_defaults(run=prep_dji)

    check = sub.add_parser("check-misb", help="validate the rotation math against the aircraft's own frame centre")
    check.add_argument("input")
    check.set_defaults(run=check_misb)

    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
