"""Measure the R20 hinge from the STLs in hardware/stl and record it next to the CAD nominals.

Usage: python measure_r20.py   ->  ../reference/r20_measurements.json
Nominal values are transcribed from the R20 FreeCAD generator (build_mouthtrap.py, fixed_hinge.py,
fasteners.py; not in this repository). Measured values come only from the shipped STLs.
"""
import json
import sys
from pathlib import Path

import manifold3d as mf
import numpy as np
from shapely.geometry import Point, Polygon

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mmi  # noqa: E402
from verify_mount import min_width, section_polygons, segments_along_x  # noqa: E402

NOMINAL = {
    "pivot_world_mm": [0, -116, -91],
    "hmd_lug": {"x_range": [-12.0, 0.6], "lobe_radius_pivot": 6.0, "lobe_radius_stop": 4.4,
                "axis_spacing": 9.0, "bore_diameter": 3.4, "nut_pocket_af": 5.8, "nut_pocket_depth": 6.7,
                "nut_seat_x": -5.4, "front_edge_fillet": 0.8, "back_edge_fillet": 0.3},
    "tracker_ear": {"x_range": [1.0, 8.2], "ear_radius": 5.5, "stop_sector_radius": 12.0,
                    "stop_sector_half_angle": 40.0, "bore_diameter": 3.4, "slot_radii": [7.3, 10.7],
                    "slot_end_angle": 15.0, "slot_end_radius": 1.7},
    "fasteners": {"bolt": "M3x20", "head": "r2.75 x 3.0", "nut": "AF5.5 x 4.0",
                  "pivot_pad": "silicone r5.0 x 0.4 at x 0.6..1.0", "cover_bolt_heads_y": [12.7, 15.7]},
    "source": "R20 generator scripts, 2026-11-23 delivery",
}


def hole_at(mesh, x, y, z):
    for p in section_polygons(mesh, x):
        for h in p.interiors:
            hp = Polygon(h)
            if hp.contains(Point(y, z)):
                return hp
    return None


