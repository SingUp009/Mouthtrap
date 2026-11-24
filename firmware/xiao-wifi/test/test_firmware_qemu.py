"""Boot the Wi-Fi firmware in Espressif's QEMU and check how it handles its stored settings.

QEMU has no camera and no Wi-Fi network, so this covers start-up up to the Wi-Fi connection. Build the
firmware with the test-only sdkconfig.qemu first, then run in the ESP-IDF environment:

    idf.py -C firmware/xiao-wifi -B build-qemu -D SDKCONFIG=build-qemu/sdkconfig \\
        -D "SDKCONFIG_DEFAULTS=sdkconfig.defaults;test/sdkconfig.qemu" build
    MOUTHTRAP_QEMU_BUILD=build-qemu python -m unittest discover -s firmware/xiao-wifi/test -v

The tests are skipped when MOUTHTRAP_QEMU_BUILD is not set.
"""

import os
import re
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import provision  # noqa: E402

BUILD = os.environ.get("MOUTHTRAP_QEMU_BUILD")
TOKEN = "0123456789abcdef0123456789abcdef"
BOOT_TIMEOUT = 60


@unittest.skipUnless(BUILD, "set MOUTHTRAP_QEMU_BUILD to a build made with test/sdkconfig.qemu")
class FirmwareStartTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(self.enterContext(tempfile.TemporaryDirectory()))

    def settings_image(self, ssid: str, password: str, token: str) -> Path:
        # write_image does not validate, so it can also store settings that provision.py refuses.
        image = self.folder / "settings.bin"
        provision.write_image(ssid, password, token, image, self.folder)
        return image

    def boot(self, settings: Path | None, expected: str) -> str:
        """Boot the firmware and return its log once a line matches expected, or fail on timeout."""
        flash = self.folder / "flash.bin"
        extra = [hex(provision.NVS_OFFSET), str(settings)] if settings else []
        subprocess.run([sys.executable, "-m", "esptool", "--chip", "esp32s3", "merge_bin",
                        "--fill-flash-size", "8MB", "-o", str(flash), "@flash_args", *extra],
                       check=True, cwd=Path(BUILD).resolve(), capture_output=True)
        log = self.folder / "serial.log"
        log.touch()
        qemu = subprocess.Popen(["qemu-system-xtensa", "-nographic", "-machine", "esp32s3", "-m", "8M",
                                 "-monitor", "none", "-serial", f"file:{log}",
                                 "-drive", f"file={flash},if=mtd,format=raw"],
                                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        try:
            deadline = time.monotonic() + BOOT_TIMEOUT
            while time.monotonic() < deadline:
                text = log.read_text(encoding="utf-8", errors="replace")
                if re.search(expected, text):
                    return text
                if qemu.poll() is not None:
                    break
                time.sleep(0.2)
            qemu.kill()
            _, errors = qemu.communicate()
            self.fail(f"The firmware did not log {expected!r}.\nSerial:\n{log.read_text(errors='replace')[-4000:]}"
                      f"\nQEMU:\n{errors.decode(errors='replace')[-2000:]}")
        finally:
            if qemu.poll() is None:
                qemu.kill()
                qemu.communicate()

    def test_start_noSettings_stopsAndAsksForProvisioning(self):
        # When
        text = self.boot(None, r"with provision\.py, then reset")
        # Then
        self.assertIn("No settings found: ESP_ERR_NVS_NOT_FOUND", text)
        self.assertNotIn("wifi:", text)

    def test_start_invalidSettings_stopsBeforeWifi(self):
        # Given a password shorter than WPA2 allows
        image = self.settings_image("net", "short", TOKEN)
        # When
        text = self.boot(image, r"with provision\.py, then reset")
        # Then
        self.assertIn("The stored settings are invalid.", text)
        self.assertNotIn("wifi:", text)

    def test_start_validSettings_startsWifi(self):
        # Given
        image = self.settings_image('My, "Net"', "password123", TOKEN)
        # When / Then
        self.boot(image, r"Settings loaded; connecting to Wi-Fi\.[\s\S]*wifi:")


if __name__ == "__main__":
    unittest.main()
