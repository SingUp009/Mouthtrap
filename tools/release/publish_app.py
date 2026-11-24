"""Publish the GUI self-contained for win-x64 and pack it into a zip file for a release.

    python tools/release/publish_app.py <version> <output folder>

CI runs this on windows-2025 with the SDK in global.json. A rebuild of the same commit from a clone
of the same repository gives the same zip file byte for byte, wherever it is checked out.
"""

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

from pack import pack

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "software" / "src" / "Mouthtrap.App" / "Mouthtrap.App.csproj"
RUNTIME = "win-x64"


def run(*command: str) -> None:
    subprocess.run(command, check=True, cwd=ROOT)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("version", help="version without the leading v, such as 1.2.0")
    parser.add_argument("output", type=Path, help="folder for the zip file")
    arguments = parser.parse_args()

    name = f"mouthtrap-{arguments.version}-{RUNTIME}"
    output = arguments.output.resolve()
    stage = output / "work" / name
    shutil.rmtree(output / "work", ignore_errors=True)

    # EnableWindowsTargeting lets Linux build the Windows app to check a change; releases use Windows.
    build_properties = [] if os.name == "nt" else ["-p:EnableWindowsTargeting=true"]
    # Build from scratch, away from the bin and obj folders of earlier builds. The folder stays inside
    # the repository so that ContinuousIntegrationBuild maps its paths too.
    artifacts = ROOT / ".artifacts" / "release-build"
    shutil.rmtree(artifacts, ignore_errors=True)
    build_properties.append(f"-p:ArtifactsPath={artifacts}")
    # The App project lists win-x64 in RuntimeIdentifiers, so its lock file covers this restore.
    run("dotnet", "restore", str(PROJECT), "--locked-mode", *build_properties)
    # ContinuousIntegrationBuild maps the source paths, so the checkout folder does not end up in the files.
    run("dotnet", "publish", str(PROJECT), "-c", "Release", "-r", RUNTIME, "--self-contained", "--no-restore",
        "-o", str(stage), f"-p:Version={arguments.version}", "-p:ContinuousIntegrationBuild=true",
        *build_properties)
    shutil.copy2(ROOT / "LICENSE", stage / "LICENSE")
    models = stage / "examples" / "models"
    models.mkdir(parents=True)
    shutil.copy2(ROOT / "software" / "examples" / "models" / "babble45.json", models / "babble45.json")

    if "SOURCE_DATE_EPOCH" not in os.environ:
        commit_time = subprocess.run(["git", "log", "-1", "--format=%ct"], check=True, cwd=ROOT,
                                     capture_output=True, text=True).stdout.strip()
        os.environ["SOURCE_DATE_EPOCH"] = commit_time
    count = pack(stage, output / f"{name}.zip", name)
    shutil.rmtree(output / "work")
    shutil.rmtree(artifacts)
    print(f"Packed {count} files into {output / (name + '.zip')}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
