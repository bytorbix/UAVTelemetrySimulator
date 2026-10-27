"""
Builds the Flights folder: one folder per dataset with

    video.ts         the video stream only (no audio, no KLV), remuxed without re-encoding
    telemetry.csv    MISB ST 0601 columns, one row per sample, trimmed to the video
    dataset.json     where it came from, camera, DEM tile, what is measured vs assumed

Every dataset, DJI or MISB, ends up with the same columns (see COLUMNS / README.md).

Usage (needs numpy, rasterio, pyproj, ffmpeg/ffprobe on PATH):
    python build_flights.py                 # all datasets
    python build_flights.py DJI_0001 falls  # only these
    python build_flights.py --no-video      # telemetry + dataset.json only
    python build_flights.py --datasets "S:/UAVMapper Project/Datasets"   # run from anywhere (e.g. the repo copy)
"""

import argparse
import csv
import datetime as dt
import json
import math
import os
import subprocess
import sys

import numpy as np

import klv0601

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # .../Datasets
FLIGHTS = os.path.join(ROOT, "Flights")
DJI_SRC = os.path.join(ROOT, "QGISFMV_Samples", "QGISFMV_Samples", "DJI")
MISB_SRC = os.path.join(ROOT, "QGISFMV_Samples", "QGISFMV_Samples", "MISB")
DEM_DIR = os.path.join(ROOT, "DEM")

def set_root(root):
    global ROOT, FLIGHTS, DJI_SRC, MISB_SRC, DEM_DIR
    ROOT = root
    FLIGHTS = os.path.join(ROOT, "Flights")
    DJI_SRC = os.path.join(ROOT, "QGISFMV_Samples", "QGISFMV_Samples", "DJI")
    MISB_SRC = os.path.join(ROOT, "QGISFMV_Samples", "QGISFMV_Samples", "MISB")
    DEM_DIR = os.path.join(ROOT, "DEM")


COLUMNS = [
    "PrecisionTimeStamp",            # tag 2   microseconds since 1970-01-01 UTC
    "pts_time",                      # -       seconds since the first video frame
    "PlatformHeadingAngle",          # tag 5   deg, 0..360, clockwise from true north
    "PlatformPitchAngle",            # tag 6   deg, nose up positive
    "PlatformRollAngle",             # tag 7   deg, right wing down positive
    "SensorLatitude",                # tag 13  deg WGS-84
    "SensorLongitude",               # tag 14  deg WGS-84
    "SensorTrueAltitude",            # tag 15  m above mean sea level
    "SensorEllipsoidHeight",         # tag 75  m above the WGS-84 ellipsoid
    "SensorHorizontalFieldOfView",   # tag 16  deg
    "SensorVerticalFieldOfView",     # tag 17  deg
    "SensorRelativeAzimuthAngle",    # tag 18  deg, 0..360, relative to the platform's nose
    "SensorRelativeElevationAngle",  # tag 19  deg, negative = looking down
    "SensorRelativeRollAngle",       # tag 20  deg, 0..360
    "FrameCenterLatitude",           # tag 23  optional (blank for DJI)
    "FrameCenterLongitude",          # tag 24  optional
    "FrameCenterElevation",          # tag 25  optional
    "SlantRange",                    # tag 21  optional
]
REQUIRED = COLUMNS[:14]

# DJI field of view is NOT measured; these are spec-sheet assumptions (16:9 video, full sensor width)
DJI_FOV_ASSUMED = {
    "P4": (81.7, 51.9, "Phantom 4, 20 mm eq., 94 deg diagonal"),
    "P3 Advanced": (81.7, 51.9, "Phantom 3 Advanced, same lens as P4"),
    "Mavic": (67.3, 41.1, "assumed Mavic Pro, 26 mm eq., 78.8 deg diagonal"),
}

