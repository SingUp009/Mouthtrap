# Contributing to Mouthtrap

These rules apply to everyone who changes this repository, human or AI agent. The [README](README.md) explains what Mouthtrap is and how to build it.

## Design rules

1. **Check the API before you use it.** Moderato.Effects is pinned to a preview version (`0.1.0-preview.6`). Read that version's guide and public API before writing code against it. Do not use internal or guessed APIs; the same goes for ONNX Runtime, FlashCap and the VRChat OSC format.
2. **Design ownership first.** Before you implement, decide who owns each `Resource`, where cancellation stops, and how typed errors propagate. Every camera, model, socket and stream is acquired as a resource and released however the session ends.
3. **Classify each failure before you handle it.** Moderato.Results and Moderato.Effects make the failure contract explicit; they are not a reason to replace every `throw` and `try`/`catch`. A failure the caller can expect, such as bad user input, a missing file or a lost connection, is a typed error: return `Result<T, E>`, give types whose values come from outside a `Create` or `Validate` method that returns one, and end a `Scope.Gen` block with `throw scope.Fail(error)`. A broken contract or invariant (an argument the caller should have checked, an unknown enum value, a misconfigured key list) stays an exception and becomes `Die`. Cancellation by the caller stays an `OperationCanceledException` and becomes `Interrupted`. Convert exceptions only where an operation is known to raise them, and only those types: a limited `try`/`catch` or `Effect.Try`/`Effect.TryAsync` with a typed exception at that boundary, never a catch-all that turns unknown exceptions, `ScopeFailure` or cancellation into input errors.
4. **Keep the UI thin.** The GUI and the CLI only pick inputs and show results. Decoding, preprocessing, inference, calibration, smoothing and output all belong in `Mouthtrap.Core` and the input, inference and output projects.
5. **Respect the replaceable boundaries.** Video input is abstracted by `IFrameSource`. Do not let camera, image, video or network details leak into preprocessing or inference. The same applies to `IMouthEstimator` for models and `ITrackingSink` for outputs.
6. **Change only what you need.** Keep existing, working code. Keep formatting changes out of behaviour changes.
7. **Verify for real.** A change is done when it compiles, the tests pass, and the behaviour has been run. Do not describe hardware behaviour (camera, XIAO, VRChat) as verified unless you ran it on the hardware. Say what you could not check.
8. **Reproduce before you work around.** When something fails, write down the type transitions and the scope of the change, and find the cause. If you suspect a library bug, prove it with a minimal reproduction before adding a workaround.

## Privacy

- Never commit or print Wi-Fi credentials or the stream token (`stream-token.txt`). The firmware must not contain them: they are stored on the board at run time with `firmware/xiao-wifi/provision.py`. Settings images written with `provision.py --image`, and an old `sdkconfig.wifi` with its build output, contain them too.
- Do not commit camera captures or recordings of faces. Keep local experiments in the git-ignored `.artifacts/` folder.
- Models are not part of the repository. Do not commit model weights.

## Tests

