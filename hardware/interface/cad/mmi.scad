// Mouthtrap Mount Interface (MMI) v0.1 - OpenSCAD library
// Units: mm, degrees. Frame: see ../specification.md (origin on the pivot axis, +X towards the tracker ear).
// Values mirror ../mmi-v0.1.json; tests/test_scad.py checks that the two agree.
// Works with OpenSCAD 2021.01 and later. License: MIT (repository LICENSE).

MMI_VERSION = "0.1";

// Axes (y, z)
MMI_PIVOT = [0, 0];
MMI_STOP  = [9, 0];

// HMD side
MMI_HMD_FACE_X   = 0.6;    // mating face
MMI_NUT_SEAT_X   = -5.4;   // nut bears on this plane; fixes the M3x20 grip
MMI_BORE_D       = 3.4;
MMI_NUT_POCKET_AF = 5.8;
MMI_NUT_CLOCK    = 30;     // flats normal to Y, as in R20 (any clocking conforms)
MMI_LUG_BACK_X   = -12.0;  // R20 outer face; anything <= MMI_NUT_SEAT_X - 4.3 conforms
MMI_LUG_R_PIVOT  = 6.0;    // R20 lobe radii
MMI_LUG_R_STOP   = 4.4;

// Tracker side
MMI_TRK_FACE_X   = 1.0;
MMI_TRK_OUTER_X  = 8.2;
MMI_SLOT_R       = [7.3, 10.7];
MMI_SLOT_END     = 15;

// Fasteners
MMI_SHANK_R      = 1.5;
MMI_BOLT_TIP_X   = -11.8;
MMI_TOOL_R       = 3.0;
MMI_TOOL_X       = [8.2, 41.2];   // head (3.0) + 30 mm key access
MMI_WASHER_R     = 5.5;

// Tracker keep-out zone (TKZ), tracker frame at theta = 0: [x0, x1, y0, y1, z0, z1]
MMI_TKZ_SWEEP = 16.3;
MMI_TKZ_BOXES = [
    [-18, 18, -7.6, 13.5, -37, -7],    // body: case + cover
    [-17, 17, 13.5, 15.8, -36, -8],    // front_fasteners: cover screw heads
    [-37, -14, -7.6, 11.4, -49, 3.7],  // antenna pad and braces
    [18, 38, -6, 6.5, -31, -12]        // usb_cable allowance
];
MMI_EAR_X = [1.0, 8.2];

$fn = $preview ? 48 : 128;

// ---------- helpers ----------
module mmi_along_x(x0, x1) {             // extrude a 2D (y, z) shape along +X
    multmatrix([[0, 0, 1, x0], [1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 0, 1]])
        linear_extrude(height = x1 - x0) children();
}
module mmi_hex2d(af, clock = MMI_NUT_CLOCK) rotate(clock) circle(r = af / sqrt(3), $fn = 6);

function mmi_ear_points(n = 97) =
    let(r = 12 / cos(80 / (n - 1) / 2))
    concat([[-7.6, -9], [r * cos(-40), -9]],
           [for (i = [0 : n - 1]) let(a = -40 + 80 * i / (n - 1)) [r * cos(a), r * sin(a)]],
           [[-7.6, 4.0]]);

// Every TKZ block is a prism along X, so its sweep about X is the X-extrusion of the
// 2D region swept by its (y, z) section. Children must be convex 2D shapes.
module mmi_sweep2d(a = MMI_TKZ_SWEEP, step = 0.5) {
    n = ceil(2 * a / step);
    for (i = [0 : n - 1])
        hull() {
            rotate(-a + 2 * a * i / n) children();
            rotate(-a + 2 * a * (i + 1) / n) children();
        }
}

// ---------- HMD side ----------
// 2D outline of the R20 lug in (y, z). Replace it if your mount needs another shape;
// keep the bearing areas (r 5.0 around the pivot, r 3.5 around the stop) solid.
module mmi_hmd_lug_profile() {
    hull() {
        translate(MMI_PIVOT) circle(r = MMI_LUG_R_PIVOT);
        translate(MMI_STOP) circle(r = MMI_LUG_R_STOP);
    }
}
module mmi_hmd_lug(back_x = MMI_LUG_BACK_X) mmi_along_x(back_x, MMI_HMD_FACE_X) mmi_hmd_lug_profile();

