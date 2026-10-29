# Status and what has been tested

> **Sidera is in development and not reliable for imaging sessions.** It can move real equipment. Use it on your own initiative and at your own risk, stay at the telescope, and keep the means to stop and park within reach.

This page says what has been seen working on real equipment and what has only been tested against simulators and fakes. Everything not listed as verified should be treated as untested.

## Verified on real equipment

| Area | What | Equipment |
|---|---|---|
| Camera | Connect, exposures, image download | ZWO ASI2600 (ASCOM) |
| Focuser | Connect, absolute moves | ZWO EAF (ASCOM) |
| Mount | Connect and read, slews, **Slew & Center** converges in 2–3 attempts on both sides of the pier, a first **meridian flip** (the pier side changed) | OnStep (ASCOM), in October 2026 |
| Plate solving | ASTAP solves 5 s frames in about a second | ASI2600 with a 750 mm telescope |

The first real flip showed that the pointing of that mount was 0.5° to 1.2° off after slews (alignment of the mount, not Sidera) and that the centering after the flip failed because the mount had stopped tracking. Sidera now checks tracking, waits for the mount to settle, and retries a lost solve; **these changes have not been run on the mount again**.

## Tested against simulators and fakes only

- Guiding and dither through PHD2 (the connection and state are tested against a real PHD2 without equipment)
- Domes and roll-off roofs over ASCOM Alpaca (tested against the ASCOM Alpaca OmniSim)
- The ASCOM filter wheel (tested against a fake driver and the ASCOM filter wheel simulator)
- Rotators and rotator calibration
- **Autofocus** (the new search is tested against simulated stars only, never on a real sky)
- The meridian flip safeguards: the pier side check, tracking stopped after a failed flip, the retry from the corrected position
- Saving frames, the dashboard, the settings

## Known gaps

- The dome does not follow the mount (no slaving).
- Slews send catalog (J2000) coordinates to the mount as they are; Slew & Center corrects the difference. The meridian time does use the position of date.
- Frames of autofocus and plate solves are not saved; JPG, a second copy and moving frames after a session are not there yet.

The full list is the [backlog](backlog.md).

## Testing safely

The automated tests need no hardware. Tests of real devices run when you name the device:

```bash
SIDERA_ASCOM_MOUNT="<ProgId of your mount>" dotnet test tests/Sidera.Ascom.IntegrationTests -c Release --filter "FullyQualifiedName~Mount"
```

A test that moves a device (a small, reversible move) is left out when its gate is set to `0`, for example `SIDERA_ASCOM_MOUNT_SLEW_OK=0`. Only run them when the area around the equipment is clear. The list of variables is in [Development](development.md).

Tests of Alpaca devices only ever talk to the computer they run on. They never scan or contact other devices on your network.

## Reporting what you find

If you try Sidera on real hardware, a short report is the most useful contribution: the equipment, what you did, what happened, and the log. Please say whether it worked.
