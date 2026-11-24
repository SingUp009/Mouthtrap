"""Check that a firmware build fits its app partition, and report how much room is left.

    python firmware/check-size.py <build folder>

The app must fit the smallest app partition in the build's partition table. When it runs in GitHub
Actions, it writes the sizes to the job summary.
"""

import argparse
import json
import os
import struct
import sys
from pathlib import Path

ENTRY = struct.Struct("<2sBBII16sI")  # magic, type, subtype, offset, size, label, flags
MAGIC = b"\xaa\x50"
APP_TYPE = 0


def app_partitions(table: bytes) -> list[tuple[str, int]]:
    """Read the app partitions of a binary partition table as (label, size)."""
    partitions = []
    for start in range(0, len(table) - ENTRY.size + 1, ENTRY.size):
        magic, kind, _, _, size, label, _ = ENTRY.unpack_from(table, start)
        if magic != MAGIC:
            break  # the MD5 entry or the erased end of the table
        if kind == APP_TYPE:
            partitions.append((label.rstrip(b"\0").decode("ascii"), size))
    return partitions


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("build", type=Path, help="ESP-IDF build folder")
    arguments = parser.parse_args()

    description = json.loads((arguments.build / "project_description.json").read_text(encoding="utf-8"))
    app = arguments.build / description["app_bin"]
    partitions = app_partitions((arguments.build / "partition_table" / "partition-table.bin").read_bytes())
    if not partitions:
        print("error: the partition table has no app partition.", file=sys.stderr)
        return 1
    label, capacity = min(partitions, key=lambda partition: partition[1])
    used = app.stat().st_size
    free = capacity - used
    fits = free >= 0

    print(f"{app.name}: {used:,} bytes in {label} ({capacity:,} bytes), {free:,} bytes ({100 * free / capacity:.1f}%) free")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as file:
            file.write("| Firmware | App bytes | Partition | Free | Fits |\n|---|---|---|---|---|\n"
                       f"| {arguments.build.name} | {used:,} | {label} {capacity:,} | {free:,} "
                       f"({100 * free / capacity:.1f}%) | {'yes' if fits else '**no**'} |\n")
    if not fits:
        print(f"error: the app is {-free:,} bytes larger than the {label} partition.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
