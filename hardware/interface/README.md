# Mouthtrap Mount Interface (MMI)

The shared joint between a headset-specific mount and the Mouthtrap tracker module. Current version: **v0.1 (provisional)**, based on the R20 hardware.

| File | Contents |
|---|---|
| [`specification.md`](specification.md) | The specification: frame, dimensions, fasteners, keep-out zones, conformance, versioning |
| [`mmi-v0.1.json`](mmi-v0.1.json) | The same values in machine-readable form, used by the checks |
| [`cad/mmi.scad`](cad/mmi.scad) | OpenSCAD library: lug, cuts, keep-out zones, tracker ear |
| [`cad/mmi_fit_coupon.scad`](cad/mmi_fit_coupon.scad) | Small fit coupons for both sides of the joint |
| [`tests/`](tests) | Measurement and verification scripts |
| [`tests/results/`](tests/results) | Recorded results for the R20 tracker and the OpenSCAD library |
| [`reference/`](reference) | R20 registration and measurements |
| [`figures/`](figures) | Figures used by the specification |

To make a mount for another headset, start with [`../docs/creating-a-mount.md`](../docs/creating-a-mount.md).

## Running the checks

```sh
cd hardware/interface/tests
python -m pip install -r requirements.txt
python run_all.py                 # needs OpenSCAD on PATH; add --skip-scad without it
python verify_mount.py ../../mounts/<your-hmd>/mount.json
```

Results are written as JSON so they can be compared between runs. They cover CAD geometry only; printing, fit with real hardware, strength and field of view are checked by hand.

## License

Everything in this folder is released under the repository's [MIT License](../../LICENSE). No third-party headset or board geometry is included.
