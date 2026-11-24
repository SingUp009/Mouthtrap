"""Check an HMD-side mount against MMI v0.1 (CAD checks only; not a substitute for a print or wear test).

Usage:
    python verify_mount.py ../../mounts/quest-3s/mount.json
    python verify_mount.py --stl my_mount_mmi.stl            # STL already in the MMI frame
Writes <mount dir>/verification/mmi-v0.1.json (or --out) and exits 1 if a required check fails.
"""
import argparse
import hashlib
import json
import platform
import sys
from pathlib import Path

import manifold3d as mf
import numpy as np
import shapely
import trimesh
from shapely.geometry import Point, Polygon

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mmi  # noqa: E402

VOL_TOL = 0.01  # mm^3, numerical allowance for float32 booleans


class Report:
    def __init__(self):
        self.checks = []

    def add(self, cid, level, ok, measured, requirement):
        status = "pass" if ok else ("fail" if level == "required" else "warn")
        self.checks.append({"id": cid, "level": level, "status": status,
                            "measured": measured, "requirement": requirement})
        print(f"  [{status.upper():4}] {cid}: {json.dumps(measured)}")


def segments_along_x(mesh, y, z):
    loc, _, _ = mesh.ray.intersects_location([[-1000.0, y, z]], [[1.0, 0.0, 0.0]])
    xs = sorted(np.asarray(loc).reshape(-1, 3)[:, 0])
    return list(zip(xs[0::2], xs[1::2]))


def section_polygons(mesh, x):
    s = mesh.section(plane_origin=[x, 0, 0], plane_normal=[1, 0, 0])
    if s is None:
        return []
    rings = [Polygon(d[:, 1:3]) for d in s.discrete if len(d) > 3]
    rings = [r if r.is_valid else r.buffer(0) for r in rings]
    # Build polygons with holes by containment parity.
    rings.sort(key=lambda r: -r.area)
    polys = []
    for r in rings:
        for i, p in enumerate(polys):
            if p.contains(r.representative_point()):
                polys[i] = p.difference(r)
                break
        else:
            polys.append(r)
    out = []
    for p in polys:
        out.extend(p.geoms if hasattr(p, "geoms") else [p])
    return out


def min_width(poly):
    hull = np.asarray(poly.convex_hull.exterior.coords)
    best = np.inf
    for a, b in zip(hull[:-1], hull[1:]):
        d = b - a
        n = np.array([-d[1], d[0]]) / np.linalg.norm(d)
        proj = hull @ n
        best = min(best, proj.max() - proj.min())
    return float(best)


