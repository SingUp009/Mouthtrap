# XIAO ESP32S3 Sense — Wi-Fi firmware

Streams the camera of a Seeed Studio XIAO ESP32S3 Sense (OV3660) over Wi-Fi as MJPEG at `http://<ip>/stream`. This is Mouthtrap's main camera setup. For a wired camera, use the [USB webcam firmware](../xiao-uvc/README.md) instead.

- 640×480 JPEG, up to about 15 fps
- Protected by a token; requests without it get `401`
- Plain HTTP, so the video and the token are not encrypted. Use it only on a network you trust.

## You need

- Python 3.10 or later, with the flashing tools: `python -m pip install -r firmware/requirements.txt` (in a release zip, `requirements.txt` is next to the firmware)
- A 2.4 GHz WPA2-PSK network. Open, WPA2-Enterprise and WPA3-only networks do not work, and the XIAO has no 5 GHz radio.
- The PC and the XIAO on the same network. Guest networks that block traffic between devices do not work.

The firmware contains no Wi-Fi settings. You flash the same firmware on any network, then store your network and a stream token on the board with `provision.py`. Flashing a new firmware keeps the stored settings.

## From the Mouthtrap GUI

The GUI does steps 1 and 2 in one go, without Python:

1. Connect the board with USB.
2. Under **Board firmware**, choose:
   - **XIAO Wi-Fi camera**;
   - the version: the latest published release, one listed with **Find releases**, or a zip file such as this folder's release zip;
   - the board's serial port.

   Then enter your 2.4 GHz network and password.
3. Press **Write to board**.

The GUI downloads the chosen release and checks it against the release's `SHA256SUMS`; it refuses a zip that holds another firmware. It then writes the firmware and the settings, and restarts the board. Then it waits up to a minute for the board to report its URL, and sets up the Wi-Fi input with that URL and the token file. The token file is the one already chosen for the Wi-Fi input, or `stream-token.txt` next to the GUI's settings. If the board does not answer, put it in download mode (below) and try again.

The steps below do the same from the command line.

## 1. Flash the firmware

Download `mouthtrap-firmware-xiao-wifi-<version>.zip` from the [releases](https://github.com/SingUp009/Mouthtrap/releases), extract it and run this in the extracted folder, replacing `COM3` with your port:

```powershell
python -m esptool --chip esp32s3 --port COM3 --before default_reset --after hard_reset write_flash "@flash_args"
```

If the board has no serial port, put it in download mode first: hold **BOOT**, press and release **RESET**, then release **BOOT**.

To build it yourself instead, load the ESP-IDF 5.5.4 environment (for example with its `export.ps1`) and run this from the repository root:

```powershell
idf.py -C firmware/xiao-wifi -B "$PWD/firmware/xiao-wifi/build" build
idf.py -C firmware/xiao-wifi -B "$PWD/firmware/xiao-wifi/build" -p COM3 flash
```

## 2. Store the settings

Run `provision.py` from the release folder, or from `firmware/xiao-wifi` in the repository:

```powershell
python provision.py --port COM3 --ssid "your network"
```

It asks for the Wi-Fi password, so the password does not end up in your shell history. It writes the settings to the board's `nvs` partition and restarts the board. The first time, it also creates `stream-token.txt` with a random token; later runs reuse that file, so Mouthtrap keeps working when you change the network. Use `--token-file` to keep the token somewhere else.

Watch the log to see the board connect, replacing `COM3` with your port:

```powershell
python -m serial.tools.miniterm COM3 115200
```

When it works, the log shows `Camera URL: http://<ip>/stream`. Leave with **Ctrl+]**.

> [!CAUTION]
> `stream-token.txt` lets anyone on your network watch the camera. Git ignores it in `firmware/xiao-wifi`; never share it. `provision.py` keeps the password only in a temporary folder that it deletes. With `--image`, it writes the settings to a file instead of the board; that file contains the password, so delete it after flashing.

### Upgrading from a build with `sdkconfig.wifi`

Earlier versions compiled the Wi-Fi settings into the firmware from `sdkconfig.wifi`. Flash the new firmware, then run `provision.py` with `--token-file stream-token.txt` pointing to your existing token so that Mouthtrap keeps working. Delete `sdkconfig.wifi` and the old `build` folder afterwards: they contain your password.

## 3. Connect Mouthtrap

In the GUI, choose the Wi-Fi (MJPEG) input, enter the URL from the log and select the `stream-token.txt` that `provision.py` created as the token file.

From the command line:

```powershell
& 'software/src/Mouthtrap.Cli/bin/Release/net11.0-windows/Mouthtrap.Cli.exe' run --source Mjpeg `
    --url http://<ip>/stream --token-file firmware/xiao-wifi/stream-token.txt --model model.json --seconds 10
```

## Troubleshooting

| Problem | What to check |
|---|---|
| The stream never starts | The firmware refuses to start if a setting is missing or invalid; the log says which. Run `provision.py` again. The SSID must be 1–32 bytes, the password 8–63 bytes, and the token 32–120 letters, digits, `-` or `_`. |
| It does not join the network | The first connection waits up to 45 seconds, and a lost connection is retried 8 times. After that, check the settings, run `provision.py` again if needed, and press **RESET**. The camera only starts once Wi-Fi is connected. |
| A second viewer cannot connect | The firmware serves one stream at a time. |
| The stream stops | A send that stalls for more than 2 seconds ends the stream; reconnect. |

## Notes

- Flashing this firmware replaces the USB webcam firmware.
- The camera keeps running until power-off, even when nobody is watching. There is no power saving.
- The camera pins, orientation and colour settings match the USB webcam firmware.
- Components: esp32-camera 2.0.15, locked in `dependencies.lock`.
- The settings live in the `nvs` partition at `0x9000` (see `partitions.csv`), namespace `mouthtrap`, keys `ssid`, `password` and `token`. Erasing the whole flash removes them.
