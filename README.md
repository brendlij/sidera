# Sidera

Modern astrophotography sequencing built around multi-camera imaging, automation and a fast workflow.

Sidera is an early-stage desktop application for Windows. It runs sequences across one or more imaging rigs, coordinates the
devices they share (mount, guider, focusers), and works with the ASCOM Platform for real equipment or with built-in simulators.

> **Status:** under active development. Camera and focuser have been validated against real hardware (ZWO ASI2600MC Pro, ZWO EAF).
> The mount's real movement commands (slew, abort, axis moves, tracking) are implemented and tested against simulators, but not yet
> verified on a real mount. Guiding through PHD2 is implemented and tested against an in-memory PHD2 (and its connection and state against a real
> PHD2 without equipment); a real guided session and a real dither are not yet verified. Plate solving and an ASCOM filter wheel are not implemented yet.

## What it does today

- **Equipment:** one page with a tab per device kind (camera, mount, focuser, filter wheel, guider, rotator). Choose a driver (none, simulator,
  or an installed ASCOM driver), open its setup dialog, connect. Camera settings apply as you change them.
- **Camera:** exposures with gain, offset, binning, subframe and readout mode where the camera supports them; stop and abort;
  cooling controls; frames shown on the Imaging page.
- **Mount:** slew, tracking, sync, park, hold-to-move pad, and a Stop that is always there. A large slew asks for confirmation.
- **Focuser:** absolute and relative moves, halt, temperature.
- **Rotator:** absolute and relative moves, halt, sync and reverse where the driver supports them (the page shows only what the rotator can do). A rotator belongs to
  a rig, which is optional. Its position is the mechanical angle of the driver and is never taken for the rotation of the sky in the image: that is a calibration of the
  rig, measured by one plate solve and kept with it. Rotating to a sky angle, verifying it with a solve and centering while rotating are described below.
- **Guider:** PHD2 as the guiding backend (host and port are the settings of the device; its equipment, calibration and star stay in PHD2).
  Start, stop, pause; live guide graph in arcseconds, rolling RMS, star SNR, the settle after a dither, and what PHD2 reports about its setup.
- **Observing site:** one place for the whole application (Settings): latitude north and longitude east positive (or write N, S, E, W),
  elevation in meters. Unknown until you enter it. When a mount connects its site is compared with it; a real difference (more than 100 m)
  asks what to do (use the mount's location, send Sidera's to the mount, or keep both). Nothing is written to a mount by itself.
- **Optical train:** the focal length (and optionally aperture, pixel size, sensor pixels) of a camera's rig, on the camera page. Pixel scale,
  sensor size and field of view are derived from it and from what the camera reports, never stored. Each value is shown with its source, resolved one value at a time:
  what you entered (Manual), else what the camera reports (Device), else the small built-in camera database for a camera it knows (Sidera Camera Database, a versioned
  JSON resource in `Sidera.Core`), else unknown. The database is never copied into the rig, a manual value always wins and can be reverted, and a difference between the
  camera and the database is shown as a warning, not replaced.
- **Framing:** search an object (M31, NGC 7000, IC 434), see the field of the selected rig on a sky survey (HiPS tiles from the CDS, cached on your disk),
  drag and turn the frame, then Slew & Center on it or add it to the session. With a rotator in the rig the page offers Center & Rotate instead, and a rig without one
  compares the rotation of a plate solve with the plan and says by how many degrees to change it (Solve Again after you turned the camera). The mount is never synchronized. No survey is bundled; the rights of each survey are shown with it.
- **Sequences:** repeat, group and parallel steps, safe points, pause and resume, dithering with guider coordination, autofocus
  with policies, plate solving, and multiple rigs in one sequence. Sequences are saved as `.astraseq` files.
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
| `src/Sidera.Sky` | Sky surveys (HiPS), their disk cache and the object catalogs of the framing workspace |
| `src/Sidera.Phd2` | PHD2 as a guider: the TCP event server protocol, `Phd2Guider`; nothing of the protocol leaves this project |
| `src/Sidera.Desktop` | The Avalonia application |
| `tests/` | Unit tests for each project, and opt-in integration tests against ASCOM |

## Real-hardware tests

Tests that touch real devices are skipped unless you opt in with environment variables. Reading is separate from moving:

| Variable | Enables |
|---|---|
| `SIDERA_ASCOM_TESTS=1` | Tests against the ASCOM simulators (needs the ASCOM Platform) |
| `SIDERA_ASCOM_ROTATOR` | The ProgId of a real rotator; connects and reads it |
| `SIDERA_ASCOM_ROTATOR_OK=1` | A real rotator moves two degrees and back |
| `SIDERA_ASCOM_CAMERA`, `SIDERA_ASCOM_FOCUSER`, `SIDERA_ASCOM_MOUNT` | The ProgId of the device; connects and reads it, short exposures, small focuser moves |
| `SIDERA_ASCOM_CAMERA_STOP_OK=1` | Stop-exposure test |
| `SIDERA_ASCOM_CAMERA_COOLING_OK=1` | Short cooling check |
| `SIDERA_ASCOM_MOUNT_TRACKING_OK=1`, `_SLEW_OK=1`, `_AXIS_OK=1` | Tracking toggle, a small slew and stop, a slow axis move |
| `SIDERA_ASCOM_AUTOFOCUS_OK=1` | A real autofocus run (needs a star field) |
| `SIDERA_ASCOM_MOUNT_SITE_WRITE_OK=1` | Writes the site that the mount reports back to it, unchanged (reading and validating the site needs no gate) |
| `SIDERA_PHD2_TESTS=1` | Connects to a running PHD2 (`SIDERA_PHD2_HOST`, `SIDERA_PHD2_PORT`, default 127.0.0.1:4400) and reads its state; moves nothing |
| `SIDERA_PHD2_GUIDING_OK=1` | Starts and stops real guiding in PHD2 (its equipment must be connected, a guide star needed) |
| `SIDERA_PHD2_DITHER_OK=1` | One real dither with its settle |

