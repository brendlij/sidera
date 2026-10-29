# Development

## Build and test

```bash
dotnet build
dotnet run --project src/Sidera.Desktop
dotnet test
```

The tests need no hardware: devices are simulators or fakes of the ASCOM drivers. Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the tests of real devices, the [ASCOM Platform](https://ascom-standards.org/) 7.

## Repository layout

| Project | Contents |
|---|---|
| `src/Sidera.Core` | Device abstractions, the models of sessions, focusing, imaging, mounts and the sky |
| `src/Sidera.Runtime` | The runtime host, simulators, sequencing, autofocus, plate solving and centering |
| `src/Sidera.Alpaca` | ASCOM Alpaca over HTTP: the protocol client, discovery, `AlpacaDome` |
| `src/Sidera.Ascom` | ASCOM adapters: discovery, one STA thread per device, camera, mount, focuser, rotator, filter wheel |
| `src/Sidera.Sky` | Sky surveys (HiPS), their cache and the object catalogs of the framing workspace |
| `src/Sidera.Phd2` | PHD2 as a guider |
| `src/Sidera.Astap` | ASTAP as a plate solver |
| `src/Sidera.Desktop` | The Avalonia application (view models, views, the Help Center content) |
| `tests/` | Unit tests for each project, and opt-in tests of real devices |
| `tools/help-screenshots` | The scripts that take the pictures of the Help Center again |

## Tests of real hardware

A test of a real device runs when you name the device; its movement is left out when its gate is set to `0`, `false`, `no` or `off`.

| Variable | Meaning |
|---|---|
| `SIDERA_ASCOM_CAMERA`, `_FOCUSER`, `_MOUNT`, `_ROTATOR` | The ProgId of a real device: connects and reads it, short exposures, small focuser moves, a small slew and stop, tracking and axis moves, a rotator move of two degrees and back |
| `SIDERA_MERIDIAN_FLIP_TARGET=<ra hours>,<dec degrees>` | With `SIDERA_ASCOM_MOUNT`: the flip test slews to this target. Without it the test only reads |
| `…_OK=0` | Leaves out what moves: `SIDERA_ASCOM_MOUNT_SLEW_OK`, `_TRACKING_OK`, `_AXIS_OK`, `_SITE_WRITE_OK`, `SIDERA_ASCOM_ROTATOR_OK`, `SIDERA_ASCOM_CAMERA_STOP_OK`, `_COOLING_OK`, `SIDERA_ASCOM_AUTOFOCUS_OK`, `SIDERA_MERIDIAN_FLIP_OK`, `SIDERA_PHD2_GUIDING_OK`, `SIDERA_PHD2_DITHER_OK` |
| `SIDERA_ASCOM_TESTS=0` | Skips the tests against the ASCOM simulators (they run when the ASCOM Platform is installed) |
| `SIDERA_ALPACA_SERVER` | The Alpaca simulator (OmniSim) to test against, default `127.0.0.1:32323`. The tests refuse every host except this computer. Start it on the loopback: `ascom.alpaca.simulators.exe --urls=http://127.0.0.1:32323` |
| `SIDERA_PHD2_HOST`, `SIDERA_PHD2_PORT` | A running PHD2 (default 127.0.0.1:4400) |
| `SIDERA_ASCOM_MANUAL=1` | The tests that open a dialog and wait for a person |
| `SIDERA_ASTAP_TESTS=1` | The tests against the real ASTAP |

The application itself only reads `SIDERA_EQUIPMENT_FILE` and `SIDERA_SETTINGS_FILE` (other locations for the two files).

## Conventions

- Text the user reads follows the wording of the application; a test checks it (no internal words such as the file format).
- The format of a session file stays at version 9. The format names are `sidera-sequence` and `sidera-equipment`; files with the earlier names `astra-sequence`, `astra-equipment` and the extension `.astraseq` still open and are written with the new names when saved.
- Commits describe what changed and why.
- The pictures of the Help Center can be taken again with `tools/help-screenshots` (see its README).
