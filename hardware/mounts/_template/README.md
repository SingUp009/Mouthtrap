# Mount template

Copy this folder to `hardware/mounts/<hmd-id>/` (lower-case, hyphens, e.g. `pico-4`) and follow [`../../docs/creating-a-mount.md`](../../docs/creating-a-mount.md).

| File | What to do |
|---|---|
| `mount_template.scad` | Rename to `<hmd-id>.scad`. Replace the placeholder attachment and arm. |
| `mount.json` | Fill in the headset, files and status. |

The placeholder attachment is a plain block. It is not based on any headset and is only there so the template renders and passes the interface checks as shipped.