Every physical action has its own gate: no gate, no movement. Only run mount tests when the area around the mount is clear.

## Your data

- Equipment: `%APPDATA%\Sidera\equipment.json` (override with `SIDERA_EQUIPMENT_FILE`). An equipment file from the time the project was
  called Astra (`%APPDATA%\Astra\equipment.json`) is copied on the first start; the old one is left untouched.
- Settings (the observing site): `%APPDATA%\Sidera\settings.json` (override with `SIDERA_SETTINGS_FILE`). Without the file, or without a
  site in it, the site is unknown; Sidera never assumes 0° 0° 0 m. Rigs keep their optical inputs in the equipment file.
- Environment variables with the old `ASTRA_` prefix still work when the `SIDERA_` one of the same name is not set.
- The extension `.astraseq` and the format names `astra-sequence` and `astra-equipment` belong to the formats from before the rename.
  They are kept, so existing files keep loading.

### Plate solving and centering

Configure ASTAP in Settings → Plate Solving. Sidera discovers `astap_cli.exe`
and installed database tiles; D50 is supported along with other ASTAP catalogs.
Executable and optional database folders, solve timeout, search radius,
downsample, blind fallback, exposure, centering tolerance and attempt limit
are persisted in the existing `settings.json` alongside the observing site.

The Plate Solve workspace captures a dedicated exposure or solves the last
frame. Hints use the selected rig's optical geometry, camera pixel size,
frame binning and dimensions, and connected mount coordinates. Unknown
coordinates stay unknown. Results include WCS-derived scale/FOV, rotation,
parity, pointing error and diagnostic focal-length estimates; rig optics
are never changed automatically.

Slew & Center holds the camera and mount resources until it finishes, uses
spherical pointing errors and tangent-plane corrections, stops at tolerance
or the attempt limit, and never syncs the mount. Real solve exposures require
`SIDERA_ASTAP_CAMERA_OK=1`; real mount centering additionally requires
`SIDERA_ASTROMETRY_CENTERING_OK=1`. Simulated devices need neither gate.
Do not enable these gates unless physical operation is intended.

The backend-neutral Plate Solve sequence step is persisted as `plateSolve`
in `.astraseq` version 7. Versions 1–6 continue to load; older Sidera versions
reject version 7 rather than silently skipping the new action.

### Rotation

The rotation of the sky in an image is the angle from the top of the image to celestial north, counterclockwise, in (−180, 180] (the `CROTA2` of a plate
solve). A rotator reports a position from 0 to 360. They are related by the calibration of the rig, `sky = ±position + offset`, which Sidera does not guess:
Calibrate (Equipment → Rotator) measures the offset with one plate solve at the current position and keeps it, with the time, for the rig. The direction is a
setting (not reversed unless chosen); a wrong direction shows at the first verified rotation, which fails instead of turning on.

- **Rotate to Angle** turns the rotator by the calibration and does not solve. Without a calibration it fails and says so.
- **Rotate & Verify** turns, solves, compares by the shortest signed angle, corrects, and repeats until the error is within the tolerance (default 0.5°, set
  in Settings → Plate Solving) or the attempts (default 4) are used up. The solve is the authority; an error that grows after a correction stops it.
- **Center & Rotate** centers the target, rotates and verifies, and centers again when turning moved the field; it ends only when both the pointing and the
  rotation tolerances hold, after at most 3 rounds.

They are sequence steps (`rotateToAngle`, `rotateAndVerify`, `centerAndRotate`, `.astraseq` version 7) and are offered by Framing and Add to Session for a rig
with a rotator. A rotation holds the rotator and every camera on it (and the mount when it centers) for its whole duration: the rotator does not move while a camera
exposes, and two rotations do not overlap. Plate Solve, Slew & Center, Rotate & Verify and Center & Rotate never synchronize the mount; only the explicit
Sync step or button does.

Safe installed-ASTAP tests use `SIDERA_ASTAP_TESTS=1`. A known FITS can be
supplied in `SIDERA_ASTAP_TEST_IMAGE`, with optional expected center in
`SIDERA_ASTAP_TEST_RA` (hours) and `SIDERA_ASTAP_TEST_DEC` (degrees). The
pixel round-trip validation supports unsigned 16-bit primary FITS images.
The production backend accepts camera frames and writes its own FITS.

## License

GNU General Public License v3.0, see [LICENSE](LICENSE).
