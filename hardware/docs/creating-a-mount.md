# Creating a mount for another headset

This guide takes you from an empty folder to a pull request with a new HMD mount. The tracker module (camera case, cover, XIAO, software) stays the same; you only design the part that holds it on your headset.

You need OpenSCAD (2021.01 or later; a newer build renders much faster), Python 3.11+ for the checks, a printer, and the hardware in [`../README.md`](../README.md). FreeCAD users can open `.scad` files through the OpenSCAD workbench, or model in FreeCAD and check the exported STL the same way (step 5).

## 1. Understand the joint

Read sections 1, 2 and 5 of the [specification](../interface/specification.md). In short:

- Your mount provides a **lug**: a flat face at x = 0.6, two Ø3.4 bores at (y, z) = (0, 0) and (9, 0), and two hex pockets for M3 nyloc nuts whose seat is at x = −5.4.
- The tracker module hangs on +X of that face and swings ±16.3° about the X axis. Your mount must stay out of the swept **tracker keep-out zone (TKZ)** and the bolt-head and key corridors on +X.
- The camera looks along +Y. At θ = 0 the lens is 22 mm below the pivot axis and 5.5 mm to +X.

## 2. Measure your headset

Do not guess dimensions. A mount that does not grip properly can drop the camera onto the wearer or the floor.

- Measure the area you will attach to (strap, front shell, facial interface) with calipers and a contour gauge, or make your own 3D scan.
- Decide where the pivot axis goes relative to the headset, and which way +X points. Aim the camera at the mouth: the lens should end up roughly in front of the mouth with room to tilt ±15°.
- Write down where every number came from. If you use a third-party model, check that its license allows you to use and publish derived geometry. **Never commit third-party scans or manufacturer CAD.**

## 3. Copy the template

```sh
cp -r hardware/mounts/_template hardware/mounts/<hmd-id>
mv hardware/mounts/<hmd-id>/mount_template.scad hardware/mounts/<hmd-id>/<hmd-id>.scad
```

Open `<hmd-id>.scad`. Everything is in the MMI frame, in millimetres.

## 4. Model the attachment

1. **Replace `hmd_attachment()`** with geometry that grips your headset. Build it from your measurements, or `import()` a surface you are allowed to use and model around it, placed relative to the pivot (origin).
2. **Adjust `arm()`** so it connects the lug (x −12 … 0.6 around the two axes) to your attachment. Keep it on −X of the mating face near the hinge.
3. Leave `mmi_hmd_part()` around your geometry. It adds the lug, cuts the bores and pockets, and trims anything inside the keep-out zones with a 0.3 mm margin (`clearance`).
4. Press F5. The transparent shape is the swept TKZ; nothing of yours should come near it except the lug.

If you need a different lug outline (for example to reach around a sensor), replace `mmi_hmd_lug_profile()` in your own file, keeping the bearing areas solid: radius 5.0 around the pivot and 3.5 around the stop.

## 5. Check it

Export in the MMI frame (`frame = "mmi"`, F6, Export STL) as `<hmd-id>_mmi.stl`, set `files.stl` in `mount.json`, then:

```sh
cd hardware/interface/tests
python -m pip install -r requirements.txt
python verify_mount.py ../../mounts/<hmd-id>/mount.json
```

Every `required` check must pass. The output tells you what failed and by how much:

| Failure | Usual cause |
|---|---|
| `*.bore`, `*.seat_and_face` | lug moved or resized; you edited the cuts or `lug_back_x` |
| `*.nut_pocket` | something of yours covers the pocket on −X; the nut cannot go in |
| `*.bearing_fill` | the lug outline is too small, or the clearance trim reached the face |
| `tkz.no_intersection` | your part enters the space the tracker swings through |
| `fastener_zone.*` | your part blocks a bolt head or the hex key on +X |

Commit the result file the script writes to `<hmd-id>/verification/mmi-v0.1.json`.

## 6. Print and test

1. Print the fit coupons first: [`../interface/cad/mmi_fit_coupon.scad`](../interface/cad/mmi_fit_coupon.scad) with `part = "both"`. They print face down without supports. Check that an M3 bolt passes the bores, the nuts press into the pockets, and the two coupons bolt together and turn. If the bores are tight, fix your slicer settings (or hole compensation) rather than the model.
2. Print the mount. Choose an orientation where the lug's mating face is flat and the strap or clip is strong in the direction it is loaded; set `print_rotate` if you export a print-oriented file. PETG on a 0.4 mm nozzle is what R20 uses.
3. Insert the nuts, then bolt on the tracker module with the silicone pad between the faces.
4. On the headset, check and note: it stays put when you shake your head, it does not touch your face or block the lenses or sensors, the camera sees the whole mouth across the angle range, and Mouthtrap tracks.

## 7. Record the status honestly

In `mount.json` set only what you actually did:

| Field | Set to `true` when |
|---|---|
| `designed` | the checks in step 5 pass |
| `test_printed` | you printed it and the fit in step 6.3 worked |
| `verified_on_hmd` | you wore it and step 6.4 worked; describe what you tested in `status_note` |

## 8. Open a pull request

Add to your folder:

- `<hmd-id>.scad` (editable source) and `<hmd-id>_mmi.stl`
- `mount.json` and `verification/mmi-v0.1.json`
- a short `README.md`: headset, where the dimensions came from, print settings, what you tested, photos if you have them (no faces)

Then add a row to the supported mounts table in [`../README.md`](../README.md). By contributing you agree that your files are released under the repository's MIT License.

Follow the commit format in [`AGENTS.md`](../../AGENTS.md), for example:

```text
✨ feat(hardware): add the PICO 4 mount
```
