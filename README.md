# Sidera

Modern astrophotography sequencing built around multi-camera imaging, automation and a fast workflow.

Sidera is an early-stage desktop application for Windows. It runs sequences across one or more imaging rigs, coordinates the
devices they share (mount, guider, focusers), and works with the ASCOM Platform for real equipment or with built-in simulators.

> **Status:** under active development. Camera and focuser have been validated against real hardware (ZWO ASI2600MC Pro, ZWO EAF).
> The mount's real movement commands (slew, abort, axis moves, tracking) are implemented and tested against simulators, but not yet
> verified on a real mount. Plate solving, PHD2 integration and an ASCOM filter wheel are not implemented yet.

## What it does today

- **Equipment:** one page with a tab per device kind (camera, mount, focuser, filter wheel, guider). Choose a driver (none, simulator,
  or an installed ASCOM driver), open its setup dialog, connect. Camera settings apply as you change them.
- **Camera:** exposures with gain, offset, binning, subframe and readout mode where the camera supports them; stop and abort;
  cooling controls; frames shown on the Imaging page.
- **Mount:** slew, tracking, sync, park, hold-to-move pad, and a Stop that is always there. A large slew asks for confirmation.
- **Focuser:** absolute and relative moves, halt, temperature.
- **Sequences:** repeat, group and parallel steps, safe points, pause and resume, dithering with guider coordination, autofocus
  with policies, and multiple rigs in one sequence. Sequences are saved as `.astraseq` files.
- **Diagnostics:** structured logging to `%LOCALAPPDATA%\Sidera\logs`.

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For real equipment: the [ASCOM Platform](https://ascom-standards.org/) 7 and the drivers of your devices

## Build and run

```bash
dotnet build
dotnet run --project src/Sidera.Desktop
dotnet test
```

The tests need no hardware: devices are simulators or fakes of the ASCOM drivers.

## Repository layout

| Project | Contents |
|---|---|
| `src/Sidera.Core` | Device abstractions, capability model, acquisition model |
| `src/Sidera.Runtime` | Runtime host, simulators, sequencing, autofocus |
| `src/Sidera.Ascom` | ASCOM adapters: discovery, one STA thread per device, camera, mount, focuser |
| `src/Sidera.Desktop` | The Avalonia application |
| `tests/` | Unit tests for each project, and opt-in integration tests against ASCOM |

## Real-hardware tests

Tests that touch real devices are skipped unless you opt in with environment variables. Reading is separate from moving:

| Variable | Enables |
|---|---|
| `SIDERA_ASCOM_TESTS=1` | Tests against the ASCOM simulators (needs the ASCOM Platform) |
| `SIDERA_ASCOM_CAMERA`, `SIDERA_ASCOM_FOCUSER`, `SIDERA_ASCOM_MOUNT` | The ProgId of the device; connects and reads it, short exposures, small focuser moves |
| `SIDERA_ASCOM_CAMERA_STOP_OK=1` | Stop-exposure test |
| `SIDERA_ASCOM_CAMERA_COOLING_OK=1` | Short cooling check |
| `SIDERA_ASCOM_MOUNT_TRACKING_OK=1`, `_SLEW_OK=1`, `_AXIS_OK=1` | Tracking toggle, a small slew and stop, a slow axis move |
| `SIDERA_ASCOM_AUTOFOCUS_OK=1` | A real autofocus run (needs a star field) |

Every physical action has its own gate: no gate, no movement. Only run mount tests when the area around the mount is clear.

## Your data

- Equipment: `%APPDATA%\Sidera\equipment.json` (override with `SIDERA_EQUIPMENT_FILE`). An equipment file from the time the project was
  called Astra (`%APPDATA%\Astra\equipment.json`) is copied on the first start; the old one is left untouched.
- Environment variables with the old `ASTRA_` prefix still work when the `SIDERA_` one of the same name is not set.
- The extension `.astraseq` and the format names `astra-sequence` and `astra-equipment` belong to the formats from before the rename.
  They are kept, so existing files keep loading.

## License

GNU General Public License v3.0, see [LICENSE](LICENSE).
