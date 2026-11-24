# Meta Quest 3S mount (R20)

The reference HMD-side implementation of MMI v0.1. The printable file stays at [`../../stl/CentralMount.stl`](../../stl/CentralMount.stl) so existing links keep working; [`mount.json`](mount.json) points to it and records the transform from the MMI frame to that print-oriented STL.

| | |
|---|---|
| Interface | MMI v0.1 — conforms ([`verification/mmi-v0.1.json`](verification/mmi-v0.1.json)) |
| Warnings | `tkz.clearance`: 0.06 mm to the keep-out zone (0.3 recommended). The real R20 case clears the mount by 0.27 mm at +15° and 0.074 mm at the +16.3° slot end. |
| Fasteners | 2 × M3×20 socket head cap screw, 2 × M3 DIN 985 nyloc nut, silicone pad 0.5 mm |
| Status | Designed. Test-printed and verified on HMD are not recorded for R20 (the R20 release notes list print, fit, load and durability as unchecked). |

The mount was fitted to a Quest 3S model supplied by the user, whose scale was inferred and not verified. That model is not in this repository and must not be added.

## Example: checking compatibility

```sh
python ../../interface/tests/verify_mount.py mount.json
```

[`r20_overlay.scad`](r20_overlay.scad) loads the STL into the MMI frame in OpenSCAD and overlays the interface cuts, fastener zones and the swept keep-out zone. With `show = "lug_diff"` it renders what the library lug has that R20 does not: 6.9 mm³, all from the R20 edge fillets.
