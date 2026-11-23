# Mouthtrap

Mouth tracking for VRChat. Mouthtrap reads video of your mouth, runs a face-expression model on your PC, and sends the result to VRChat's face tracking over OSC.

It is built around a Seeed Studio XIAO ESP32S3 Sense (OV3660) streaming over Wi-Fi, but any webcam works.

## Requirements

- Windows 11
- [.NET SDK 11.0.100](https://dotnet.microsoft.com/download)
- A camera pointed at your mouth: a webcam, or a XIAO with the [Wi-Fi firmware](firmware/xiao-wifi/README.md)
- An ONNX face model (not included, see below)
- VRChat with OSC enabled (**Action Menu → Options → OSC → Enabled**) and an avatar with face tracking

## Setup

1. Get `faceModel.onnx` from [Baballonia](https://github.com/Project-Babble/Baballonia) (commit `3f85119`).
2. Copy [`software/examples/models/babble45.json`](software/examples/models/babble45.json) next to the model.
3. Build and start the app:

```powershell
dotnet build software/Mouthtrap.slnx -c Release
dotnet run --project software/src/Mouthtrap.App -c Release --no-build
```

## Use

1. Choose the input: a camera, or Wi-Fi with the stream URL and token file.
2. Under **Model definition**, choose `babble45.json`, and turn on inference.
3. Drag on the preview to crop to your mouth. Set the rotation if the camera is upside down.
4. Turn on **Send to VRChat** and press **Start**.
5. Open and close your mouth wide a few times to finish calibration.

Settings are restored on the next start. The GUI is in English or Japanese; change it under **Language**.

## Command line

The same processing without a window:

```powershell
$cli = 'software/src/Mouthtrap.Cli/bin/Release/net11.0-windows/Mouthtrap.Cli.exe'
& $cli run --saved-settings --seconds 0             # run with the GUI's settings until Ctrl+C
& $cli osc-test --osc 127.0.0.1:9000 --key JawOpen  # move the avatar's jaw without a camera
```

Run `& $cli run --help` for all options.

## Limitations

- Tongue keys are off by default: with this model they also react to an open mouth.
- The camera must stay still and close to the mouth, for example in the [Quest 3S mount](hardware/README.md).
- The Wi-Fi stream is plain HTTP. Use it only on a trusted network.

## Contributing

See [AGENTS.md](AGENTS.md).

## License

[MIT](LICENSE). Parts of [`firmware/xiao-uvc`](firmware/xiao-uvc) are under [Apache-2.0](firmware/xiao-uvc/LICENSE). Models are not included and keep their own licenses: Babble's `faceModel.onnx` is under the Babble Software Distribution License 1.0 (non-commercial).
