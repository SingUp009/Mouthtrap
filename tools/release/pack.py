"""Pack a folder into a zip file that is byte-for-byte the same for the same input.

The entries are sorted, and their timestamps, permissions and host system are fixed, so that the
archive depends only on the file contents. The timestamp comes from SOURCE_DATE_EPOCH when it is set.
"""

import argparse
import os
import sys
import time
import zipfile
from pathlib import Path

ZIP_EPOCH = 315532800  # 1980-01-01, the earliest time a zip file can store


def entry_time() -> tuple[int, int, int, int, int, int]:
    epoch = max(int(os.environ.get("SOURCE_DATE_EPOCH", ZIP_EPOCH)), ZIP_EPOCH)
    # Zip times have a two-second resolution.
    return time.gmtime(epoch - epoch % 2)[:6]


def pack(source: Path, output: Path, prefix: str) -> int:
    files = sorted((path for path in source.rglob("*") if path.is_file()),
                   key=lambda path: path.relative_to(source).as_posix())
    if not files:
        raise SystemExit(f"error: {source} has no files to pack.")
    timestamp = entry_time()
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w") as archive:
        for path in files:
            name = f"{prefix}/{path.relative_to(source).as_posix()}" if prefix else path.relative_to(source).as_posix()
            info = zipfile.ZipInfo(name, date_time=timestamp)
            info.create_system = 3  # Unix, whichever system packs it
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, path.read_bytes(), compresslevel=9)
    return len(files)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("source", type=Path, help="folder to pack")
    parser.add_argument("output", type=Path, help="zip file to write")
    parser.add_argument("--prefix", default="", help="folder name for the entries inside the zip")
    arguments = parser.parse_args()
    count = pack(arguments.source, arguments.output, arguments.prefix.strip("/"))
    print(f"Packed {count} files into {arguments.output}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
