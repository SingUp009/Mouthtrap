"""Tests for provision.py. Run in the ESP-IDF environment, from the repository root:

    python -m unittest discover -s firmware/xiao-wifi/test -v
"""

import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import provision  # noqa: E402

TOKEN = "0123456789abcdef0123456789abcdef"


def read_settings(image: Path) -> dict[str, str]:
    """Decode an NVS image with ESP-IDF's own parser, as the firmware would read it."""
    tool = Path(os.environ["IDF_PATH"]) / "components" / "nvs_flash" / "nvs_partition_tool" / "nvs_tool.py"
    result = subprocess.run([sys.executable, str(tool), "-d", "minimal", "-f", "json", str(image)],
                            check=True, capture_output=True, text=True)
    return {entry["key"]: entry["data"] for entry in json.loads(result.stdout)
            if entry["namespace"] == provision.NAMESPACE}


def run_main(*arguments: str, password: str | None = "password123") -> tuple[int, str]:
    """Run provision.py as a user would and return its exit code and error output."""
    environment = {} if password is None else {"MOUTHTRAP_TEST_PASSWORD": password}
    errors = io.StringIO()
    with mock.patch.object(sys, "argv", ["provision.py", *arguments]), \
            mock.patch.dict(os.environ, environment), \
            mock.patch("sys.stdout", io.StringIO()), mock.patch("sys.stderr", errors):
        return provision.main(), errors.getvalue()


class CheckTextTests(unittest.TestCase):
    def test_check_text_lengthInRange_accepts(self):
        # When / Then
        provision.check_text("SSID", "x" * 32, 1, 32)

    def test_check_text_lengthCountedInUtf8Bytes_raises(self):
        # Given eleven characters that are 33 bytes in UTF-8
        ssid = "あ" * 11
        # When / Then
        with self.assertRaises(provision.SettingsError):
            provision.check_text("SSID", ssid, 1, 32)

    def test_check_text_controlCharacter_raises(self):
        # When / Then
        with self.assertRaises(provision.SettingsError):
            provision.check_text("SSID", "my\nnet", 1, 32)


class CheckTokenTests(unittest.TestCase):
    def test_check_token_allowedCharacters_accepts(self):
        # When / Then
        provision.check_token("A-z_9" * 7)

    def test_check_token_tooShort_raises(self):
        # When / Then
        with self.assertRaises(provision.SettingsError):
            provision.check_token("a" * 31)

    def test_check_token_otherCharacter_raises(self):
        # When / Then
        with self.assertRaises(provision.SettingsError):
            provision.check_token("a" * 31 + "!")


class ReadOrCreateTokenTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(self.enterContext(tempfile.TemporaryDirectory()))

    def test_read_or_create_token_missingFile_createsRandomToken(self):
        # Given
        path = self.folder / "stream-token.txt"
        # When
        with mock.patch("sys.stdout"):
            token = provision.read_or_create_token(path)
        # Then
        self.assertEqual(path.read_text(encoding="utf-8"), token)
        provision.check_token(token)

    def test_read_or_create_token_existingFile_reusesToken(self):
        # Given a token file written by PowerShell, with a BOM and a line break
        path = self.folder / "stream-token.txt"
        path.write_text("﻿" + TOKEN + "\r\n", encoding="utf-8")
        # When
        with mock.patch("sys.stdout"):
            token = provision.read_or_create_token(path)
        # Then
        self.assertEqual(token, TOKEN)

    def test_read_or_create_token_invalidFile_raises(self):
        # Given
        path = self.folder / "stream-token.txt"
        path.write_text("short", encoding="utf-8")
        # When / Then
        with self.assertRaises(provision.SettingsError):
            provision.read_or_create_token(path)


class MainTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.token_file = self.folder / "stream-token.txt"
        self.token_file.write_text(TOKEN, encoding="utf-8")
        self.image = self.folder / "settings.bin"

    def test_main_image_writesSettingsTheFirmwareReads(self):
        # Given an SSID that needs CSV quoting
        ssid = 'My, "Net"'
        # When
        code, _ = run_main("--ssid", ssid, "--password-env", "MOUTHTRAP_TEST_PASSWORD",
                        "--token-file", str(self.token_file), "--image", str(self.image))
        # Then
        self.assertEqual(code, 0)
        self.assertEqual(self.image.stat().st_size, provision.NVS_SIZE)
        self.assertEqual(read_settings(self.image), {"ssid": ssid, "password": "password123", "token": TOKEN})

    def test_main_passwordTooShort_returnsInputErrorWithoutImage(self):
        # When
        code, _ = run_main("--ssid", "net", "--password-env", "MOUTHTRAP_TEST_PASSWORD",
                           "--token-file", str(self.token_file), "--image", str(self.image), password="short")
        # Then
        self.assertEqual(code, 2)
        self.assertFalse(self.image.exists())

    def test_main_passwordVariableMissing_returnsInputError(self):
        # When
        code, _ = run_main("--ssid", "net", "--password-env", "MOUTHTRAP_TEST_UNSET",
                           "--token-file", str(self.token_file), "--image", str(self.image), password=None)
        # Then
        self.assertEqual(code, 2)

    def test_main_flashFails_returnsToolErrorAndDeletesSecrets(self):
        # Given esptool failing, and the real temporary folders recorded
        folders = []
        real = provision.tempfile.TemporaryDirectory

        def recording(**options):
            folder = real(**options)
            folders.append(Path(folder.name))
            return folder

        flash_error = subprocess.CalledProcessError(2, [sys.executable, "-m", "esptool", "--port", "secret"])
        with mock.patch.object(provision.tempfile, "TemporaryDirectory", recording), \
                mock.patch.object(provision, "flash_image", side_effect=flash_error):
            # When
            code, errors = run_main("--ssid", "net", "--password-env", "MOUTHTRAP_TEST_PASSWORD",
                                    "--token-file", str(self.token_file), "--port", "COM99")
        # Then the settings image was written to a folder that is gone, and no command line was printed
        self.assertEqual(code, 1)
        self.assertEqual(len(folders), 1)
        self.assertFalse(folders[0].exists())
        self.assertIn("esptool failed", errors)
        self.assertNotIn("secret", errors)


if __name__ == "__main__":
    unittest.main()
