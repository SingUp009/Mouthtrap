// Example: the released R20 Quest 3S mount placed in the MMI frame, with the interface overlaid.
// Preview (F5) to inspect; it renders nothing printable. The numeric check is
//   python ../../interface/tests/verify_mount.py mount.json
include <../../interface/cad/mmi.scad>

show = "overlay"; // [overlay, lug_diff]

// Inverse of mmi_to_file in mount.json (print-oriented STL -> MMI frame).
module r20_mount_mmi()
    multmatrix([[0, 0, 1, -12], [0, 1, 0, -22.09943], [-1, 0, 0, 115], [0, 0, 0, 1]])
        import("../../stl/CentralMount.stl");

if (show == "overlay") {
    color("SteelBlue") r20_mount_mmi();
    %mmi_tkz();                                   // swept tracker keep-out (+/-16.3 deg)
    color("Red", 0.5) mmi_fastener_zones();
    color("Orange") mmi_hmd_cuts();               // should sit exactly in the R20 bores and pockets
    mmi_axes();
} else {
    // Material the library lug has but R20 does not (R20 rounds the lug edges with R0.8/R0.3 fillets).
    difference() { difference() { mmi_hmd_lug(); mmi_hmd_cuts(); } r20_mount_mmi(); }
}
