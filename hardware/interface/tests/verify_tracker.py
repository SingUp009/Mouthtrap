"""Check a tracker module (case + cover, MMI frame, theta = 0) against MMI v0.1.

Usage:
    python verify_tracker.py                      # the R20 reference in hardware/stl
    python verify_tracker.py --stl case_mmi.stl --stl cover_mmi.stl
"""
import argparse
import json
import sys
from pathlib import Path

import manifold3d as mf
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mmi  # noqa: E402
from verify_mount import Report, VOL_TOL, segments_along_x  # noqa: E402


def verify(parts, spec):
    rep = Report()
    ts = spec["tracker_side"]
    for i, p in enumerate(parts):
        rep.add(f"mesh.valid[{i}]", "required", bool(p.is_watertight and p.body_count == 1 and p.volume > 0),
                {"watertight": bool(p.is_watertight), "bodies": int(p.body_count)}, "closed single body")
    T = mf.Manifold.batch_boolean([mmi.to_manifold(p) for p in parts], mf.OpType.Add)
    blocks = mf.Manifold.batch_boolean(list(mmi.tkz_blocks(spec, grow=0.01).values()), mf.OpType.Add)
    out = (T - blocks).volume()
    rep.add("tkz.contained", "required", out <= VOL_TOL, {"volume_outside_mm3": round(out, 4)},
            "tracker inside the TKZ blocks at theta = 0 (0.01 mm numerical allowance)")
    case = parts[0]
    # Ear faces, sampled around the pivot bore at r = 3.5.
    faces = []
    for a in np.radians(np.arange(0, 360, 45) + 13.7):
        for x0, x1 in segments_along_x(case, 3.5 * np.cos(a), 3.5 * np.sin(a)):
            if x0 < 4.6 < x1:
                faces.append((x0, x1))
    f = np.array(faces)
    ok = len(f) == 8 and np.abs(f[:, 0] - ts["mating_face_x"]).max() <= 0.05 and \
        np.abs(f[:, 1] - ts["outer_face_x"]).max() <= 0.05
    rep.add("ear.faces", "required", bool(ok),
            {"mating_face_x": round(float(np.median(f[:, 0])), 4) if len(f) else None,
             "outer_face_x": round(float(np.median(f[:, 1])), 4) if len(f) else None},
            f"{ts['mating_face_x']} / {ts['outer_face_x']} +/-0.05")
    x0, x1 = ts["mating_face_x"] + 0.02, ts["outer_face_x"] - 0.02
    bore = (mmi.cyl_x(spec["hmd_side"]["bore_diameter_min"] / 2, x0, x1) ^ mmi.to_manifold(case)).volume()
    rep.add("ear.pivot_bore", "required", bore <= VOL_TOL, {"material_mm3": round(bore, 4)}, "free Ø3.35")
    # The M3 shank must travel along the slot over the functional range.
    shank = mmi.to_manifold(case)
    lim = spec["hinge"]["functional_range_deg"]
    blocked = []
    for th in np.arange(-lim, lim + 1e-9, 0.5):
        y, z = 9 * np.cos(np.radians(-th)), 9 * np.sin(np.radians(-th))
        v = (mmi.cyl_x(spec["fasteners"]["shank_radius"], x0, x1, y, z) ^ shank).volume()
        if v > VOL_TOL:
            blocked.append(float(th))
    rep.add("ear.slot_travel", "required", not blocked, {"blocked_at_deg": blocked},
            f"stop shank free over +/-{lim} deg")
    return rep


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stl", action="append")
    ap.add_argument("--out", default=str(Path(__file__).parent / "results" / "r20_tracker.json"))
    a = ap.parse_args()
    spec = mmi.load_spec()
    parts = [mmi.load_mesh(p) for p in a.stl] if a.stl else mmi.load_reference_tracker()
    rep = verify(parts, spec)
    failed = [c["id"] for c in rep.checks if c["status"] == "fail"]
    Path(a.out).write_text(json.dumps({"mmi_version": spec["version"], "input": a.stl or "R20 reference",
                                       "conforms": not failed, "checks": rep.checks}, indent=2), encoding="utf-8")
    print("CONFORMS" if not failed else f"DOES NOT CONFORM: {failed}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
