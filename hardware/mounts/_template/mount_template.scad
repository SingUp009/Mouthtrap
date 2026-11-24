// MMI v0.1 HMD mount template.
// Copy this folder to hardware/mounts/<your-hmd>/ and edit the three marked sections.
// Everything is modelled in the MMI frame (origin on the pivot axis, +X towards the tracker ear,
// +Y = camera direction, +Z up). See hardware/docs/creating-a-mount.md.
include <../../interface/cad/mmi.scad>

/* [Output] */
// Export "mmi" for verification (commit this STL); "print" applies print_rotate for slicing.
frame = "mmi"; // [mmi, print]
// Rotation used for printing. Record it in mount.json if you commit a print-oriented file.
print_rotate = [0, 0, 0];

/* [Interface] */
// Extra clearance around the tracker keep-out zone (mm). 0.3 or more is recommended.
clearance = 0.3;
// Outer face of the lug. Must be <= -9.7 so the nut pocket is at least 4.3 deep.
lug_back_x = -12.0;
// Nut pocket clocking: 30 = flats normal to Y, as in R20. Any clocking conforms.
// With clearance 0.3 the stop-pocket wall under the lobe is about 0.94 mm (two perimeters).
nut_clock = 30;

// ---------------------------------------------------------------------------------------
// (1) HMD ATTACHMENT - replace this placeholder with geometry that grips YOUR headset.
//     The block below is not based on any headset. Model the real attachment from your own
//     measurements, or import a surface you are allowed to use:
//         import("my_measured_surface.stl");   // do not commit third-party scans or CAD
// ---------------------------------------------------------------------------------------
pad_center = [0, -2, 24];     // where the attachment sits relative to the pivot
pad_size   = [24, 30, 3];     // x, y, z
pad_tilt   = 0;               // rotation about X, degrees

module hmd_attachment() {
    translate(pad_center) rotate([pad_tilt, 0, 0]) translate(-pad_size / 2) cube(pad_size);
}

// ---------------------------------------------------------------------------------------
// (2) ARM - connects the MMI lug to the attachment. Keep it on the -X side of the mating face
//     (x <= 0.6) near the hinge; mmi_hmd_part() trims anything that enters the keep-out.
// ---------------------------------------------------------------------------------------
arm_x = [lug_back_x, 0.6];
module arm() {
    hull() {
        mmi_along_x(arm_x[0], arm_x[1]) translate([4.5, 3]) square([15, 6], center = true);
        translate(pad_center) rotate([pad_tilt, 0, 0])
            translate([-pad_size[0] / 2, -6, -pad_size[2] / 2]) cube([pad_size[0], 12, 0.01]);
    }
}

// ---------------------------------------------------------------------------------------
// (3) Optional extras: cable clips, tether anchor, labels. Add them inside mmi_hmd_part().
// ---------------------------------------------------------------------------------------
module mount() {
    mmi_hmd_part(clearance = clearance, back_x = lug_back_x, nut_clock = nut_clock) {
        hmd_attachment();
        arm();
    }
}

if (frame == "print") rotate(print_rotate) mount();
else mount();

// Preview helpers (F5 only): the swept tracker keep-out and the axes.
if ($preview) {
    %mmi_tkz();
    mmi_axes();
}