def main():
    spec = mmi.load_spec()
    reg = json.loads((mmi.INTERFACE / "reference" / "r20_registration.json").read_text(encoding="utf-8"))
    mount = mmi.load_mesh(mmi.HARDWARE / reg["parts"]["CentralMount"]["file"],
                          reg["parts"]["CentralMount"]["mmi_to_file"])
    case, cover = mmi.load_reference_tracker()
    m = {}
    for name, (y, z) in (("pivot", (0, 0)), ("stop", (9, 0))):
        b = hole_at(mount, -2.4, y, z)
        n = hole_at(mount, -7.4, y, z)
        segs = [s for a in np.radians(np.arange(0, 360, 45) + 13.7)
                for s in segments_along_x(mount, y + 2.4 * np.cos(a), z + 2.4 * np.sin(a)) if s[0] < -2.4 < s[1]]
        m[f"hmd_{name}"] = {
            "bore_centre_yz": [round(b.centroid.x, 4), round(b.centroid.y, 4)],
            "bore_area_equivalent_diameter": round(2 * np.sqrt(b.area / np.pi), 4),
            "bore_polygon_vertices": len(b.exterior.coords) - 1,
            "nut_pocket_across_flats": round(min_width(n), 4),
            "nut_pocket_across_corners": round(max(np.hypot(*(np.array(c) - [y, z])) for c in n.exterior.coords) * 2, 4),
            "nut_seat_x": round(float(np.median([s[0] for s in segs])), 4),
            "mating_face_x": round(float(np.median([s[1] for s in segs])), 4),
        }
        out = [s for s in segments_along_x(mount, y + 3.2 * np.cos(np.radians(90)), z + 3.2) ]
        m[f"hmd_{name}"]["pocket_open_to_x_min"] = not any(s[0] < -5.5 for s in out)
    m["hmd_lug_outer_face_x"] = round(float(min(s[0] for s in segments_along_x(mount, 0, -5.5))), 4)
    eb = hole_at(case, 4.6, 0, 0)
    ear = [s for a in np.radians(np.arange(0, 360, 45) + 13.7) for s in segments_along_x(case, 3.5 * np.cos(a), 3.5 * np.sin(a))
           if s[0] < 4.6 < s[1]]
    # Slot extent along r = 9: find the angular range that is open at mid-ear.
    open_deg = [a for a in np.arange(-40, 40.01, 0.1)
                if not any(s[0] < 4.6 < s[1] for s in segments_along_x(case, 9 * np.cos(np.radians(a)),
                                                                        9 * np.sin(np.radians(a))))]
    rad = [r for r in np.arange(6.0, 12.0, 0.01) if not any(s[0] < 4.6 < s[1] for s in segments_along_x(case, r, 0))]
    m["tracker_ear"] = {
        "mating_face_x": round(float(np.median([s[0] for s in ear])), 4),
        "outer_face_x": round(float(np.median([s[1] for s in ear])), 4),
        "pivot_bore_area_equivalent_diameter": round(2 * np.sqrt(eb.area / np.pi), 4),
        "slot_open_angle_at_r9_deg": [round(min(open_deg), 1), round(max(open_deg), 1)],
        "slot_open_radius_at_0deg": [round(min(rad), 2), round(max(rad), 2)],
    }
    m["axial_gap"] = round(m["tracker_ear"]["mating_face_x"] - m["hmd_pivot"]["mating_face_x"], 4)
    m["grip_length"] = round(m["tracker_ear"]["outer_face_x"] - m["hmd_pivot"]["nut_seat_x"], 4)
    m["bolt_protrusion_past_nut"] = round(20 - m["grip_length"] - 4.0, 4)

    # Hinge sweep with the real meshes: gap by angle and the first contact angle.
    M = mmi.to_manifold(mount)
    T = mf.Manifold.batch_boolean([mmi.to_manifold(case), mmi.to_manifold(cover)], mf.OpType.Add)

    def hit(th):
        return (mmi.rot_x(T, th) ^ M).volume() > 0.01
    rows = [{"theta_deg": float(t), "min_gap_mm": round(mmi.rot_x(T, t).min_gap(M, 2.0), 4),
             "intersection_mm3": round((mmi.rot_x(T, t) ^ M).volume(), 4)} for t in np.arange(-30, 30.01, 1.0)]
    contact = {}
    for sign in (1, -1):
        lo, hi = 0.0, None
        for t in np.arange(1, 61):
            if hit(sign * t):
                hi = float(t)
                break
            lo = float(t)
        if hi is not None:
            for _ in range(20):
                mid = (lo + hi) / 2
                lo, hi = (lo, mid) if hit(sign * mid) else (mid, hi)
        contact["positive" if sign > 0 else "negative"] = None if hi is None else round(sign * lo, 3)
    slot_limit = spec["tracker_side"]["slot_end_angle"] + np.degrees((1.7 - spec["fasteners"]["shank_radius"]) / 9)
    m["hinge"] = {"slot_mechanical_limit_deg": round(float(slot_limit), 3),
                  "first_geometric_contact_deg": contact,
                  "contact_region": "case top edge near y 9..11, z -4.5 against the HMD lug below the stop lobe",
                  "sweep_by_angle": rows}
    out = {"method": "STLs in hardware/stl registered into the MMI frame (registration in r20_registration.json); "
                     "sections and axis-parallel rays with trimesh/shapely; booleans with manifold3d",
           "nominal": NOMINAL, "measured_from_stl": m}
    p = mmi.INTERFACE / "reference" / "r20_measurements.json"
    p.write_text(json.dumps(out, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in m.items() if k != "hinge"}, indent=1))
    print("hinge:", {k: v for k, v in m["hinge"].items() if k != "sweep_by_angle"})


if __name__ == "__main__":
    main()
