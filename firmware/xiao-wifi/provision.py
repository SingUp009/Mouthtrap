"""Store the Wi-Fi settings and the stream token on a XIAO running the Mouthtrap Wi-Fi firmware.

The firmware reads its settings from the "nvs" partition at run time, so one firmware build works on
every network. This script writes that partition and leaves the firmware itself untouched.
"""

import argparse
import csv
import getpass
import os
import re
import secrets
import subprocess
import sys
import tempfile
from pathlib import Path

# Keep these in sync with partitions.csv and SETTINGS_NAMESPACE in main/main.c.
NVS_OFFSET = 0x9000
NVS_SIZE = 0x6000
NAMESPACE = "mouthtrap"
TOKEN_PATTERN = re.compile(r"[A-Za-z0-9_-]{32,120}")


class SettingsError(Exception):
    """A setting that the firmware would refuse."""


def check_text(name: str, value: str, minimum: int, maximum: int) -> None:
    if any(ord(c) < 0x20 or ord(c) == 0x7F for c in value):
        raise SettingsError(f"The {name} must not contain control characters.")
    length = len(value.encode("utf-8"))
    if not minimum <= length <= maximum:
        raise SettingsError(f"The {name} must be {minimum}-{maximum} bytes; it is {length}.")


def check_token(token: str) -> None:
    if not TOKEN_PATTERN.fullmatch(token):
        raise SettingsError("The stream token must be 32-120 letters, digits, '-' or '_'.")


def read_or_create_token(path: Path) -> str:
    """Reuse the existing token so that Mouthtrap keeps working; create one the first time."""
    if path.exists():
        token = path.read_text(encoding="utf-8-sig").strip()
        check_token(token)
        print(f"Using the existing stream token in {path}.")
        return token
    token = secrets.token_hex(32)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "x", encoding="utf-8", newline="") as file:
        file.write(token)
    print(f"Created a new stream token in {path}.")
    return token


def write_image(ssid: str, password: str, token: str, image: Path, workdir: Path) -> None:
    settings = workdir / "settings.csv"
    with open(settings, "w", encoding="utf-8", newline="") as file:
        writer = csv.writer(file)
        writer.writerow(["key", "type", "encoding", "value"])
        writer.writerow([NAMESPACE, "namespace", "", ""])
        writer.writerow(["ssid", "data", "string", ssid])
        writer.writerow(["password", "data", "string", password])
        writer.writerow(["token", "data", "string", token])
    generate = [sys.executable, "-m", "esp_idf_nvs_partition_gen", "generate",
                str(settings), str(image), hex(NVS_SIZE)]
    # The generator prints only its own progress, never the values.
    subprocess.run(generate, check=True, stdout=subprocess.DEVNULL)


def flash_image(port: str, image: Path) -> None:
    command = [sys.executable, "-m", "esptool", "--chip", "esp32s3", "--port", port,
               "--before", "default_reset", "--after", "hard_reset",
               "write_flash", hex(NVS_OFFSET), str(image)]
    subprocess.run(command, check=True)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--ssid", required=True, help="2.4 GHz WPA2-PSK network name")
    parser.add_argument("--token-file", type=Path, default=Path("stream-token.txt"),
                        help="stream token for Mouthtrap; created if missing (default: stream-token.txt)")
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument("--port", help="serial port of the XIAO, such as COM3")
    target.add_argument("--image", type=Path,
                        help=f"write the {NVS_SIZE:#x}-byte settings image here instead of flashing it "
                             f"(flash it at {NVS_OFFSET:#x}); it contains the password")
    parser.add_argument("--password-env", metavar="NAME",
                        help="read the Wi-Fi password from this environment variable instead of asking")
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    try:
        check_text("SSID", arguments.ssid, 1, 32)
        if arguments.password_env:
            password = os.environ.get(arguments.password_env)
            if password is None:
                raise SettingsError(f"The environment variable {arguments.password_env} is not set.")
        else:
            password = getpass.getpass("Wi-Fi password: ")
        check_text("password", password, 8, 63)
        token = read_or_create_token(arguments.token_file)
    except (SettingsError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    # The CSV and the image hold the password; keep them in a private folder that is always removed.
    with tempfile.TemporaryDirectory(prefix="mouthtrap-") as workdir:
        image = arguments.image or Path(workdir) / "settings.bin"
        try:
            write_image(arguments.ssid, password, token, image, Path(workdir))
            if arguments.port:
                flash_image(arguments.port, image)
        except subprocess.CalledProcessError as error:
            # error.cmd is [python, "-m", module, ...]; never print the whole command line.
            print(f"error: {error.cmd[2]} failed with exit code {error.returncode}.", file=sys.stderr)
            return 1
    if arguments.port:
        print("Settings stored. The board restarts and logs the camera URL once it joins the network.")
    else:
        print(f"Settings image written to {arguments.image}. Flash it at {NVS_OFFSET:#x}, then delete it.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
