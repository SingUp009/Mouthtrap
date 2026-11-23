# XIAO ESP32S3 Sense — Wi-Fi firmware

Streams the camera of a Seeed Studio XIAO ESP32S3 Sense (OV3660) over Wi-Fi as MJPEG at `http://<ip>/stream`. This is Mouthtrap's main camera setup. For a wired camera, use the [USB webcam firmware](../xiao-uvc/README.md) instead.

- 640×480 JPEG, up to about 15 fps
- Protected by a token; requests without it get `401`
- Plain HTTP, so the video and the token are not encrypted. Use it only on a network you trust.

## You need

- ESP-IDF 5.5.4
- A 2.4 GHz WPA2-PSK network. Open, WPA2-Enterprise and WPA3-only networks do not work, and the XIAO has no 5 GHz radio.
- The PC and the XIAO on the same network. Guest networks that block traffic between devices do not work.

## 1. Create the settings

Run this from the repository root. It creates `sdkconfig.wifi` and `stream-token.txt` with a random token, and stops if either file already exists:

```powershell
$configPath = 'firmware/xiao-wifi/sdkconfig.wifi'
$tokenPath = 'firmware/xiao-wifi/stream-token.txt'
if ((Test-Path -LiteralPath $configPath) -or (Test-Path -LiteralPath $tokenPath)) { throw 'Configuration already exists; edit it instead.' }
$token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$config = (Get-Content firmware/xiao-wifi/sdkconfig.wifi.example -Raw).Replace('CONFIG_MOUTHTRAP_STREAM_TOKEN=""', "CONFIG_MOUTHTRAP_STREAM_TOKEN=""$token""")
[IO.File]::WriteAllText((Join-Path $PWD $configPath), $config, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $PWD $tokenPath), $token, [Text.UTF8Encoding]::new($false))
```

Then open `sdkconfig.wifi` and fill in `CONFIG_MOUTHTRAP_WIFI_SSID` and `CONFIG_MOUTHTRAP_WIFI_PASSWORD`.

> [!CAUTION]
> `sdkconfig.wifi`, `stream-token.txt` and the `build` folder contain your Wi-Fi password and the token. Git ignores them; never share them.

## 2. Build and flash

Load the ESP-IDF environment (for example with its `export.ps1`), then build from the repository root:

```powershell
idf.py -C firmware/xiao-wifi -B "$PWD/firmware/xiao-wifi/build" -D SDKCONFIG="$PWD/firmware/xiao-wifi/sdkconfig.wifi" -D IDF_TARGET=esp32s3 build
```

Flash it and watch the log, replacing `COM3` with your port:

```powershell
idf.py -C firmware/xiao-wifi -B "$PWD/firmware/xiao-wifi/build" -p COM3 flash monitor
```

If the board has no serial port, put it in download mode first: hold **BOOT**, press and release **RESET**, then release **BOOT**.

When it works, the log shows `Camera URL: http://<ip>/stream`.

## 3. Connect Mouthtrap

In the GUI, choose the Wi-Fi (MJPEG) input, enter the URL from the log and select `firmware/xiao-wifi/stream-token.txt` as the token file.

From the command line:

```powershell
& 'software/src/Mouthtrap.Cli/bin/Release/net11.0-windows/Mouthtrap.Cli.exe' run --source Mjpeg `
    --url http://<ip>/stream --token-file firmware/xiao-wifi/stream-token.txt --model model.json --seconds 10
```

## Troubleshooting

| Problem | What to check |
|---|---|
| The stream never starts | The firmware refuses to start if a setting is missing or invalid. The SSID must be 1–32 bytes, the password 8–63 bytes, and the token 32–120 letters, digits, `-` or `_`. |
| It does not join the network | The first connection waits up to 45 seconds, and a lost connection is retried 8 times. After that, check the settings and press **RESET**. The camera only starts once Wi-Fi is connected. |
| A second viewer cannot connect | The firmware serves one stream at a time. |
| The stream stops | A send that stalls for more than 2 seconds ends the stream; reconnect. |

## Notes

- Flashing this firmware replaces the USB webcam firmware.
- The camera keeps running until power-off, even when nobody is watching. There is no power saving.
- The camera pins, orientation and colour settings match the USB webcam firmware.
- Components: esp32-camera 2.0.15, locked in `dependencies.lock`.
