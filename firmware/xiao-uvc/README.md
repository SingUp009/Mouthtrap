# XIAO ESP32S3 Sense — USB webcam firmware

Turns a Seeed Studio XIAO ESP32S3 Sense (OV3660) into a USB webcam (UVC) that you plug into the PC. There is no Wi-Fi. Mouthtrap's main setup is the [Wi-Fi firmware](../xiao-wifi/README.md); this one is for wired use.

| Build | Modes (nominal) |
|---|---|
| Default | 320×240 @ 30 fps |
| Multi-mode | 640×480 @ 15, 800×600 @ 10, 480×320 @ 30, 320×240 @ 30 fps |

## You need

- ESP-IDF v5.5.4 (`735507283d5b2f9fb363a1901172dbd9e847945d`)
- A board with 8 MB of flash and 8 MB of octal PSRAM. Check your board's flash size before flashing.

## 1. Back up the board

Read the whole flash to a file outside the repository and check its size and hash. You can restore it later as shown in [Restore the backup](#restore-the-backup).

## 2. Get the firmware

Download `mouthtrap-firmware-xiao-uvc-<version>.zip` (default) or `mouthtrap-firmware-xiao-uvc-multimode-<version>.zip` from the [releases](https://github.com/SingUp009/Mouthtrap/releases) and extract it. To flash it you need Python 3.10 or later and `python -m pip install -r requirements.txt`.

Or build it yourself: load the ESP-IDF environment, then run this in this folder:

```powershell
idf.py set-target esp32s3
idf.py build
```

For the multi-mode build, build into a separate folder:

```powershell
idf.py -B build-quality -D SDKCONFIG=build-quality/sdkconfig -D "SDKCONFIG_DEFAULTS=sdkconfig.defaults;sdkconfig.quality" build
```

## 3. Flash

1. Put the board in download mode: hold **BOOT**, press and release **RESET**, then release **BOOT**.
2. Flash it, replacing `COM3` with your port. From a release folder: `python -m esptool --chip esp32s3 --port COM3 write_flash "@flash_args"`. From your own build: `idf.py -p COM3 flash`; for the multi-mode build, use `build-quality/flash_args`.

Once the board runs as a webcam, its serial port may disappear. Use download mode again for the next flash.

## 4. Use it with Mouthtrap

Windows lists the board as **UVC CAM1**. In Mouthtrap, choose **UVC CAM1 — DirectShow** and a mode such as **640×480 [JPEG, 15fps]**. Mouthtrap does not pick a mode for this camera automatically.

## Restore the backup

Put the board in download mode, then run (replace `COM3` and the file name):

```powershell
python -m esptool --chip esp32s3 --port COM3 --baud 921600 write_flash --flash_mode keep --flash_size keep --flash_freq keep 0x0 original-flash.bin
python -m esptool --chip esp32s3 --port COM3 --baud 921600 verify_flash 0x0 original-flash.bin
```

## Notes

- JPEG quality (0–63, lower is better) is 12 for 640×480, 14 for 800×600 and 10 for the others, as in Espressif's example, so the modes differ in compression as well as size. 480×320 also has a different aspect ratio.
- Frames larger than the 75 KiB transfer buffer are dropped.
- The sensor stays on when the stream stops, so the board does not save power while idle.
- The camera pins follow [Seeed's camera guide](https://wiki.seeedstudio.com/xiao_esp32s3_camera_usage/).
- Components: esp32-camera 2.0.15 and usb_device_uvc 1.3.1, locked in `dependencies.lock`.

## License

`main/usb_webcam_main.c`, `main/camera_pin.h` and `main/Kconfig.projbuild` come from Espressif's [usb_webcam example](https://github.com/espressif/esp-iot-solution/tree/f721a40c032f6168844fe3bd321bc182e2593348/examples/usb/device/usb_webcam) and are licensed under [Apache-2.0](LICENSE); their copyright notices are kept. The only change is that the custom pin range in `Kconfig.projbuild` goes up to 48, so the XIAO's GPIO 40, 47 and 48 can be used.