def verify(mesh, spec, tracker_parts=None):
    rep = Report()
    hs, fs = spec["hmd_side"], spec["fasteners"]
    face, seat = hs["mating_face_x"], hs["nut_seat_x"]

    # 1. Mesh validity
    ok = bool(mesh.is_watertight and mesh.is_winding_consistent and mesh.body_count == 1 and mesh.volume > 0)
    rep.add("mesh.valid", "required", ok,
            {"watertight": bool(mesh.is_watertight), "winding_consistent": bool(mesh.is_winding_consistent),
             "bodies": int(mesh.body_count), "volume_mm3": round(float(mesh.volume), 3),
             "triangles": int(len(mesh.faces))}, "closed, consistently wound, one body, positive volume")
    if not ok:
        return rep
    M = mmi.to_manifold(mesh)

    for name in ("pivot", "stop"):
        y, z = spec["axes"][name]
        # 2. Bore: free cylinder of the minimum diameter, measured diameter and centre at mid-depth.
        free = (mmi.cyl_x(hs["bore_diameter_min"] / 2, seat + 0.02, face - 0.02, y, z) ^ M).volume()
        xm = (seat + face) / 2
        hole = None
        for p in section_polygons(mesh, xm):
            for h in p.interiors:
                hp = Polygon(h)
                if hp.contains(Point(y, z)):
                    hole = hp
        if hole is None:
            rep.add(f"{name}.bore", "required", False, {"found": False}, "through bore on the axis")
            continue
        d_eq = 2 * np.sqrt(hole.area / np.pi)
        c = hole.centroid
        err = float(np.hypot(c.x - y, c.y - z))
        rep.add(f"{name}.bore", "required",
                free <= VOL_TOL and d_eq <= hs["bore_diameter_max"] and err <= spec["axes"]["position_tolerance"],
                {"area_equivalent_diameter": round(d_eq, 4), "centre_error": round(err, 4),
                 "material_inside_min_diameter_mm3": round(free, 4)},
                f"{hs['bore_diameter_min']} <= d <= {hs['bore_diameter_max']}, centre within "
                f"{spec['axes']['position_tolerance']}")

        # 3. Seat and mating face along the axis, sampled at r = 2.4 (outside the bore, inside the nut).
        seats, faces = [], []
        for a in np.radians(np.arange(0, 360, 45) + 13.7):
            for x0, x1 in segments_along_x(mesh, y + 2.4 * np.cos(a), z + 2.4 * np.sin(a)):
                if x0 < xm < x1:
                    seats.append(x0)
                    faces.append(x1)
        if len(seats) < 8:
            rep.add(f"{name}.seat_and_face", "required", False, {"samples": len(seats)},
                    "solid lug between nut seat and mating face")
        else:
            s_dev = float(np.max(np.abs(np.array(seats) - seat)))
            f_dev = float(np.max(np.abs(np.array(faces) - face)))
            rep.add(f"{name}.seat_and_face", "required",
                    s_dev <= hs["nut_seat_tolerance"] and f_dev <= hs["mating_face_tolerance"],
                    {"seat_x": round(float(np.median(seats)), 4), "face_x": round(float(np.median(faces)), 4),
                     "grip_mm": round(float(np.median(faces) - np.median(seats)), 4) +
                     round(spec["tracker_side"]["outer_face_x"] - face, 4),
                     "max_seat_dev": round(s_dev, 4), "max_face_dev": round(f_dev, 4)},
                    f"seat {seat}+/-{hs['nut_seat_tolerance']}, face {face}+/-{hs['mating_face_tolerance']}")

        # 4. Nut pocket: a minimum-AF hex must fit to the required depth and be insertable from outside.
        best = None
        for clock in np.arange(0, 60, 1.0):
            v = (mmi.hex_x(hs["nut_pocket_af_min"], seat - hs["nut_pocket_min_depth"], seat - 0.02, y, z, clock)
                 ^ M).volume()
            if best is None or v < best[1]:
                best = (clock, v)
        clock = best[0]
        corridor = (mmi.hex_x(hs["nut_pocket_af_min"], mesh.bounds[0][0] - 1, seat - 0.02, y, z, clock)
                    ^ M).volume()
        widths = []
        for p in section_polygons(mesh, seat - 2.0):
            for h in p.interiors:
                hp = Polygon(h)
                if hp.contains(Point(y, z)):
                    widths.append(min_width(hp))
        af = widths[0] if widths else None
        rep.add(f"{name}.nut_pocket", "required",
                best[1] <= VOL_TOL and corridor <= VOL_TOL and af is not None and af <= hs["nut_pocket_af_max"],
                {"best_clocking_deg": float(clock), "material_in_min_hex_mm3": round(best[1], 4),
                 "material_in_insertion_corridor_mm3": round(corridor, 4),
                 "measured_af_at_seat_minus_2": None if af is None else round(af, 4)},
                f"AF {hs['nut_pocket_af_min']}..{hs['nut_pocket_af_max']}, depth >= {hs['nut_pocket_min_depth']}, "
                "open towards -X")

        # 5. Nut seat must be solid under the nut face.
        ring = mmi.hex_x(fs["nut_af"], seat, seat + 0.5, y, z, clock) - mmi.cyl_x(hs["bore_diameter"] / 2 + 0.05,
                                                                              seat - 1, seat + 1, y, z)
        fill = (ring ^ M).volume() / ring.volume()
        rep.add(f"{name}.seat_fill", "required", fill >= hs["seat_min_fill"], {"fill": round(fill, 4)},
                f">= {hs['seat_min_fill']}")

        # 6. Bearing area on the mating face.
        r = hs["bearing_radius_pivot"] if name == "pivot" else hs["bearing_radius_stop"]
        ann = mmi.cyl_x(r, face - 0.3, face, y, z) - mmi.cyl_x(hs["bore_diameter"] / 2 + 0.05, face - 1, face + 1, y, z)
        fill = (ann ^ M).volume() / ann.volume()
        rep.add(f"{name}.bearing_fill", "required", fill >= hs["bearing_min_fill"],
                {"radius": r, "fill": round(fill, 4)}, f">= {hs['bearing_min_fill']}")

        # 11. Pocket wall (recommendation), from sections through the nut height.
        walls = []
        for xs in (seat - 0.5, seat - 2.0, seat - 3.8):
            for p in section_polygons(mesh, xs):
                holes = list(p.interiors)
                for i, h in enumerate(holes):
                    if Polygon(h).contains(Point(y, z)):
                        others = [p.exterior] + [o for j, o in enumerate(holes) if j != i]
                        walls.append(min(shapely.LineString(h.coords).distance(shapely.LineString(o.coords))
                                         for o in others))
        w = round(float(min(walls)), 4) if walls else None
        rep.add(f"{name}.pocket_wall", "recommended", w is not None and w >= hs["min_pocket_wall"],
                {"min_wall": w}, f">= {hs['min_pocket_wall']}")

    # 7. Washer gap must stay empty.
    a = spec["tkz"]["sweep_deg"]
    gapzone = mmi.sweep(mmi.prism_x(face + 0.005, spec["tracker_side"]["mating_face_x"], mmi.ear_profile(spec)), -a, a)
    v = (gapzone ^ M).volume()
    rep.add("washer_gap.empty", "required", v <= VOL_TOL, {"material_mm3": round(v, 4)},
            f"no material in {face} < x <= {spec['tracker_side']['mating_face_x']} under the swept ear")

    # 8. Tracker keep-out zone.
    T = mmi.tkz(spec)
    v = (T ^ M).volume()
    gap = T.min_gap(M, 2.0)
    rep.add("tkz.no_intersection", "required", v <= VOL_TOL, {"intersection_mm3": round(v, 4)},
            f"mount outside the TKZ swept over +/-{a} deg")
    rep.add("tkz.clearance", "recommended", gap >= spec["hmd_side"]["recommended_tkz_clearance"] - 0.005,
            {"min_gap_mm": round(gap, 4)}, f">= {spec['hmd_side']['recommended_tkz_clearance']}")

    # 9. Fastener, tool and nut-insertion zones.
    for k, Z in mmi.fastener_zones(spec).items():
        v = (Z ^ M).volume()
        rep.add(f"fastener_zone.{k}", "required", v <= VOL_TOL, {"material_mm3": round(v, 4)}, "empty")

    # 10. Informational: sweep the actual R20 tracker meshes.
    if tracker_parts:
        tr = mf.Manifold.batch_boolean([mmi.to_manifold(p) for p in tracker_parts], mf.OpType.Add)
        rows = []
        for th in np.append(np.arange(-a, a, 0.5), a):
            Rt = mmi.rot_x(tr, th)
            rows.append({"theta_deg": round(float(th), 2), "intersection_mm3": round((Rt ^ M).volume(), 4),
                         "min_gap_mm": round(Rt.min_gap(M, 2.0), 4)})
        worst = min(rows, key=lambda r: r["min_gap_mm"])
        rep.add("r20_tracker_sweep", "informational", all(r["intersection_mm3"] <= VOL_TOL for r in rows),
                {"min_gap_mm": worst["min_gap_mm"], "at_theta_deg": worst["theta_deg"], "by_angle": rows},
                "reference R20 case + cover, no intersection over the mechanical range")
    return rep


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("manifest", nargs="?")
    ap.add_argument("--stl")
    ap.add_argument("--out")
    ap.add_argument("--no-reference-sweep", action="store_true")
    args = ap.parse_args()
    spec = mmi.load_spec()
    if args.manifest:
        mpath = Path(args.manifest).resolve()
        man = json.loads(mpath.read_text(encoding="utf-8"))
        stl = (mpath.parent / man["files"]["stl"]).resolve()
        matrix = man.get("mmi_to_file")
        out = Path(args.out) if args.out else mpath.parent / "verification" / "mmi-v0.1.json"
    elif args.stl:
        stl, matrix, man = Path(args.stl).resolve(), None, {}
        out = Path(args.out) if args.out else stl.with_suffix(".mmi-v0.1.json")
    else:
        ap.error("give a mount.json or --stl")
    print(f"MMI v{spec['version']} check: {stl}")
    mesh = mmi.load_mesh(stl, matrix)
    tracker = None if args.no_reference_sweep else mmi.load_reference_tracker()
    rep = verify(mesh, spec, tracker)
    failed = [c["id"] for c in rep.checks if c["status"] == "fail"]
    result = {
        "mmi_version": spec["version"],
        "scope": "CAD/mesh checks only. Printing, fit with real hardware, strength and field of view are not covered.",
        "input": {"file": str(stl.relative_to(mmi.HARDWARE)) if stl.is_relative_to(mmi.HARDWARE) else stl.name,
                  "sha256": hashlib.sha256(stl.read_bytes()).hexdigest(), "mmi_to_file": matrix,
                  "mount_id": man.get("id")},
        "tools": {"python": platform.python_version(), "trimesh": trimesh.__version__,
                  "manifold3d": getattr(mf, "__version__", "unknown"), "shapely": shapely.__version__},
        "summary": {"required_failed": failed,
                    "warnings": [c["id"] for c in rep.checks if c["status"] == "warn"],
                    "conforms": not failed},
        "checks": rep.checks,
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{'CONFORMS' if not failed else 'DOES NOT CONFORM'} to MMI v{spec['version']} -> {out}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
