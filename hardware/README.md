# Hardware

Printable parts that hold the XIAO ESP32S3 Sense in front of the mouth on a VR headset. Revision R20.

The hardware has two halves joined by the **Mouthtrap Mount Interface (MMI) v0.1**:

```text
HMD-specific mount            one per headset      (mounts/)
        |  MMI v0.1: two M3 bolts, pivot + arc-slot stop
Tracker module                same for every headset (stl/)
        +-- XIAO ESP32S3 Sense with OV3660 camera
        +-- camera case with adjustable hinge, cover
```

## Supported mounts

| Headset | Mount | MMI | Designed | Test-printed | Verified on HMD |
|---|---|---|---|---|---|
| Meta Quest 3S | [`quest-3s`](mounts/quest-3s) (R20) | v0.1 | ✔ | not recorded | not recorded |

*Designed*: passes the MMI checks in CAD. *Test-printed*: printed and assembled with the tracker module. *Verified on HMD*: worn and tracking on that headset. See [`mounts/quest-3s`](mounts/quest-3s) for the warnings recorded for R20. To add a headset, see [Creating a mount](docs/creating-a-mount.md).

## Parts

| File | Part |
|---|---|
| [`CameraCaseWithAdapter.stl`](stl/CameraCaseWithAdapter.stl) | Tracker module: case for the XIAO, with the MMI hinge ear |
| [`CameraCover.stl`](stl/CameraCover.stl) | Tracker module: lid for the case |
| [`CentralMount.stl`](stl/CentralMount.stl) | Meta Quest 3S mount (MMI HMD side) |

- Binary STL, units in millimetres. These files are identical to the R20 release of 2026-11-23 (SHA-256 in [`interface/reference/r20_registration.json`](interface/reference/r20_registration.json)).
- Designed for PETG on a Bambu Lab A1 with a 0.4 mm nozzle. Mount and case: supports on, 5 mm brim, no supports inside the nut pockets. Cover: no supports, no brim.
- Only the printable meshes and the interface CAD are included. The R20 CAD sources are not in this repository, and no Quest 3S or XIAO geometry is included.

## Other parts

| Part | Specification | Qty |
|---|---|---|
| Socket head cap screw | M3 × 20, coarse thread, standard head, fully threaded | 6 |
| Nylon-insert lock nut | M3, DIN 985, 5.5 mm across flats, 4 mm high or less | 6 |
| Silicone sheet | 0.5 mm thick, cut into washers and pads | 1 |
| Hex key | 2.5 mm | 1 |

Two of the six bolts and nuts form the hinge (MMI); four close the cover.

You also need a USB-C cable and a 5 V supply, an external Wi-Fi antenna with a U.FL connector, and thin cable ties and a safety cord to hold the cables and keep the camera from falling.

## Assembly order at the hinge

1. Press the two nuts into the hex pockets of the mount from the outer side (−X). Do this first: once the case is on, its antenna pad sits in line with the pockets.
2. Put a silicone washer (about Ø10) between the mount face and the case ear.
3. Insert the pivot bolt through the round hole of the ear and the stop bolt through the arc slot, and tighten with the 2.5 mm key.
4. Set the camera angle (±15°), then tighten the stop bolt to hold it.

## Mount interface

| | |
|---|---|
| [Specification](interface/specification.md) | Frame, dimensions, fasteners, keep-out zones, conformance, versioning |
| [OpenSCAD library](interface/cad/mmi.scad) | Lug, cuts, keep-out zones and tracker ear for new designs |
| [Fit coupons](interface/cad/mmi_fit_coupon.scad) | Small test prints for both sides of the joint |
| [Mount template](mounts/_template) | Starting point for a new headset |
| [Checks](interface/tests) | Mesh checks and recorded results |
| [Creating a mount](docs/creating-a-mount.md) | Step-by-step guide for contributors |

## License

All hardware files are released under the repository's [MIT License](../LICENSE). Contributed mounts must not include third-party headset geometry unless its license allows redistribution.