DATASETS = {
    # name: (kind, source video, telemetry source / klv stream index, extra caveats)
    "DJI_0001": ("dji", "DJI_0001/DJI_0001.MOV", "DJI_0001/DJI_0001-TxtLogToCsv.csv", []),
    "DJI_0002": ("dji", "DJI_0002/DJI_0002.MOV", "DJI_0002/telemetry.csv",
                 ["The log holds two recordings; this video is the one whose length matches."]),
    "DJI_0047": ("dji", "DJI_0047/DJI_0047.mp4", "DJI_0047/telemetry.csv",
                 ["The log starts mid-recording (no record-start event), so video t=0 is taken as the first log row "
                  "(Esri_multiplexer_0, the same flight, makes the same assumption). Possible offset of 1-2 s.",
                  "A P3 Advanced records at most 2.7K; this 4K video was likely re-encoded."]),
    "DJI_0732": ("dji", "DJI_0732/DJI_0732.MP4", "DJI_0732/DJI_0732-TxtLogToCsv.csv", []),
    "DJI_0872": ("dji", "DJI_0872/DJI_0872.MP4", "DJI_0872/DJI_0872-TxtLogToCsv.csv", []),
    "Cheyenne": ("misb", "Cheyenne.ts", None, []),
    "Cheyenne_VAhospital": ("misb", "CheyenneVAhospital.mpeg4", None, []),
    "Cheyenne_Handoff": ("misb", "Cheyenne_Handoff.H264", None, []),
    "Truck": ("misb", "Truck.H264", None, []),
    "falls": ("misb", "falls.ts", 3,
              ["The file has two KLV streams; stream 3 (ST 0601 v7) is used. Stream 1 has UTC stamps 7 h off "
               "(local time labelled as UTC)."]),
    "klv_metadata_test_sync": ("misb", "klv_metadata_test_sync.ts", None,
                               ["Its own frame centre is ~4.5 deg off its heading + azimuth (~313 m), so the "
                                "orientation may not be trustworthy."]),
    "Esri_multiplexer_0": ("misb", "Esri_multiplexer_0.mp4", None,
                           ["Same flight as DJI_0047, converted to MISB by Esri's multiplexer.",
                            "PrecisionTimeStamp is local time (EDT) labelled as UTC: about 4 h early. "
                            "Only 1 KLV packet per second."]),
    "Esri_multiplexer_1": ("misb", "Esri_multiplexer_1.mp4", None,
                           ["Esri multiplexer output from the same site as DJI_0047 (no DJI log here).",
                            "PrecisionTimeStamp is likely local time (EDT) labelled as UTC. Only 1 KLV packet per second."]),
}


# --- geometry ----------------------------------------------------------------------------------------
# NED world frame, body x forward / y right / z down, R = Rz(yaw) Ry(pitch) Rx(roll)

def rotation(yaw, pitch, roll):
    y, p, r = (math.radians(a) for a in (yaw, pitch, roll))
    rz = np.array([[math.cos(y), -math.sin(y), 0], [math.sin(y), math.cos(y), 0], [0, 0, 1]])
    ry = np.array([[math.cos(p), 0, math.sin(p)], [0, 1, 0], [-math.sin(p), 0, math.cos(p)]])
    rx = np.array([[1, 0, 0], [0, math.cos(r), -math.sin(r)], [0, math.sin(r), math.cos(r)]])
    return rz @ ry @ rx


def euler(r):
    yaw = math.degrees(math.atan2(r[1, 0], r[0, 0]))
    pitch = math.degrees(math.asin(max(-1.0, min(1.0, -r[2, 0]))))
    roll = math.degrees(math.atan2(r[2, 1], r[2, 2]))
    return yaw, pitch, roll


def wrap360(a):
    return a % 360.0


# --- heights -----------------------------------------------------------------------------------------

_geoid = {}


def geoid_offset(lat, lon, model):
    """Geoid height N (m) so that ellipsoid height = MSL height + N. model: 'EGM2008' or 'EGM96'."""
    import pyproj
    from pyproj import Transformer
    if model not in _geoid:
        pyproj.network.set_network_enabled(True)
        src = {"EGM2008": "EPSG:4326+3855", "EGM96": "EPSG:4326+5773"}[model]
        _geoid[model] = Transformer.from_crs(src, "EPSG:4979", always_xy=True)
    _, _, h = _geoid[model].transform(lon, lat, 0.0)
    if not math.isfinite(h):
        raise RuntimeError(f"no {model} geoid value at {lat},{lon} (grid not reachable?)")
    return h


def dem_tile_name(lat, lon):
    la, lo = math.floor(lat), math.floor(lon)
    return (f"Copernicus_DSM_COG_10_{'N' if la >= 0 else 'S'}{abs(la):02d}_00_"
            f"{'E' if lo >= 0 else 'W'}{abs(lo):03d}_00_DEM")


