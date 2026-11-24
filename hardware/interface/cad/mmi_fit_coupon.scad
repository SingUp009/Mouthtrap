// MMI v0.1 fit coupons. Print these before a full mount.
//   part = "hmd"     : the HMD-side lug only (bores + nut pockets). Bolt it to an R20 camera case.
//   part = "tracker" : the tracker-side ear only (bore + arc slot). Bolt it to an R20 CentralMount or your mount.
//   part = "both"    : both on one plate; bolt them to each other.
// Exported in print orientation: mating face down, no supports needed.
include <mmi.scad>

part = "both";        // [hmd, tracker, both]
frame = "print";      // [print, mmi]  "mmi" keeps the interface frame for checking

module hmd_coupon() difference() { mmi_hmd_lug(); mmi_hmd_cuts(); }
module tracker_coupon() mmi_tracker_ear();

// mating face (x = 0.6) on the bed, lug grows upwards (+Z)
module hmd_print() translate([0, 0, MMI_HMD_FACE_X]) rotate([0, 90, 0]) hmd_coupon();
// mating face (x = 1.0) on the bed, ear grows upwards
module tracker_print() translate([0, 0, -MMI_TRK_FACE_X]) rotate([0, -90, 0]) tracker_coupon();

if (frame == "mmi") {
    if (part != "tracker") hmd_coupon();
    if (part != "hmd") tracker_coupon();
} else {
    if (part != "tracker") hmd_print();
    if (part != "hmd") translate([0, 30, 0]) tracker_print();
}