Tests follow the conventions of [Moderato.Effects](https://github.com/SingUp009/Moderato.Effects/blob/main/CONTRIBUTING.md#code-and-tests).

- Each project in `software/src` has a test project in `software/tests`. They run on xUnit v3 through Microsoft.Testing.Platform; `software/tests/Directory.Build.props` sets this up.
- Name tests `<Method>_<Condition>_<ExpectedResult>`, such as `Run_OpenFails_DoesNotReleaseUnacquiredDevice`.
- Mark the steps with `// Given`, `// When` and `// Then`. The When step calls the public API as a user would. Use `// When / Then` for a check that is a single call.
- Cover the happy path, the edge cases and the error paths. Match typed errors by their case, not by message text, unless the message is what you test.
- Control time with `FakeTimeProvider`. Use `TimerAwareTimeProvider` (in `software/tests/Shared`) to advance time only after the code under test has created its timer.
- Order concurrent events with `TaskCompletionSource` signals, never with delays. Bound every wait and pass `TestContext.Current.CancellationToken` to it.
- Tests do not replace running on hardware. Camera, XIAO and VRChat behaviour is checked by hand; say so when you could not.

## Checks

Run the same steps as CI from the repository root before you push:

```powershell
dotnet restore software/Mouthtrap.slnx --locked-mode
dotnet format style software/Mouthtrap.slnx --verify-no-changes --no-restore --severity warn
dotnet format analyzers software/Mouthtrap.slnx --verify-no-changes --no-restore --severity warn
dotnet format whitespace software/Mouthtrap.slnx --verify-no-changes --no-restore
dotnet build software/Mouthtrap.slnx -c Release --no-restore -warnaserror
dotnet test --solution software/Mouthtrap.slnx -c Release --no-build --no-restore
```

For firmware changes, run what `.github/workflows/firmware.yml` runs, in the ESP-IDF 5.5.4 environment:

```bash
firmware/build-release.sh 0.0.0-dev .artifacts/firmware
python firmware/check-size.py .artifacts/firmware/build/xiao-wifi   # and the other variants
idf.py -C firmware/xiao-wifi -B "$PWD/.artifacts/build-qemu" -D SDKCONFIG="$PWD/.artifacts/build-qemu/sdkconfig" \
    -D "SDKCONFIG_DEFAULTS=sdkconfig.defaults;test/sdkconfig.qemu" build
MOUTHTRAP_QEMU_BUILD=.artifacts/build-qemu python -m unittest discover -s firmware/xiao-wifi/test -v
```

Dependencies are locked with `packages.lock.json`. When you add or update a package, commit the updated lock files.

## Releases

Push a tag such as `v1.2.0` to prepare a release. `.github/workflows/release.yml` builds the packages twice, on separate runners and from different checkout folders, and uploads them with checksums and a provenance attestation to a **draft** release only if both builds are identical.

Publish the draft only after checking its files on hardware. Download them from the draft, not from a CI run, so that the files you check are the ones you publish:

- `xiao-wifi`: flash it, run `provision.py`, see `Camera URL` in the log, and track the mouth through the GUI from the same release. Then flash it again and check that the settings are kept.
- `xiao-uvc` and `xiao-uvc-multimode`: flash each and check that Windows lists **UVC CAM1** and that the GUI shows every mode of the variant.
- GUI: on a Windows PC without the .NET SDK, start `Mouthtrap.App.exe` from the zip, and send to VRChat.

If a check fails, delete the draft and the tag, fix the problem, and tag again.

How the packages are built:

- `.github/workflows/firmware.yml` builds the three firmware variants as a matrix whenever the firmware changes, fails when less than 10% of the app partition is free (`firmware/check-size.py`), and runs `firmware/xiao-wifi/test`: unit tests for `provision.py` and start-up tests in QEMU. Its artifacts hold the release zip, `flash_args`, `flasher_args.json`, and the ELF and map files. CI does not replace the hardware check.
- The GUI is published by `tools/release/publish_app.py` (self-contained, `win-x64`) on `windows-2025` with the SDK in `global.json`.
- The firmware is built by `firmware/build-release.sh` in the ESP-IDF image pinned by digest in `.github/workflows/build.yml` and `firmware.yml`; keep the two the same. Run the script in that image to reproduce a release's firmware byte for byte.
- Zip files are written by `tools/release/pack.py`, which fixes entry order, timestamps and permissions.
- Keep the build inputs pinned: the SDK, the ESP-IDF image, the components in `dependencies.lock`, the packages in `packages.lock.json`, the Python version and the actions by commit. Firmware builds set `CONFIG_APP_REPRODUCIBLE_BUILD`, and the firmware takes no build-time secrets, so one build serves everyone.

## Commits

Subject line:

```text
<optional gitmoji> <optional #issue> <type>(<scope>): <description>
```

- `type`, `scope` and an English description are required.
- `type` is one of `fix`, `feat`, `build`, `ci`, `docs`, `perf`, `refactor`, `test`, `style` or `chore`.
- `scope` is one word naming the responsibility you changed. If a change does not fit one word, split it into several commits.
- Mark breaking changes with `!` after the scope or a `BREAKING CHANGE:` footer.
- Write the body in English and explain why the change was made.

Keep commits small and focused:

- A feature and its regression tests go in the same commit.
- A new project goes in with its solution entry and its lock file.
- Formatting-only changes go in their own `style` commit.

The convention follows [Moderato.Effects](https://github.com/SingUp009/Moderato.Effects/blob/main/CONTRIBUTING.md#commits-and-pull-requests); see the [Moderato.Results history](https://github.com/SingUp009/Moderato.Results/commits/main/) for examples.
