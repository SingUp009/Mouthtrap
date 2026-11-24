"""Run every MMI check and refresh the recorded results.

Usage: python run_all.py [--skip-scad]
"""
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
HW = HERE.parents[1]
steps = [
    [sys.executable, "measure_r20.py"],
    [sys.executable, "verify_tracker.py"],
    [sys.executable, "verify_mount.py", str(HW / "mounts" / "quest-3s" / "mount.json")],
]
if "--skip-scad" not in sys.argv:
    steps.append([sys.executable, "test_scad.py"])
failed = []
for cmd in steps:
    print("\n$", " ".join(Path(c).name for c in cmd[1:]), flush=True)
    if subprocess.run(cmd, cwd=HERE).returncode:
        failed.append(cmd[1])
print("\nALL PASSED" if not failed else f"\nFAILED: {failed}")
sys.exit(1 if failed else 0)
