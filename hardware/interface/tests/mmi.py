"""Shared geometry for MMI checks. All coordinates are in the MMI frame (mm)."""
import json
from pathlib import Path

import manifold3d as mf
import numpy as np
import trimesh

INTERFACE = Path(__file__).resolve().parents[1]
HARDWARE = INTERFACE.parent


def load_spec(version="0.1"):
    return json.loads((INTERFACE / f"mmi-v{version}.json").read_text(encoding="utf-8"))


def to_manifold(mesh):
    m = mf.Manifold(mf.Mesh(vert_properties=np.asarray(mesh.vertices, np.float32),
                            tri_verts=np.asarray(mesh.faces, np.uint32)))
    if m.status() != mf.Error.NoError:
        raise ValueError(f"mesh is not a valid manifold: {m.status()}")
    return m


def to_trimesh(man):
    m = man.to_mesh()
    return trimesh.Trimesh(np.asarray(m.vert_properties)[:, :3], np.asarray(m.tri_verts), process=False)


def load_mesh(path, matrix_to_file=None):
    """Load a mesh and bring it into the MMI frame. matrix_to_file maps MMI -> file coordinates."""
    mesh = trimesh.load(path, force="mesh")
    if matrix_to_file is not None:
        mesh.apply_transform(np.linalg.inv(np.asarray(matrix_to_file, float)))
    return mesh


def load_reference_tracker():
    """R20 case + cover from hardware/stl, registered into the MMI frame."""
    reg = json.loads((INTERFACE / "reference" / "r20_registration.json").read_text(encoding="utf-8"))
    parts = []
    for name in ("CameraCaseWithAdapter", "CameraCover"):
        entry = reg["parts"][name]
        parts.append(load_mesh(HARDWARE / entry["file"], entry["mmi_to_file"]))
    return parts


def box(b):
    x0, x1, y0, y1, z0, z1 = b
    return mf.Manifold.cube([x1 - x0, y1 - y0, z1 - z0]).translate([x0, y0, z0])


def prism_x(x0, x1, yz):
    """Extrude a polygon given in (y, z) along +X from x0 to x1."""
    e = mf.Manifold.extrude(mf.CrossSection([list(map(tuple, yz))]), x1 - x0)
    return e.transform(np.array([[0, 0, 1, x0], [1, 0, 0, 0], [0, 1, 0, 0]], float))


def cyl_x(r, x0, x1, y=0.0, z=0.0, n=128):
    return mf.Manifold.cylinder(x1 - x0, r, r, n).transform(
        np.array([[0, 0, 1, x0], [0, 1, 0, y], [-1, 0, 0, z]], float))


def hex_x(af, x0, x1, y=0.0, z=0.0, clock_deg=30.0):
    """Hex prism along X. clock_deg=30 puts flats normal to Y, as in R20."""
    r = af / np.sqrt(3)
    a = np.radians(np.arange(6) * 60 + clock_deg)
    return prism_x(x0, x1, np.c_[y + r * np.cos(a), z + r * np.sin(a)])


def rot_x(man, deg):
    R = trimesh.transformations.rotation_matrix(np.radians(deg), [1, 0, 0])
    return man.transform(R[:3, :4])


def sweep(man, a0, a1, step=0.5):
    """Sweep a convex solid about X by hulling consecutive rotated copies."""
    th = np.linspace(a0, a1, int(np.ceil((a1 - a0) / step)) + 1)  # same steps as mmi_sweep2d()
    hulls = [mf.Manifold.batch_hull([rot_x(man, t0), rot_x(man, t1)]) for t0, t1 in zip(th[:-1], th[1:])]
    return mf.Manifold.batch_boolean(hulls, mf.OpType.Add)


def ear_profile(spec, grow=0.0, n=97):
    e = spec["tkz"]["ear"]
    r, h = e["arc_radius"] + grow, e["arc_half_angle"]
    # Circumscribe the arc so the polygon never lies inside the true circle.
    r /= np.cos(np.radians(2 * h / (n - 1)) / 2)
    arc = [(r * np.cos(a), r * np.sin(a)) for a in np.radians(np.linspace(-h, h, n))]
    return ([(e["back_y"] - grow, e["bottom_z"] - grow), (arc[0][0], e["bottom_z"] - grow)]
            + arc + [(e["back_y"] - grow, e["back_top_z"] + grow)])


def tkz_blocks(spec, grow=0.0):
    t = spec["tkz"]
    blocks = {k: box([v[0] - grow, v[1] + grow, v[2] - grow, v[3] + grow, v[4] - grow, v[5] + grow])
              for k, v in t["boxes"].items()}
    x0, x1 = t["ear"]["x"]
    blocks["ear"] = prism_x(x0 - grow, x1 + grow, ear_profile(spec, grow))
    return blocks


def tkz(spec, grow=0.0, step=0.5):
    a = spec["tkz"]["sweep_deg"]
    return mf.Manifold.batch_boolean([sweep(b, -a, a, step) for b in tkz_blocks(spec, grow).values()],
                                     mf.OpType.Add)


def fastener_zones(spec):
    """Static zones in the HMD-side frame: bolts, heads, tool access and nut insertion."""
    f = spec["fasteners"]
    zones = {}
    for name in ("pivot", "stop"):
        y, z = spec["axes"][name]
        zones[f"{name}_shank"] = cyl_x(f["shank_radius"], f["bolt_tip_x"], f["bolt_head_seat_x"], y, z)
        x0 = f["bolt_head_seat_x"]
        zones[f"{name}_head_and_tool"] = cyl_x(f["tool_corridor_radius"], x0,
                                               x0 + f["head_height"] + f["tool_corridor_length"], y, z)
        zones[f"{name}_washer"] = cyl_x(spec["washer"]["zone_radius"], spec["hmd_side"]["mating_face_x"] + 1e-3,
                                        spec["tracker_side"]["mating_face_x"], y, z) if name == "pivot" else None
    return {k: v for k, v in zones.items() if v is not None}
