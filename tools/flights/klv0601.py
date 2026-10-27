"""
Minimal MISB ST 0601 (UAS Datalink Local Set) decoder.

Reads the KLV data stream of a video with ffprobe and returns one dict per KLV packet,
with its pts_time (seconds, the file's own timeline) and the decoded ST 0601 items we use.
Only the items Marco Polo needs are decoded; everything else is skipped.
"""

import json
import subprocess

UAS_LS_KEY = bytes.fromhex("060E2B34020B01010E01030101000000")


def _ber_length(buf, i):
    first = buf[i]
    if first < 0x80:
        return first, i + 1
    n = first & 0x7F
    return int.from_bytes(buf[i + 1:i + 1 + n], "big"), i + 1 + n


def _ber_oid(buf, i):
    value = 0
    while True:
        b = buf[i]
        value = (value << 7) | (b & 0x7F)
        i += 1
        if b < 0x80:
            return value, i


def _uint(raw, lo, hi):
    n = len(raw)
    return lo + int.from_bytes(raw, "big") * (hi - lo) / (2 ** (8 * n) - 1)


def _int(raw, half_range):
    n = len(raw)
    v = int.from_bytes(raw, "big", signed=True)
    if v == -(2 ** (8 * n - 1)):  # "out of range" / error indicator
        return None
    return v * (2 * half_range) / (2 ** (8 * n) - 2)


def _text(raw):
    return raw.decode("latin-1").strip("\x00 ")


# tag -> (name, decoder)
DECODERS = {
    2: ("PrecisionTimeStamp", lambda r: int.from_bytes(r, "big")),
    3: ("MissionID", _text),
    5: ("PlatformHeadingAngle", lambda r: _uint(r, 0, 360)),
    6: ("PlatformPitchAngle", lambda r: _int(r, 20)),
    7: ("PlatformRollAngle", lambda r: _int(r, 50)),
    10: ("PlatformDesignation", _text),
    11: ("ImageSourceSensor", _text),
    13: ("SensorLatitude", lambda r: _int(r, 90)),
    14: ("SensorLongitude", lambda r: _int(r, 180)),
    15: ("SensorTrueAltitude", lambda r: _uint(r, -900, 19000)),
    16: ("SensorHorizontalFieldOfView", lambda r: _uint(r, 0, 180)),
    17: ("SensorVerticalFieldOfView", lambda r: _uint(r, 0, 180)),
    18: ("SensorRelativeAzimuthAngle", lambda r: _uint(r, 0, 360)),
    19: ("SensorRelativeElevationAngle", lambda r: _int(r, 180)),
    20: ("SensorRelativeRollAngle", lambda r: _uint(r, 0, 360)),
    21: ("SlantRange", lambda r: _uint(r, 0, 5_000_000)),
    23: ("FrameCenterLatitude", lambda r: _int(r, 90)),
    24: ("FrameCenterLongitude", lambda r: _int(r, 180)),
    25: ("FrameCenterElevation", lambda r: _uint(r, -900, 19000)),
    65: ("UasLdsVersionNumber", lambda r: int.from_bytes(r, "big")),
    75: ("SensorEllipsoidHeight", lambda r: _uint(r, -900, 19000)),
    90: ("PlatformPitchAngleFull", lambda r: _int(r, 90)),
    91: ("PlatformRollAngleFull", lambda r: _int(r, 90)),
}


def decode_local_set(value):
    out = {}
    i = 0
    while i < len(value):
        tag, i = _ber_oid(value, i)
        length, i = _ber_length(value, i)
        raw = value[i:i + length]
        i += length
        if tag in DECODERS and length > 0:
            name, fn = DECODERS[tag]
            try:
                out[name] = fn(raw)
            except Exception:
                pass
    # the full-range variants win over the limited ones when both are present
    if out.get("PlatformPitchAngleFull") is not None:
        out["PlatformPitchAngle"] = out["PlatformPitchAngleFull"]
    if out.get("PlatformRollAngleFull") is not None:
        out["PlatformRollAngle"] = out["PlatformRollAngleFull"]
    out.pop("PlatformPitchAngleFull", None)
    out.pop("PlatformRollAngleFull", None)
    return out


def split_packets(buf):
    """A data packet can hold several KLV units; yield the UAS Local Set values."""
    i = 0
    while i + 16 < len(buf):
        j = buf.find(UAS_LS_KEY, i)
        if j < 0:
            return
        length, k = _ber_length(buf, j + 16)
        yield buf[k:k + length]
        i = k + length


def _hexdump_to_bytes(dump):
    out = bytearray()
    for line in dump.splitlines():
        # ffprobe: "00000000: 060e 2b34 ...  ascii"
        if ":" not in line:
            continue
        hexpart = line.split(":", 1)[1][:40]
        out += bytes.fromhex(hexpart.replace(" ", ""))
    return bytes(out)


def klv_streams(path):
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "stream=index,codec_type,codec_name",
         "-of", "json", path], capture_output=True, text=True, check=True)
    return [s["index"] for s in json.loads(probe.stdout)["streams"]
            if s.get("codec_type") == "data" and s.get("codec_name") == "klv"]


def read_klv(path, stream_index):
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", str(stream_index), "-show_packets", "-show_data",
         "-show_entries", "packet=pts_time,dts_time,data", "-of", "json", path],
        capture_output=True, text=True, check=True)
    rows = []
    for pkt in json.loads(probe.stdout).get("packets", []):
        t = pkt.get("pts_time") or pkt.get("dts_time")
        if t in (None, "N/A"):
            continue
        for value in split_packets(_hexdump_to_bytes(pkt.get("data", ""))):
            row = decode_local_set(value)
            if row:
                row["pts_time"] = float(t)
                rows.append(row)
    return rows


def video_info(path):
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
         "stream=start_time,duration,width,height,r_frame_rate,codec_name:format=duration,start_time",
         "-of", "json", path], capture_output=True, text=True, check=True)
    j = json.loads(probe.stdout)
    s = j["streams"][0]
    fmt = j.get("format", {})
    num, den = s["r_frame_rate"].split("/")
    duration = s.get("duration") or fmt.get("duration")
    return {
        "codec": s["codec_name"],
        "width": int(s["width"]),
        "height": int(s["height"]),
        "fps": float(num) / float(den),
        "start_time": float(s.get("start_time") or fmt.get("start_time") or 0.0),
        "duration": float(duration) if duration not in (None, "N/A") else None,
    }
