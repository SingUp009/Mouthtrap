"""Render the OpenSCAD library and coupons and check them against the Python reference.

Usage: python test_scad.py [--openscad PATH]   ->  results/scad_checks.json
Needs OpenSCAD on PATH (2021.01 or later).
"""
import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import manifold3d as mf
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mmi  # noqa: E402
import verify_mount  # noqa: E402
import verify_tracker  # noqa: E402

CAD = mmi.INTERFACE / "cad"


def render(openscad, scad, out, defines=()):
    cmd = [openscad, "-q", "-o", str(out)]
    for k, v in defines:
        cmd += ["-D", f"{k}={json.dumps(v)}"]
    subprocess.run(cmd + [str(scad)], check=True, timeout=1800)
    return mmi.load_mesh(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--openscad", default=shutil.which("openscad"))
    a = ap.parse_args()
    if not a.openscad:
        sys.exit("OpenSCAD not found")
    spec = mmi.load_spec()
    res = {}
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        # 1. TKZ: OpenSCAD (2D sweeps) vs Python (3D hulls) from the JSON spec.
        src = tmp / "tkz.scad"
        src.write_text(f'include <{(CAD / "mmi.scad").as_posix()}>\nmmi_tkz();\n')
        S = mmi.to_manifold(render(a.openscad, src, tmp / "tkz.stl"))
        P = mmi.tkz(spec)
        xor = (S - P).volume() + (P - S).volume()
        res["tkz_scad_vs_json"] = {"scad_mm3": round(S.volume(), 3), "json_mm3": round(P.volume(), 3),
                                   "symmetric_difference_mm3": round(xor, 4),
                                   "pass": xor / P.volume() < 1e-4}
        # 2. Coupons in the MMI frame.
        hmd = render(a.openscad, CAD / "mmi_fit_coupon.scad", tmp / "h.stl", [("part", "hmd"), ("frame", "mmi")])
        trk = render(a.openscad, CAD / "mmi_fit_coupon.scad", tmp / "t.stl", [("part", "tracker"), ("frame", "mmi")])
        rep = verify_mount.verify(hmd, spec, mmi.load_reference_tracker())
        res["hmd_coupon_vs_mmi"] = {c["id"]: c["status"] for c in rep.checks}
        rep = verify_tracker.verify([trk], spec)
        res["tracker_coupon_vs_mmi"] = {c["id"]: c["status"] for c in rep.checks}
        # 3. Coupon pair, and the tracker coupon against the R20 CentralMount, over the mechanical range.
        reg = json.loads((mmi.INTERFACE / "reference" / "r20_registration.json").read_text(encoding="utf-8"))
        r20 = mmi.load_mesh(mmi.HARDWARE / reg["parts"]["CentralMount"]["file"],
                            reg["parts"]["CentralMount"]["mmi_to_file"])
        T = mmi.to_manifold(trk)
        for label, other in (("coupon_pair", hmd), ("tracker_coupon_vs_r20_mount", r20)):
            O = mmi.to_manifold(other)
            rows = [(float(t), (mmi.rot_x(T, t) ^ O).volume(), mmi.rot_x(T, t).min_gap(O, 2.0))
                    for t in np.append(np.arange(-16.3, 16.3, 0.5), 16.3)]
            res[label] = {"max_intersection_mm3": round(max(r[1] for r in rows), 4),
                          "min_gap_mm": round(min(r[2] for r in rows), 4),
                          "pass": max(r[1] for r in rows) <= verify_mount.VOL_TOL}
        # Pivot and stop bores line up when the pair is assembled: bolt shanks clear both parts.
        Hm = mmi.to_manifold(hmd)
        shanks = mf.Manifold.batch_boolean(
            [mmi.cyl_x(1.5, -11.8, 8.2, *spec["axes"][k]) for k in ("pivot", "stop")], mf.OpType.Add)
        res["coupon_pair_bolts_clear"] = {"material_mm3": round(((Hm + T) ^ shanks).volume(), 4)}
        # 4. The mount template as shipped (placeholder attachment) must conform.
        tpl = render(a.openscad, mmi.HARDWARE / "mounts" / "_template" / "mount_template.scad", tmp / "tpl.stl")
        rep = verify_mount.verify(tpl, spec, mmi.load_reference_tracker())
        res["template_vs_mmi"] = {c["id"]: c["status"] for c in rep.checks}
        res["template_measured"] = {c["id"]: c["measured"] for c in rep.checks
                                    if c["id"] in ("tkz.clearance", "stop.pocket_wall", "pivot.pocket_wall")}
        # 5. Print orientation: the mating face sits on the bed.
        for part in ("hmd", "tracker"):
            m = render(a.openscad, CAD / "mmi_fit_coupon.scad", tmp / f"{part}_p.stl", [("part", part)])
            z0 = m.bounds[0][2]
            bed = m.triangles_center[:, 2] < z0 + 1e-4
            res[f"{part}_coupon_print"] = {"bbox_mm": np.round(m.extents, 3).tolist(),
                                          "area_on_bed_mm2": round(float(m.area_faces[bed].sum()), 2),
                                          "watertight": bool(m.is_watertight)}
    ok = (res["tkz_scad_vs_json"]["pass"] and res["coupon_pair"]["pass"]
          and res["tracker_coupon_vs_r20_mount"]["pass"]
          and "fail" not in res["hmd_coupon_vs_mmi"].values() and "fail" not in res["template_vs_mmi"].values() and "fail" not in res["tracker_coupon_vs_mmi"].values()
          and res["coupon_pair_bolts_clear"]["material_mm3"] <= verify_mount.VOL_TOL)
    res["all_pass"] = ok
    out = Path(__file__).parent / "results" / "scad_checks.json"
    out.write_text(json.dumps(res, indent=2), encoding="utf-8")
    print(json.dumps(res, indent=1))
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