// Bores and nut pockets. Pockets open towards -X so the nuts go in from outside.
module mmi_hmd_cuts(pocket_af = MMI_NUT_POCKET_AF, clock = MMI_NUT_CLOCK) {
    for (c = [MMI_PIVOT, MMI_STOP]) {
        mmi_along_x(-80, MMI_HMD_FACE_X + 1) translate(c) circle(d = MMI_BORE_D);
        mmi_along_x(-80, MMI_NUT_SEAT_X) translate(c) mmi_hex2d(pocket_af, clock);
    }
}

// ---------- Keep-out ----------
module mmi_tkz(grow = 0, step = 0.5) {
    for (b = MMI_TKZ_BOXES)
        mmi_along_x(b[0] - grow, b[1] + grow) mmi_sweep2d(MMI_TKZ_SWEEP, step)
            translate([b[2] - grow, b[4] - grow]) square([b[3] - b[2] + 2 * grow, b[5] - b[4] + 2 * grow]);
    mmi_along_x(MMI_EAR_X[0] - grow, MMI_EAR_X[1] + grow) mmi_sweep2d(MMI_TKZ_SWEEP, step)
        offset(delta = grow) polygon(mmi_ear_points());
}
module mmi_fastener_zones() {
    for (c = [MMI_PIVOT, MMI_STOP]) {
        mmi_along_x(MMI_BOLT_TIP_X, MMI_TOOL_X[0]) translate(c) circle(r = MMI_SHANK_R);
        mmi_along_x(MMI_TOOL_X[0], MMI_TOOL_X[1]) translate(c) circle(r = MMI_TOOL_R);
    }
    // Washer gap under the swept ear.
    mmi_along_x(MMI_HMD_FACE_X, MMI_TRK_FACE_X) mmi_sweep2d() polygon(mmi_ear_points());
}
// Everything an HMD-side part must stay out of. clearance grows the TKZ only.
module mmi_keepout(clearance = 0.3, step = 0.5) {
    mmi_tkz(clearance, step);
    mmi_fastener_zones();
}

// Wrap your HMD-specific geometry: adds the R20 lug, cuts the bores, pockets and keep-out.
module mmi_hmd_part(clearance = 0.3, back_x = MMI_LUG_BACK_X, nut_clock = MMI_NUT_CLOCK, step = 0.5) {
    difference() {
        union() { mmi_hmd_lug(back_x); children(); }
        mmi_hmd_cuts(clock = nut_clock);
        mmi_keepout(clearance, step);
    }
}

// ---------- Tracker side (for coupons and adapters) ----------
module mmi_tracker_ear_profile() {
    difference() {
        polygon(mmi_ear_points());
        translate(MMI_PIVOT) circle(d = MMI_BORE_D);
        // Arc slot for the stop bolt.
        intersection() {
            difference() { circle(r = MMI_SLOT_R[1]); circle(r = MMI_SLOT_R[0]); }
            polygon([[0, 0], [20 * cos(-MMI_SLOT_END), 20 * sin(-MMI_SLOT_END)], [20, 0],
                     [20 * cos(MMI_SLOT_END), 20 * sin(MMI_SLOT_END)]]);
        }
        for (a = [-MMI_SLOT_END, MMI_SLOT_END]) translate(9 * [cos(a), sin(a)]) circle(d = MMI_BORE_D);
    }
}
module mmi_tracker_ear() mmi_along_x(MMI_TRK_FACE_X, MMI_TRK_OUTER_X) mmi_tracker_ear_profile();

// ---------- Debug ----------
module mmi_axes(len = 20) {
    color("red")   mmi_along_x(0, len) circle(r = 0.3);
    color("green") rotate([-90, 0, 0]) cylinder(r = 0.3, h = len);
    color("blue")  cylinder(r = 0.3, h = len);
}