def dem_height(path, lat, lon):
    import rasterio
    with rasterio.open(path) as ds:
        heights = ds.read(1).astype(float)
        col, row = (~ds.transform) * (lon, lat)
        nodata = ds.nodata
    col -= 0.5
    row -= 0.5
    c0, r0 = int(math.floor(col)), int(math.floor(row))
    fc, fr = col - c0, row - r0
    cells = heights[r0:r0 + 2, c0:c0 + 2]
    if nodata is not None and (cells == nodata).any():
        raise ValueError(f"DEM has no data at {lat},{lon}")
    top = cells[0, 0] * (1 - fc) + cells[0, 1] * fc
    bottom = cells[1, 0] * (1 - fc) + cells[1, 1] * fc
    return float(top * (1 - fr) + bottom * fr)


# --- helpers -----------------------------------------------------------------------------------------

def num(v):
    try:
        f = float(v)
        return f if math.isfinite(f) else None
    except (TypeError, ValueError):
        return None


def fmt(v, digits=9):
    if v is None:
        return ""
    if isinstance(v, int):
        return str(v)
    return repr(round(float(v), digits))


def utc_iso(us):
    return dt.datetime.fromtimestamp(us / 1e6, dt.timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def forward_fill(rows, keys):
    last = {}
    for r in rows:
        for k in keys:
            if r.get(k) is None:
                if k in last:
                    r[k] = last[k]
            else:
                last[k] = r[k]
    return rows


def trim_to_video(rows, duration, fps):
    # half a frame of slack: container start times are often a few microseconds off the first packet
    slack = 0.5 / fps
    return [r for r in rows if r["pts_time"] >= -slack and (duration is None or r["pts_time"] <= duration + slack)]


# --- DJI ---------------------------------------------------------------------------------------------

def read_dji(path):
    with open(path, newline="", encoding="utf-8-sig", errors="surrogateescape") as f:
        return list(csv.DictReader(f))


def dji_time_us(text):
    text = text.strip()
    # DJI logs drop the fraction on whole seconds ("17:48:00")
    t = dt.datetime.strptime(text, "%Y/%m/%d %H:%M:%S.%f" if "." in text else "%Y/%m/%d %H:%M:%S")
    t = t.replace(tzinfo=dt.timezone.utc)
    return int(round(t.timestamp() * 1e6))


def recording_segments(raw):
    segs, start = [], None
    for i, r in enumerate(raw):
        rec = r.get("CUSTOM.isVideo", "") == "Recording"
        if rec and start is None:
            start = i
        if not rec and start is not None:
            segs.append((start, i - 1))
            start = None
    if start is not None:
        segs.append((start, len(raw) - 1))
    return segs


def build_dji(name, video_rel, csv_rel, video, notes):
    raw = read_dji(os.path.join(DJI_SRC, csv_rel))
    times = [dji_time_us(r["CUSTOM.updateTime"]) for r in raw]

    segs = recording_segments(raw)
    if not segs:
        sys.exit(f"{name}: no Recording rows in the log")
    # the recording whose length best matches the video
    best = min(segs, key=lambda s: abs((times[s[1]] - times[s[0]]) / 1e6 - video["duration"]))
    t0 = times[best[0]]
    seg_len = (times[best[1]] - times[best[0]]) / 1e6

    drone = raw[0].get("OSD.droneType", "").strip()
    fov_h, fov_v, fov_note = DJI_FOV_ASSUMED.get(drone, (None, None, f"unknown drone type {drone!r}"))

    home = next(((num(r.get("HOME.latitude")), num(r.get("HOME.longitude"))) for r in raw
                 if num(r.get("HOME.latitude")) and num(r.get("HOME.longitude"))), None)
    if home is None:
        sys.exit(f"{name}: no HOME.latitude/longitude to anchor the altitude")
    tile = dem_tile_name(*home)
    dem_path = os.path.join(DEM_DIR, tile + ".tif")
    home_ground = dem_height(dem_path, *home)
    n_geoid = geoid_offset(*home, "EGM2008")  # Copernicus DEM heights are EGM2008

    rows = []
    for r, t in zip(raw, times):
        pts = (t - t0) / 1e6
        lat, lon = num(r.get("OSD.latitude")), num(r.get("OSD.longitude"))
        if not lat or not lon:
            lat = lon = None
        yaw, pitch, roll = num(r.get("OSD.yaw")), num(r.get("OSD.pitch")), num(r.get("OSD.roll"))
        gy, gp, gr = num(r.get("GIMBAL.yaw")), num(r.get("GIMBAL.pitch")), num(r.get("GIMBAL.roll"))
        height = num(r.get("OSD.height [m]"))
        row = {"PrecisionTimeStamp": t, "pts_time": pts,
               "PlatformHeadingAngle": None if yaw is None else wrap360(yaw),
               "PlatformPitchAngle": pitch, "PlatformRollAngle": roll,
               "SensorLatitude": lat, "SensorLongitude": lon,
               "SensorTrueAltitude": None if height is None else home_ground + height,
               "SensorHorizontalFieldOfView": fov_h, "SensorVerticalFieldOfView": fov_v}
        row["SensorEllipsoidHeight"] = None if row["SensorTrueAltitude"] is None else row["SensorTrueAltitude"] + n_geoid
        if None not in (yaw, pitch, roll, gy, gp, gr):
            # DJI gimbal angles are world-referenced; MISB wants them relative to the airframe
            rel = rotation(yaw, pitch, roll).T @ rotation(gy, gp, gr)
            az, el, rl = euler(rel)
            row.update(SensorRelativeAzimuthAngle=wrap360(az), SensorRelativeElevationAngle=el,
                       SensorRelativeRollAngle=wrap360(rl))
        rows.append(row)

    rows = forward_fill(rows, COLUMNS)
    rows = trim_to_video(rows, video["duration"], video["fps"])
    rows = [r for r in rows if all(r.get(k) is not None for k in REQUIRED)]

    meta = {
        "source": {"kind": "DJI flight log + video", "video": os.path.join("QGISFMV_Samples", "QGISFMV_Samples", "DJI", video_rel),
                   "telemetry": os.path.join("QGISFMV_Samples", "QGISFMV_Samples", "DJI", csv_rel)},
        "platform": f"DJI {drone}",
        "alignment": {"method": "video t=0 = first row of the matching 'Recording' segment in CUSTOM.isVideo",
                      "recording_segment_s": round(seg_len, 3), "segments_in_log": len(segs)},
        "heights": {"SensorTrueAltitude": f"DEM ground height at takeoff ({home_ground:.1f} m, {tile}) + OSD.height",
                    "SensorEllipsoidHeight": f"SensorTrueAltitude + EGM2008 geoid offset at takeoff ({n_geoid:.2f} m)"},
        "orientation": "PlatformHeading = OSD.yaw wrapped to 0..360; Sensor relative angles = airframe^-1 x absolute gimbal (GIMBAL.yaw/pitch/roll)",
        "fov": {"source": "assumed", "h": fov_h, "v": fov_v, "note": fov_note + " - needs research"},
        "dem": {"tile": tile, "available": os.path.exists(dem_path), "takeoff": list(home)},
        "caveats": notes,
    }
    return rows, meta


# --- MISB --------------------------------------------------------------------------------------------

def build_misb(name, video_rel, stream, video, notes):
    path = os.path.join(MISB_SRC, video_rel)
    streams = klv0601.klv_streams(path)
    if not streams:
        sys.exit(f"{name}: no KLV stream")
    stream = stream if stream is not None else streams[0]
    packets = klv0601.read_klv(path, stream)
    first = packets[0]

    rows = []
    for p in packets:
        row = {k: p.get(k) for k in COLUMNS}
        row["pts_time"] = p["pts_time"] - video["start_time"]
        rows.append(row)
    native_ellipsoid = any(r.get("SensorEllipsoidHeight") is not None for r in rows)
    rows = forward_fill(rows, COLUMNS)
    klv_first, klv_last = rows[0]["pts_time"], rows[-1]["pts_time"]
    rows = trim_to_video(rows, video["duration"], video["fps"])

    lat0 = next(r["SensorLatitude"] for r in rows if r.get("SensorLatitude") is not None)
    lon0 = next(r["SensorLongitude"] for r in rows if r.get("SensorLongitude") is not None)
    if not native_ellipsoid:
        n_geoid = geoid_offset(lat0, lon0, "EGM96")  # ST 0601 altitudes are MSL per EGM96
        for r in rows:
            if r.get("SensorTrueAltitude") is not None:
                r["SensorEllipsoidHeight"] = r["SensorTrueAltitude"] + n_geoid
    rows = [r for r in rows if all(r.get(k) is not None for k in REQUIRED)]

    tile = dem_tile_name(lat0, lon0)
    meta = {
        "source": {"kind": "MISB ST 0601 KLV in video", "video": os.path.join("QGISFMV_Samples", "QGISFMV_Samples", "MISB", video_rel),
                   "klv_stream_index": stream, "klv_streams_in_file": streams},
        "platform": first.get("PlatformDesignation") or "unknown",
        "uas_ls_version": first.get("UasLdsVersionNumber"),
        "alignment": {"method": "pts_time = KLV packet pts - first video frame pts; packets outside the video dropped",
                      "klv_span_s_relative_to_video": [round(klv_first, 3), round(klv_last, 3)]},
        "heights": {"SensorTrueAltitude": "from KLV (tag 15)",
                    "SensorEllipsoidHeight": "from KLV (tag 75)" if native_ellipsoid else
                    f"SensorTrueAltitude + EGM96 geoid offset ({n_geoid:.2f} m)"},
        "orientation": "from KLV (tags 5-7 platform, 18-20 sensor relative)",
        "fov": {"source": "KLV (tags 16-17)"},
        "dem": {"tile": tile, "available": os.path.exists(os.path.join(DEM_DIR, tile + ".tif"))},
        "caveats": notes,
    }
    return rows, meta


# --- output ------------------------------------------------------------------------------------------

def remux_video(src, dst):
    tmp = dst + ".part"
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", src, "-map", "0:v:0", "-c", "copy", "-f", "mpegts", tmp], check=True)
    os.replace(tmp, dst)


def write_dataset(name, rows, meta, video, src_video, do_video):
    out = os.path.join(FLIGHTS, name)
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "telemetry.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(COLUMNS)
        for r in rows:
            w.writerow([fmt(r.get(c)) if c != "PrecisionTimeStamp" else str(int(r[c])) for c in COLUMNS])

    span = rows[-1]["pts_time"] - rows[0]["pts_time"] if len(rows) > 1 else 0
    meta = {"name": name, **meta,
            "video": {"file": "video.ts", "codec": video["codec"], "width": video["width"], "height": video["height"],
                      "fps": round(video["fps"], 3), "duration_s": round(video["duration"], 3)},
            "telemetry": {"file": "telemetry.csv", "rows": len(rows),
                          "rate_hz": round((len(rows) - 1) / span, 2) if span else None,
                          "first_pts_s": round(rows[0]["pts_time"], 3), "last_pts_s": round(rows[-1]["pts_time"], 3),
                          "utc_start": utc_iso(rows[0]["PrecisionTimeStamp"]), "utc_end": utc_iso(rows[-1]["PrecisionTimeStamp"])},
            "built": dt.date.today().isoformat()}
    with open(os.path.join(out, "dataset.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2, ensure_ascii=False)

    if do_video:
        remux_video(src_video, os.path.join(out, "video.ts"))
    return meta


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("names", nargs="*")
    ap.add_argument("--no-video", action="store_true")
    ap.add_argument("--datasets", help="the Datasets folder (default: the one this script sits in, e.g. Datasets/Flights/_tools)")
    args = ap.parse_args()
    if args.datasets:
        set_root(args.datasets)

    for name in args.names or DATASETS:
        kind, video_rel, tel, notes = DATASETS[name]
        src_video = os.path.join(DJI_SRC if kind == "dji" else MISB_SRC, video_rel)
        video = klv0601.video_info(src_video)
        if kind == "dji":
            rows, meta = build_dji(name, video_rel, tel, video, notes)
        else:
            rows, meta = build_misb(name, video_rel, tel, video, notes)
        m = write_dataset(name, rows, meta, video, src_video, not args.no_video)
        t = m["telemetry"]
        print(f"{name:24} {t['rows']:5} rows {t['rate_hz']} Hz  pts {t['first_pts_s']}..{t['last_pts_s']} "
              f"/ video {m['video']['duration_s']} s  {t['utc_start']}")


if __name__ == "__main__":
    main()
