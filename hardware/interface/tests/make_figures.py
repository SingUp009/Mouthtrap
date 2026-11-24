"""Draw the specification figures from the real geometry. Usage: python make_figures.py"""
import json
import sys
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from shapely.geometry import Point, Polygon  # noqa: E402
from shapely.ops import unary_union  # noqa: E402
from shapely import affinity  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mmi  # noqa: E402
from verify_mount import section_polygons  # noqa: E402

FIG = mmi.INTERFACE / "figures"
FIG.mkdir(exist_ok=True)
spec = mmi.load_spec()
reg = json.loads((mmi.INTERFACE / "reference" / "r20_registration.json").read_text(encoding="utf-8"))
mount = mmi.load_mesh(mmi.HARDWARE / reg["parts"]["CentralMount"]["file"], reg["parts"]["CentralMount"]["mmi_to_file"])
case, cover = mmi.load_reference_tracker()


def draw(ax, geom, **kw):
    for g in getattr(geom, "geoms", [geom]):
        if g.is_empty:
            continue
        ax.fill(*g.exterior.xy, **kw)
        for h in g.interiors:
            ax.fill(*h.xy, color="white", lw=0)
        ax.plot(*g.exterior.xy, color=kw.get("ec", "k"), lw=0.6)


def swept(poly, a):
    return unary_union([affinity.rotate(poly, t, origin=(0, 0)) for t in np.linspace(-a, a, 131)])


a = spec["tkz"]["sweep_deg"]
fig, axs = plt.subplots(1, 2, figsize=(13, 6.5))
# Left: section at x = -3 (HMD lug) with the swept TKZ body block and ear outline.
ax = axs[0]
b = spec["tkz"]["boxes"]["body"]
body = Polygon([(b[2], b[4]), (b[3], b[4]), (b[3], b[5]), (b[2], b[5])])
draw(ax, swept(body, a), color="#d9d9d9", ec="#999", alpha=0.8)
draw(ax, body, color="#bfbfbf", ec="#666")
for p in section_polygons(mount, -3.0):
    draw(ax, p, color="#4a7fb5", ec="#1d3f63")
draw(ax, Polygon(mmi.ear_profile(spec)), color="none", ec="#d4572a")
ax.plot(*Polygon(mmi.ear_profile(spec)).exterior.xy, color="#d4572a", lw=1.2, ls="--")
for (y, z), lbl in (((0, 0), "pivot (0, 0)"), ((9, 0), "stop (9, 0)")):
    ax.plot(y, z, "k+", ms=10)
    ax.annotate(lbl, (y, z), (y - 4, z + 8 if y == 0 else z + 12), arrowprops=dict(arrowstyle="-", lw=0.5), fontsize=8)
ax.annotate("", (9, 0), (0, 0), arrowprops=dict(arrowstyle="<->", lw=0.8))
ax.text(4.5, -1.6, "9.00", ha="center", fontsize=8)
ax.set_title(f"Section x = -3 (looking along -X)\nblue: R20 HMD lug  grey: TKZ body block, light: swept ±{a}°\n"
             "orange dashed: tracker ear outline (x 1.0..8.2)", fontsize=9)
ax.set_xlabel("Y (camera direction) [mm]")
ax.set_ylabel("Z [mm]")
ax.set_xlim(-25, 30)
ax.set_ylim(-45, 20)
ax.set_aspect("equal")
ax.grid(lw=0.3)

# Right: section y = 0 through the pivot axis (XZ), mount + case + fasteners.
ax = axs[1]
for m, c in ((mount, "#4a7fb5"), (case, "#e07b39"), (cover, "#4caf6b")):
    s = m.section(plane_origin=[0, 0, 0], plane_normal=[0, 1, 0])
    if s is None:
        continue
    polys = [Polygon(d[:, [0, 2]]) for d in s.discrete if len(d) > 3]
    polys.sort(key=lambda p: -p.area)
    for p in polys:
        ax.fill(*p.exterior.xy, color=c, alpha=0.55, lw=0)
        ax.plot(*p.exterior.xy, color="k", lw=0.4)
f = spec["fasteners"]
ax.add_patch(plt.Rectangle((f["bolt_tip_x"], -1.5), f["bolt_head_seat_x"] - f["bolt_tip_x"], 3, fc="#777", alpha=0.7))
ax.add_patch(plt.Rectangle((8.2, -2.75), 3.0, 5.5, fc="#444", alpha=0.7))
ax.add_patch(plt.Rectangle((-9.4, -2.75), 4.0, 5.5, fc="#aa3", alpha=0.7))
for x, lbl in ((0.6, "HMD face 0.6"), (1.0, "tracker face 1.0"), (-5.4, "nut seat -5.4"), (8.2, "head seat 8.2")):
    ax.axvline(x, color="k", lw=0.4, ls=":")
    ax.text(x + (0.2 if x == 1.0 else -0.2), 8, lbl, rotation=90, fontsize=7, va="bottom",
            ha="left" if x == 1.0 else "right")
ax.set_title("Section y = 0 through the pivot axis\nblue: R20 mount  orange: case  green: cover  grey: M3x20  yellow: nut",
             fontsize=9)
ax.set_xlabel("X (pivot axis) [mm]")
ax.set_ylabel("Z [mm]")
ax.set_xlim(-40, 22)
ax.set_ylim(-52, 22)
ax.set_aspect("equal")
ax.grid(lw=0.3)
plt.tight_layout()
plt.savefig(FIG / "mmi_sections.svg")

# Clearance vs angle.
rows = json.loads((mmi.INTERFACE / "reference" / "r20_measurements.json").read_text())["measured_from_stl"]["hinge"]["sweep_by_angle"]
fig, ax = plt.subplots(figsize=(7, 3.2))
t = [r["theta_deg"] for r in rows]
ax.plot(t, [r["min_gap_mm"] for r in rows], "o-", ms=3)
ax.axvspan(-a, a, color="#cde", alpha=0.5, label=f"mechanical range ±{a}°")
ax.axhline(0.3, color="r", lw=0.6, ls="--", label="recommended 0.3 mm")
ax.set_xlabel("theta [deg]")
ax.set_ylabel("min gap [mm]")
ax.set_title("R20 mount vs case+cover (0.4 = axial washer gap)", fontsize=9)
ax.set_ylim(0, 0.45)
ax.legend(fontsize=7, loc="lower left")
ax.grid(lw=0.3)
plt.tight_layout()
plt.savefig(FIG / "r20_clearance_vs_angle.svg")
print("figures written to", FIG)
